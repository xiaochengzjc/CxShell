using System;
using System.Text;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public sealed class CommandLineLaunchOptions
{
    public bool HasCommand { get; private init; }
    public bool OpenSessionManager { get; private init; }
    public bool ShowAbout { get; private init; }
    public bool NewWindow { get; private init; }
    public bool ForceAuthPrompt { get; private init; }
    public bool ShowSessionProperties { get; private init; }
    public string? NewTabName { get; private init; }
    public string? SavedSessionPath { get; private init; }
    public string? Token { get; private init; }
    public string? TokenServer { get; private init; }
    public CommandLineSessionRequest? SessionRequest { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static CommandLineLaunchOptions Parse(IReadOnlyList<string>? args)
    {
        if (args == null || args.Count == 0)
            return new CommandLineLaunchOptions();

        var hasCommand = false;
        var openSessionManager = false;
        var showAbout = false;
        var newWindow = false;
        var forceAuthPrompt = false;
        var showSessionProperties = false;
        string? newTabName = null;
        string? savedSessionPath = null;
        string? url = null;
        string? token = null;
        string? tokenServer = null;
        var jumpHosts = new List<string>();
        string? errorMessage = null;

        for (var index = 0; index < args.Count; index++)
        {
            var current = NormalizeArgument(args[index]);
            if (string.IsNullOrWhiteSpace(current))
                continue;

            var option = current;
            string? inlineValue = null;
            var equalsIndex = current.IndexOf('=');
            if (equalsIndex > 0)
            {
                option = current[..equalsIndex];
                inlineValue = current[(equalsIndex + 1)..];
            }

            switch (option.ToLowerInvariant())
            {
                case "-url":
                case "--url":
                    hasCommand = true;
                    url = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-newtab":
                case "--newtab":
                    hasCommand = true;
                    newTabName = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-newwin":
                case "--newwin":
                    hasCommand = true;
                    newWindow = true;
                    break;

                case "-open":
                case "--open":
                    hasCommand = true;
                    openSessionManager = true;
                    break;

                case "-about":
                case "--about":
                    hasCommand = true;
                    showAbout = true;
                    break;

                case "-authprompt":
                case "--authprompt":
                    hasCommand = true;
                    forceAuthPrompt = true;
                    break;

                case "-j":
                case "--jump-host":
                case "--jumphost":
                    hasCommand = true;
                    jumpHosts.Add(NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index)));
                    break;

                case "-setviewer":
                case "--setviewer":
                    hasCommand = true;
                    _ = inlineValue ?? ReadNextValue(args, ref index);
                    break;

                case "-encryurl":
                case "--encryurl":
                    hasCommand = true;
                    url = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-token":
                case "--token":
                    hasCommand = true;
                    token = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-token-server":
                case "--token-server":
                case "-token-endpoint":
                case "--token-endpoint":
                case "-bastion-token-endpoint":
                case "--bastion-token-endpoint":
                    hasCommand = true;
                    tokenServer = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-prop":
                case "--prop":
                    hasCommand = true;
                    showSessionProperties = true;
                    savedSessionPath = NormalizeArgument(inlineValue ?? ReadNextValue(args, ref index));
                    break;

                case "-folder":
                case "--folder":
                case "-create":
                case "--create":
                    hasCommand = true;
                    _ = inlineValue ?? ReadNextValue(args, ref index);
                    errorMessage ??= $"{option} is not supported yet.";
                    break;

                default:
                    if (!option.StartsWith("-", StringComparison.Ordinal))
                    {
                        hasCommand = true;
                        savedSessionPath ??= current;
                    }
                    break;
            }
        }

        if (showSessionProperties && string.IsNullOrWhiteSpace(savedSessionPath) && errorMessage == null)
            errorMessage = "-prop requires session_path.";

        CommandLineSessionRequest? sessionRequest = null;
        if (!string.IsNullOrWhiteSpace(url) && errorMessage == null)
        {
            if (!TryParseSessionUrl(url, newTabName, forceAuthPrompt, out sessionRequest, out var parseError))
                errorMessage = parseError;
        }
        if (jumpHosts.Count > 0 && errorMessage == null)
        {
            if (sessionRequest == null)
            {
                errorMessage = "-J requires -url.";
            }
            else if (!TryApplyJumpHosts(sessionRequest.Session, jumpHosts, out var jumpError))
            {
                errorMessage = jumpError;
            }
        }

        return new CommandLineLaunchOptions
        {
            HasCommand = hasCommand || sessionRequest != null,
            OpenSessionManager = openSessionManager,
            ShowAbout = showAbout,
            NewWindow = newWindow,
            ForceAuthPrompt = forceAuthPrompt,
            ShowSessionProperties = showSessionProperties,
            NewTabName = newTabName,
            SavedSessionPath = savedSessionPath,
            Token = token,
            TokenServer = tokenServer,
            SessionRequest = sessionRequest,
            ErrorMessage = errorMessage
        };
    }

    public static CommandLineLaunchOptions ParseTokenPayload(
        string payload,
        CommandLineLaunchOptions parentOptions)
    {
        var openSessionManager = parentOptions.OpenSessionManager;
        var showAbout = parentOptions.ShowAbout;
        var newWindow = parentOptions.NewWindow;
        var forceAuthPrompt = parentOptions.ForceAuthPrompt;
        var showSessionProperties = parentOptions.ShowSessionProperties;
        var newTabName = parentOptions.NewTabName;
        string? savedSessionPath = null;
        string? url = null;
        var jumpHosts = new List<string>();
        string? errorMessage = null;

        if (!TryApplyToken(
                payload,
                ref url,
                ref newTabName,
                ref savedSessionPath,
                ref showSessionProperties,
                ref openSessionManager,
                ref showAbout,
                ref newWindow,
                ref forceAuthPrompt,
                jumpHosts,
                out var tokenError))
        {
            errorMessage = tokenError;
        }

        CommandLineSessionRequest? sessionRequest = null;
        if (!string.IsNullOrWhiteSpace(url) && errorMessage == null)
        {
            if (!TryParseSessionUrl(url, newTabName, forceAuthPrompt, out sessionRequest, out var parseError))
                errorMessage = parseError;
        }

        if (jumpHosts.Count > 0 && errorMessage == null)
        {
            if (sessionRequest == null)
            {
                errorMessage = "Token jumpHost requires url or host.";
            }
            else if (!TryApplyJumpHosts(sessionRequest.Session, jumpHosts, out var jumpError))
            {
                errorMessage = jumpError;
            }
        }

        return new CommandLineLaunchOptions
        {
            HasCommand = true,
            OpenSessionManager = openSessionManager,
            ShowAbout = showAbout,
            NewWindow = newWindow,
            ForceAuthPrompt = forceAuthPrompt,
            ShowSessionProperties = showSessionProperties,
            NewTabName = newTabName,
            SavedSessionPath = savedSessionPath,
            SessionRequest = sessionRequest,
            ErrorMessage = errorMessage
        };
    }

    private static bool TryApplyToken(
        string rawToken,
        ref string? url,
        ref string? newTabName,
        ref string? savedSessionPath,
        ref bool showSessionProperties,
        ref bool openSessionManager,
        ref bool showAbout,
        ref bool newWindow,
        ref bool forceAuthPrompt,
        List<string> jumpHosts,
        out string? errorMessage)
    {
        errorMessage = null;
        var tokenText = DecodeTokenText(rawToken);
        if (string.IsNullOrWhiteSpace(tokenText))
        {
            errorMessage = "Missing -token value.";
            return false;
        }

        tokenText = NormalizeArgument(tokenText);
        if (LooksLikeSessionUrl(tokenText))
        {
            url = tokenText;
            return true;
        }

        if (!tokenText.StartsWith('{'))
        {
            savedSessionPath ??= tokenText;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(tokenText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Invalid -token payload.";
                return false;
            }

            if (TryGetString(root, "url", out var tokenUrl) ||
                TryGetString(root, "encryurl", out tokenUrl))
            {
                url = tokenUrl;
            }
            else if (TryBuildTokenUrl(root, out tokenUrl, out var buildError))
            {
                url = tokenUrl;
            }
            else if (!string.IsNullOrWhiteSpace(buildError))
            {
                errorMessage = buildError;
                return false;
            }

            if (string.IsNullOrWhiteSpace(newTabName) &&
                (TryGetString(root, "newtab", out var tokenTabName) ||
                 TryGetString(root, "newTab", out tokenTabName) ||
                 TryGetString(root, "tabName", out tokenTabName) ||
                 TryGetString(root, "name", out tokenTabName)))
            {
                newTabName = tokenTabName;
            }

            if (string.IsNullOrWhiteSpace(savedSessionPath) &&
                (TryGetString(root, "sessionPath", out var tokenSessionPath) ||
                 TryGetString(root, "session", out tokenSessionPath)))
            {
                savedSessionPath = tokenSessionPath;
            }

            if (TryGetBoolean(root, "prop", out var tokenProp) ||
                TryGetBoolean(root, "properties", out tokenProp) ||
                TryGetBoolean(root, "showProperties", out tokenProp))
            {
                showSessionProperties = tokenProp;
            }

            if (TryGetBoolean(root, "open", out var tokenOpen))
                openSessionManager = tokenOpen;

            if (TryGetBoolean(root, "about", out var tokenAbout))
                showAbout = tokenAbout;

            if (TryGetBoolean(root, "newwin", out var tokenNewWindow) ||
                TryGetBoolean(root, "newWindow", out tokenNewWindow))
            {
                newWindow = tokenNewWindow;
            }

            if (TryGetBoolean(root, "authPrompt", out var tokenAuthPrompt) ||
                TryGetBoolean(root, "authprompt", out tokenAuthPrompt))
            {
                forceAuthPrompt = tokenAuthPrompt;
            }

            AddTokenJumpHosts(root, jumpHosts);
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Invalid -token JSON: {ex.Message}";
            return false;
        }
    }

    private static string DecodeTokenText(string rawToken)
    {
        rawToken = NormalizeArgument(rawToken);
        if (string.IsNullOrWhiteSpace(rawToken))
            return string.Empty;

        if (rawToken.StartsWith('{') || LooksLikeSessionUrl(rawToken))
            return rawToken;

        return TryDecodeBase64(rawToken, out var decoded)
            ? decoded.Trim()
            : rawToken;
    }

    private static bool TryBuildTokenUrl(JsonElement root, out string url, out string? errorMessage)
    {
        url = string.Empty;
        errorMessage = null;

        if (!TryGetString(root, "host", out var host))
            return false;

        var protocolText = TryGetString(root, "protocol", out var protocolValue)
            ? protocolValue
            : "ssh";
        if (!TryParseProtocol(protocolText, out var protocol))
        {
            errorMessage = $"Unsupported token protocol: {protocolText}.";
            return false;
        }

        var port = TryGetInt(root, "port", out var tokenPort)
            ? tokenPort
            : GetDefaultPort(protocol);
        if (port is < 1 or > 65535)
        {
            errorMessage = "Token port must be between 1 and 65535.";
            return false;
        }

        TryGetString(root, "username", out var username);
        if (string.IsNullOrWhiteSpace(username))
            TryGetString(root, "user", out username);
        TryGetString(root, "password", out var password);
        TryGetString(root, "path", out var path);
        if (string.IsNullOrWhiteSpace(path))
            TryGetString(root, "remotePath", out path);

        var builder = new StringBuilder();
        builder.Append(protocol.ToString().ToLowerInvariant());
        builder.Append("://");
        if (!string.IsNullOrWhiteSpace(username))
        {
            builder.Append(Uri.EscapeDataString(username));
            if (!string.IsNullOrEmpty(password))
            {
                builder.Append(':');
                builder.Append(Uri.EscapeDataString(password));
            }

            builder.Append('@');
        }

        builder.Append(FormatUrlHost(host));
        builder.Append(':');
        builder.Append(port);
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.Append('/');
            builder.Append(Uri.EscapeDataString(path.TrimStart('/', '\\')));
        }

        url = builder.ToString();
        return true;
    }

    private static string FormatUrlHost(string host)
    {
        host = host.Trim();
        return host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
    }

    private static void AddTokenJumpHosts(JsonElement root, List<string> jumpHosts)
    {
        if (TryGetString(root, "jumpHost", out var jumpHost) ||
            TryGetString(root, "jump", out jumpHost) ||
            TryGetString(root, "J", out jumpHost))
        {
            jumpHosts.Add(jumpHost);
        }

        if (!TryGetProperty(root, "jumpHosts", out var jumpHostsElement))
            return;

        if (jumpHostsElement.ValueKind == JsonValueKind.String)
        {
            var value = jumpHostsElement.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                jumpHosts.Add(value);
            return;
        }

        if (jumpHostsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in jumpHostsElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    jumpHosts.Add(value);
            }
        }
    }

    private static bool LooksLikeSessionUrl(string value)
    {
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex <= 0)
            return false;

        return TryParseProtocol(value[..schemeIndex], out _);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(root, propertyName, out var property))
            return false;

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                value = property.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = property.ToString();
                return !string.IsNullOrWhiteSpace(value);
            default:
                return false;
        }
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!TryGetProperty(root, propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (!TryGetProperty(root, propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), out value);
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement property)
    {
        foreach (var item in root.EnumerateObject())
        {
            if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool TryParseSessionUrl(
        string rawUrl,
        string? tabName,
        bool forceAuthPrompt,
        out CommandLineSessionRequest? request,
        out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        rawUrl = NormalizeArgument(rawUrl);
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            errorMessage = "Missing -url value.";
            return false;
        }

        var protocol = SessionProtocol.SSH;
        var remainder = rawUrl;
        var schemeIndex = rawUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            var scheme = rawUrl[..schemeIndex];
            if (!TryParseProtocol(scheme, out protocol))
            {
                errorMessage = $"Unsupported URL protocol: {scheme}.";
                return false;
            }

            remainder = rawUrl[(schemeIndex + 3)..];
        }

        var path = string.Empty;
        var slashIndex = remainder.IndexOf('/');
        if (slashIndex >= 0)
        {
            path = remainder[slashIndex..];
            remainder = remainder[..slashIndex];
        }

        var userInfo = string.Empty;
        var atIndex = remainder.LastIndexOf('@');
        if (atIndex >= 0)
        {
            userInfo = remainder[..atIndex];
            remainder = remainder[(atIndex + 1)..];
        }

        if (!TryParseHostPort(remainder, GetDefaultPort(protocol), out var host, out var port))
        {
            errorMessage = "Invalid -url host or port.";
            return false;
        }

        var username = string.Empty;
        string? password = null;
        if (!string.IsNullOrEmpty(userInfo))
        {
            var passwordSeparatorIndex = userInfo.IndexOf(':');
            if (passwordSeparatorIndex >= 0)
            {
                username = DecodeComponent(userInfo[..passwordSeparatorIndex]);
                password = DecodeComponent(userInfo[(passwordSeparatorIndex + 1)..]);
            }
            else
            {
                username = DecodeComponent(userInfo);
            }

            DecodeB64UserInfoIfNeeded(ref username, ref password);
        }

        if (forceAuthPrompt)
            password = null;

        var sessionName = string.IsNullOrWhiteSpace(tabName)
            ? BuildDefaultSessionName(protocol, username, host, port)
            : tabName.Trim();
        var remotePath = DecodePath(path);

        request = new CommandLineSessionRequest(
            new SessionInfo
            {
                Id = Guid.NewGuid(),
                Name = sessionName,
                Protocol = protocol,
                Host = host,
                Port = port,
                Username = username,
                AuthMethod = AuthMethod.Password,
                SshAutoOpenSftpPanel = false,
                SshAutoOpenMonitorPanel = false,
                SshDoNotStartFileManager = true,
                SftpRemoteStartDirectory = protocol is SessionProtocol.SFTP or SessionProtocol.FTP ? remotePath ?? string.Empty : string.Empty
            },
            string.IsNullOrEmpty(password) ? null : password,
            remotePath,
            forceAuthPrompt);
        return true;
    }

    private static bool TryApplyJumpHosts(SessionInfo session, IReadOnlyList<string> values, out string? errorMessage)
    {
        errorMessage = null;
        var rawValue = string.Join(",", values.Select(NormalizeArgument));
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            errorMessage = "Missing -J jump host value.";
            return false;
        }

        if (session.Protocol is not (SessionProtocol.SSH or SessionProtocol.SFTP))
        {
            errorMessage = "-J currently supports SSH and SFTP sessions only.";
            return false;
        }

        var specs = rawValue.Split(',', StringSplitOptions.TrimEntries);
        if (specs.Length == 0 || specs.Any(string.IsNullOrWhiteSpace))
        {
            errorMessage = "Invalid -J jump host list.";
            return false;
        }

        var proxies = new List<ProxySettings>();
        foreach (var spec in specs)
        {
            if (!TryParseJumpHost(session, spec, out var proxy, out errorMessage))
                return false;

            proxies.Add(proxy);
        }

        for (var index = 0; index < proxies.Count - 1; index++)
            proxies[index].NextProxyId = proxies[index + 1].Id;

        session.Proxy = proxies[0];
        session.ProxyServers = proxies;
        session.SelectedProxyId = proxies[0].Id;
        return true;
    }

    private static bool TryParseJumpHost(
        SessionInfo session,
        string value,
        out ProxySettings proxy,
        out string? errorMessage)
    {
        proxy = new ProxySettings();
        errorMessage = null;

        var userInfo = string.Empty;
        var hostPort = value;
        var atIndex = value.LastIndexOf('@');
        if (atIndex >= 0)
        {
            userInfo = value[..atIndex];
            hostPort = value[(atIndex + 1)..];
        }

        if (!TryParseHostPort(hostPort, 22, out var host, out var port))
        {
            errorMessage = "Invalid -J jump host or port.";
            return false;
        }

        var username = session.Username;
        var password = string.Empty;
        if (!string.IsNullOrEmpty(userInfo))
        {
            var passwordSeparatorIndex = userInfo.IndexOf(':');
            if (passwordSeparatorIndex >= 0)
            {
                username = DecodeComponent(userInfo[..passwordSeparatorIndex]);
                password = DecodeComponent(userInfo[(passwordSeparatorIndex + 1)..]);
            }
            else
            {
                username = DecodeComponent(userInfo);
            }
        }

        string? decodedPassword = string.IsNullOrEmpty(password) ? null : password;
        DecodeB64UserInfoIfNeeded(ref username, ref decodedPassword);
        password = decodedPassword ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            errorMessage = "Jump host username is required.";
            return false;
        }

        proxy = new ProxySettings
        {
            Id = Guid.NewGuid(),
            Name = $"JUMPHOST {host}:{port}",
            Protocol = ProxyProtocol.JumpHost,
            Host = host,
            Port = port,
            Username = username.Trim(),
            Password = PasswordEncryptionService.Encrypt(password),
            AuthMethod = AuthMethod.Password,
            UseAgent = string.IsNullOrEmpty(password)
        };
        return true;
    }

    private static string? ReadNextValue(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count)
            return null;

        index++;
        return args[index];
    }

    private static string NormalizeArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static bool TryParseProtocol(string value, out SessionProtocol protocol)
    {
        protocol = value.Trim().ToLowerInvariant() switch
        {
            "ssh" => SessionProtocol.SSH,
            "sftp" => SessionProtocol.SFTP,
            "telnet" => SessionProtocol.TELNET,
            "rlogin" => SessionProtocol.RLOGIN,
            "ftp" => SessionProtocol.FTP,
            _ => default
        };
        return protocol is SessionProtocol.SSH or SessionProtocol.SFTP or SessionProtocol.TELNET or SessionProtocol.RLOGIN or SessionProtocol.FTP;
    }

    private static int GetDefaultPort(SessionProtocol protocol)
    {
        return protocol switch
        {
            SessionProtocol.TELNET => 23,
            SessionProtocol.RLOGIN => 513,
            SessionProtocol.FTP => 21,
            _ => 22
        };
    }

    private static bool TryParseHostPort(string value, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;

        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith('['))
        {
            var closeIndex = value.IndexOf(']');
            if (closeIndex <= 1)
                return false;

            host = value[1..closeIndex];
            var rest = value[(closeIndex + 1)..];
            if (rest.StartsWith(':') && !TryParsePort(rest[1..], out port))
                return false;

            return !string.IsNullOrWhiteSpace(host);
        }

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex > 0 && value.IndexOf(':') == colonIndex)
        {
            var portText = value[(colonIndex + 1)..];
            if (TryParsePort(portText, out var parsedPort))
            {
                host = value[..colonIndex];
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host);
            }

            return false;
        }

        host = value;
        return true;
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value.Trim(), out port) && port is >= 1 and <= 65535;
    }

    private static string DecodeComponent(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    private static string? DecodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return null;

        return DecodeComponent(path);
    }

    private static void DecodeB64UserInfoIfNeeded(ref string username, ref string? password)
    {
        const string prefix = "b64>>";
        if (!username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;

        var encoded = username[prefix.Length..];
        if (!TryDecodeBase64(encoded, out var decoded))
            return;

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex >= 0 && password == null)
        {
            username = decoded[..separatorIndex];
            password = decoded[(separatorIndex + 1)..];
            return;
        }

        username = decoded;
    }

    private static bool TryDecodeBase64(string encoded, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            var padding = encoded.Length % 4;
            if (padding > 0)
                encoded = encoded.PadRight(encoded.Length + 4 - padding, '=');

            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDefaultSessionName(SessionProtocol protocol, string username, string host, int port)
    {
        var userPrefix = string.IsNullOrWhiteSpace(username) ? string.Empty : $"{username}@";
        return $"{protocol} {userPrefix}{host}:{port}";
    }
}

public sealed record CommandLineSessionRequest(
    SessionInfo Session,
    string? Password,
    string? InitialRemoteDirectory,
    bool ForceAuthPrompt);
