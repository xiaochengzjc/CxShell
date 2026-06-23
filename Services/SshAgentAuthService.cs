using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChiXueSsh.Models;
using Renci.SshNet;
using SshNet.Agent;

namespace ChiXueSsh.Services;

public static class SshAgentAuthService
{
    public static IReadOnlyList<AuthenticationMethod> CreateAuthenticationMethods(
        SessionInfo session,
        string? password)
    {
        var methods = new List<AuthenticationMethod>();

        if (CanUseAgent(session))
            methods.Add(CreateAgentAuthentication(session.Username));

        if (session.AuthMethod == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(session.PrivateKeyPath))
        {
            var keyFile = new PrivateKeyFile(ExpandPath(session.PrivateKeyPath));
            methods.Add(new PrivateKeyAuthenticationMethod(session.Username, keyFile));
        }
        else if (!CanUseAgent(session))
        {
            methods.Add(new PasswordAuthenticationMethod(session.Username, password ?? string.Empty));
        }

        if (methods.Count == 0)
            methods.Add(new PasswordAuthenticationMethod(session.Username, password ?? string.Empty));

        return methods;
    }

    public static bool ShouldPromptForPassword(SessionInfo session)
    {
        return session.AuthMethod == AuthMethod.Password &&
               !CanUseAgent(session) &&
               !PasswordEncryptionService.HasSavedPassword(session.Password);
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

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~"))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Path.GetFullPath(path);
    }
}
