using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CxShell.Models;
using Renci.SshNet;
using SshNet.Agent;

namespace CxShell.Services;

public static class SshAgentAuthService
{
    public static IReadOnlyList<AuthenticationMethod> CreateAuthenticationMethods(
        SessionInfo session,
        string? password)
    {
        return CreateAuthenticationMethods(
            session.Username,
            password,
            session.AuthMethod,
            session.PrivateKeyPath,
            ResolvePrivateKeyPassphrase(session.RuntimePrivateKeyPassphrase, session.PrivateKeyPassphrase),
            CanUseAgent(session),
            addPasswordForPasswordAuth: !CanUseAgent(session));
    }

    public static IReadOnlyList<AuthenticationMethod> CreateAuthenticationMethods(
        string username,
        string? password,
        AuthMethod authMethod,
        string? privateKeyPath,
        string? privateKeyPassphrase,
        bool useAgent,
        bool addPasswordForPasswordAuth = true)
    {
        var methods = new List<AuthenticationMethod>();

        if (useAgent)
            methods.Add(CreateAgentAuthentication(username));

        if (authMethod == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var expandedPath = ExpandPath(privateKeyPath);
            var keyFile = string.IsNullOrEmpty(privateKeyPassphrase)
                ? new PrivateKeyFile(expandedPath)
                : new PrivateKeyFile(expandedPath, privateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }
        else if (addPasswordForPasswordAuth)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password ?? string.Empty));
        }

        if (methods.Count == 0)
            methods.Add(new PasswordAuthenticationMethod(username, password ?? string.Empty));

        return methods;
    }

    public static bool ShouldPromptForPassword(SessionInfo session)
    {
        return session.AuthMethod == AuthMethod.Password &&
               !CanUseAgent(session) &&
               !PasswordEncryptionService.HasSavedPassword(session.Password);
    }

    public static bool HasPrivateKeyPassphrase(SessionInfo session)
    {
        return !string.IsNullOrEmpty(ResolvePrivateKeyPassphrase(
            session.RuntimePrivateKeyPassphrase,
            session.PrivateKeyPassphrase));
    }

    public static bool HasPrivateKeyPassphrase(ProxySettings proxy)
    {
        return !string.IsNullOrEmpty(ResolvePrivateKeyPassphrase(
            proxy.RuntimePrivateKeyPassphrase,
            proxy.PrivateKeyPassphrase));
    }

    public static string ResolvePrivateKeyPassphrase(string? runtimePassphrase, string? savedPassphrase)
    {
        if (!string.IsNullOrEmpty(runtimePassphrase))
            return runtimePassphrase;

        return PasswordEncryptionService.Decrypt(savedPassphrase);
    }

    public static bool RequiresPrivateKeyPassphrase(string? privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath))
            return false;

        try
        {
            var path = ExpandPath(privateKeyPath);
            if (!File.Exists(path))
                return false;

            var text = File.ReadAllText(path);
            if (text.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("DEK-Info:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var puttyEncryption = text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Encryption:", StringComparison.OrdinalIgnoreCase));
            if (puttyEncryption != null)
            {
                var value = puttyEncryption[(puttyEncryption.IndexOf(':') + 1)..].Trim();
                return !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
            }

            return OpenSshKeyUsesEncryption(text);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanUseAgent(SessionInfo session)
    {
        return session.SshUseXagent && session.Protocol is SessionProtocol.SSH or SessionProtocol.SFTP;
    }

    private static PrivateKeyAuthenticationMethod CreateAgentAuthentication(string username)
    {
        var errors = new List<string>();

        foreach (var agentFactory in CreateAgentFactories())
        {
            try
            {
                var identities = agentFactory().RequestIdentities();
                if (identities.Length > 0)
                    return new PrivateKeyAuthenticationMethod(username, identities);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        var detail = errors.Count == 0 ? "No agent identities were found." : string.Join("; ", errors.Distinct());
        throw new InvalidOperationException($"SSH agent authentication failed: {detail}");
    }

    internal static IEnumerable<Func<SshAgent>> CreateAgentFactories()
    {
        var sshAuthSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(sshAuthSock))
            yield return () => new SshAgent(sshAuthSock);

        yield return () => new SshAgent();
        yield return () => new Pageant();
    }

    private static bool OpenSshKeyUsesEncryption(string text)
    {
        if (!text.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            return false;

        var base64 = string.Concat(text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
        if (string.IsNullOrWhiteSpace(base64))
            return false;

        var bytes = Convert.FromBase64String(base64);
        var magic = Encoding.ASCII.GetBytes("openssh-key-v1\0");
        if (bytes.Length <= magic.Length || !bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
            return false;

        var offset = magic.Length;
        var cipherName = ReadSshString(bytes, ref offset);
        return !string.IsNullOrWhiteSpace(cipherName) &&
               !string.Equals(cipherName, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSshString(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length)
            return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        if (length > int.MaxValue || offset + length > bytes.Length)
            return null;

        var value = Encoding.ASCII.GetString(bytes, offset, (int)length);
        offset += (int)length;
        return value;
    }

    private static string ExpandPath(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Path.GetFullPath(path);
    }
}
