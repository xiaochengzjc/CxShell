using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public sealed class RdpFramebufferEventArgs : EventArgs
{
    public RdpFramebufferEventArgs(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }
}

public sealed class RdpBridgeClient : IDisposable
{
    private const string LibraryName = "CxRdpBridge";
    private static readonly object DebugLogLock = new();
    private static readonly object NativeLoadFailureLock = new();
    private static readonly bool DetailedDebugLogEnabled = IsEnvironmentFlagEnabled("CXSHELL_RDP_DEBUG_LOG");
    private static string? _lastNativeLoadFailure;
    private readonly FrameCallback _frameCallback;
    private readonly StatusCallback _statusCallback;
    private readonly DisconnectCallback _disconnectCallback;
    private readonly ClipboardTextCallback _clipboardTextCallback;
    private IntPtr _handle;
    private SshClient? _sshTunnelClient;
    private ForwardedPortLocal? _sshTunnelPort;

    static RdpBridgeClient()
    {
        NativeLibrary.SetDllImportResolver(typeof(RdpBridgeClient).Assembly, ResolveNativeLibrary);
    }

    public event EventHandler<RdpFramebufferEventArgs>? FramebufferUpdated;
    public event Action<string>? StatusChanged;
    public event Action? Disconnected;
    public event Action<string>? ClipboardTextReceived;

    public static string DebugLogPath => Path.Combine(GetDebugLogDirectory(), "rdp-debug.log");

    public static string? LastNativeLoadFailure
    {
        get
        {
            lock (NativeLoadFailureLock)
                return _lastNativeLoadFailure;
        }
    }

    public RdpBridgeClient()
    {
        _frameCallback = OnFrame;
        _statusCallback = OnStatus;
        _disconnectCallback = OnDisconnected;
        _clipboardTextCallback = OnClipboardText;
    }

    public void Connect(SessionInfo session, string? password)
    {
        Disconnect();

        var host = session.Host ?? string.Empty;
        var port = session.Port > 0 ? session.Port : 3389;
        var targetDescription = $"{host}:{port}";
        if (session.RdpUseSshTunnel)
        {
            port = StartSshTunnel(session);
            host = "127.0.0.1";
            targetDescription = $"{session.Host}:{(session.Port > 0 ? session.Port : 3389)} via SSH tunnel";
        }

        DebugLog($"connect start host={SanitizeLogValue(host)} port={port} target={SanitizeLogValue(targetDescription)} user={SanitizeLogValue(session.Username)} size={session.RdpDesktopWidth}x{session.RdpDesktopHeight} hasPassword={!string.IsNullOrEmpty(password)} useSshTunnel={session.RdpUseSshTunnel}");
        _handle = NativeMethods.cxrdp_create();
        if (_handle == IntPtr.Zero)
        {
            DebugLog("connect failed: native create returned null");
            StopSshTunnel();
            throw new InvalidOperationException("Failed to create RDP bridge session.");
        }

        NativeMethods.cxrdp_set_callbacks(_handle, _frameCallback, _statusCallback, _disconnectCallback, IntPtr.Zero);
        NativeMethods.cxrdp_set_clipboard_callback(_handle, _clipboardTextCallback);

        var result = NativeMethods.cxrdp_connect(
            _handle,
            host,
            port,
            session.Username ?? string.Empty,
            password ?? string.Empty,
            session.RdpDesktopWidth,
            session.RdpDesktopHeight);

        if (result != 0)
        {
            var error = GetLastError();
            DebugLog($"connect failed result={result} error={SanitizeLogValue(error)}");
            Disconnect();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"RDP bridge connect failed: {result}" : error);
        }

        DebugLog("connect worker started");
    }

    private int StartSshTunnel(SessionInfo session)
    {
        var sshHost = string.IsNullOrWhiteSpace(session.RdpSshHost) ? session.Host : session.RdpSshHost.Trim();
        var sshPort = session.RdpSshPort is >= 1 and <= 65535 ? session.RdpSshPort : 22;
        var sshUser = session.RdpSshUsername?.Trim() ?? string.Empty;
        var remoteHost = string.IsNullOrWhiteSpace(session.Host) ? "127.0.0.1" : session.Host.Trim();
        var remotePort = session.Port is >= 1 and <= 65535 ? session.Port : 3389;
        var localPort = GetFreeLoopbackPort();

        if (string.IsNullOrWhiteSpace(sshHost))
            throw new InvalidOperationException("RDP SSH tunnel host is required.");
        if (string.IsNullOrWhiteSpace(sshUser))
            throw new InvalidOperationException("RDP SSH tunnel username is required.");

        var connectionInfo = new ConnectionInfo(sshHost, sshPort, sshUser, CreateRdpSshAuthMethods(session, sshUser));
        connectionInfo.Timeout = TimeSpan.FromSeconds(12);
        _sshTunnelClient = new SshClient(connectionInfo);
        if (session.SshAcceptAndSaveHostKey)
            _sshTunnelClient.HostKeyReceived += (_, e) => e.CanTrust = true;

        DebugLog($"ssh tunnel connecting ssh={SanitizeLogValue(sshUser)}@{SanitizeLogValue(sshHost)}:{sshPort} local=127.0.0.1:{localPort} remote={SanitizeLogValue(remoteHost)}:{remotePort}");
        StatusChanged?.Invoke($"Opening SSH tunnel to {sshHost}:{sshPort}...");
        try
        {
            _sshTunnelClient.Connect();
        }
        catch (Exception ex)
        {
            var message = $"RDP SSH tunnel login failed: {ex.Message}";
            DebugLog($"ssh tunnel failed {SanitizeLogValue(ex.GetType().Name)}: {SanitizeLogValue(ex.Message)}");
            StopSshTunnel();
            throw new InvalidOperationException(message, ex);
        }

        _sshTunnelPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
        _sshTunnelPort.Exception += (_, e) => StatusChanged?.Invoke($"RDP SSH tunnel failed: {e.Exception.Message}");
        _sshTunnelClient.AddForwardedPort(_sshTunnelPort);
        _sshTunnelPort.Start();
        DebugLog($"ssh tunnel started local=127.0.0.1:{localPort} remote={SanitizeLogValue(remoteHost)}:{remotePort}");
        return localPort;
    }

    private static AuthenticationMethod[] CreateRdpSshAuthMethods(SessionInfo session, string username)
    {
        if (session.RdpSshUsePrivateKey)
        {
            if (string.IsNullOrWhiteSpace(session.RdpSshPrivateKeyPath))
                throw new InvalidOperationException("RDP SSH private key path is required.");
            return [new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(ExpandPath(session.RdpSshPrivateKeyPath)))];
        }

        var password = PasswordEncryptionService.Decrypt(session.RdpSshPassword);
        return [new PasswordAuthenticationMethod(username, password)];
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    public void SendPointer(ushort flags, ushort x, ushort y)
    {
        if (_handle != IntPtr.Zero)
            NativeMethods.cxrdp_send_pointer(_handle, flags, x, y);
    }

    public void SendKey(uint key, bool down)
    {
        if (_handle != IntPtr.Zero)
            NativeMethods.cxrdp_send_key(_handle, key, down ? 1 : 0);
    }

    public void SendUnicodeKey(char key, bool down)
    {
        if (_handle != IntPtr.Zero)
            NativeMethods.cxrdp_send_unicode_key(_handle, key, down ? 1 : 0);
    }

    public void SetClipboardText(string text)
    {
        DebugLogDetailed($"clipboard set local length={text?.Length ?? 0}");
        if (_handle != IntPtr.Zero)
            NativeMethods.cxrdp_set_clipboard_text(_handle, text ?? string.Empty);
    }

    public void Disconnect()
    {
        if (_handle == IntPtr.Zero)
        {
            StopSshTunnel();
            return;
        }

        DebugLog("disconnect requested");
        NativeMethods.cxrdp_disconnect(_handle);
        NativeMethods.cxrdp_destroy(_handle);
        _handle = IntPtr.Zero;
        StopSshTunnel();
        DebugLog("disconnect completed");
    }

    private void StopSshTunnel()
    {
        try
        {
            if (_sshTunnelPort?.IsStarted == true)
                _sshTunnelPort.Stop();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        try
        {
            if (_sshTunnelClient != null && _sshTunnelPort != null)
                _sshTunnelClient.RemoveForwardedPort(_sshTunnelPort);
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        try
        {
            if (_sshTunnelClient?.IsConnected == true)
                _sshTunnelClient.Disconnect();
            _sshTunnelClient?.Dispose();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        _sshTunnelPort = null;
        _sshTunnelClient = null;
    }

    public void Dispose()
    {
        Disconnect();
    }

    private string GetLastError()
    {
        if (_handle == IntPtr.Zero)
            return string.Empty;

        var pointer = NativeMethods.cxrdp_get_last_error(_handle);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    public static string GetExpectedNativeLibraryName()
    {
        if (OperatingSystem.IsWindows())
            return "CxRdpBridge.dll";

        if (OperatingSystem.IsMacOS())
            return "libCxRdpBridge.dylib";

        return "libCxRdpBridge.so";
    }

    public static string GetNativeLibraryLoadErrorMessage(Exception exception)
    {
        var loadFailure = LastNativeLoadFailure;
        if (!string.IsNullOrWhiteSpace(loadFailure))
            return loadFailure;

        return $"{GetExpectedNativeLibraryName()} could not be loaded: {exception.Message}";
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        var failures = new List<string>();
        var missing = new List<string>();
        foreach (var candidate in GetNativeLibraryCandidates())
        {
            if (!File.Exists(candidate))
            {
                missing.Add(candidate);
                continue;
            }

            try
            {
                var handle = NativeLibrary.Load(candidate);
                SetLastNativeLoadFailure(null);
                return handle;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            SetLastNativeLoadFailure(
                $"{GetExpectedNativeLibraryName()} was found, but Windows could not load it. A dependent native DLL may be missing or the architecture may be wrong. {string.Join(" | ", failures)}");
        }
        else
        {
            SetLastNativeLoadFailure(
                $"{GetExpectedNativeLibraryName()} was not found. Checked: {string.Join("; ", missing)}");
        }

        return IntPtr.Zero;
    }

    private static void SetLastNativeLoadFailure(string? message)
    {
        lock (NativeLoadFailureLock)
            _lastNativeLoadFailure = message;
    }

    private static string[] GetNativeLibraryCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fileName = GetExpectedNativeLibraryName();
        var rid = GetCurrentRid();
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, "runtimes", rid, "native", fileName)
        };

        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory) &&
            !string.Equals(processDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(processDirectory, fileName));
            candidates.Add(Path.Combine(processDirectory, "runtimes", rid, "native", fileName));
        }

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories)
        {
            foreach (var directory in nativeSearchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(directory, fileName));
                candidates.Add(Path.Combine(directory, "runtimes", rid, "native", fileName));
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetCurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";

        if (OperatingSystem.IsMacOS())
            return $"osx-{arch}";

        return $"linux-{arch}";
    }

    private void OnFrame(IntPtr userData, int width, int height, int stride, IntPtr data)
    {
        if (width <= 0 || height <= 0 || stride <= 0 || data == IntPtr.Zero)
            return;

        var length = checked(stride * height);
        var pixels = new byte[length];
        Marshal.Copy(data, pixels, 0, length);
        FramebufferUpdated?.Invoke(this, new RdpFramebufferEventArgs(width, height, stride, pixels));
    }

    private void OnStatus(IntPtr userData, IntPtr message)
    {
        var text = message == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            DebugLogStatus(text);
            StatusChanged?.Invoke(text);
        }
    }

    private void OnDisconnected(IntPtr userData)
    {
        DebugLog("disconnected callback");
        Disconnected?.Invoke();
    }

    private void OnClipboardText(IntPtr userData, IntPtr text)
    {
        var value = text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text) ?? string.Empty;
        DebugLogDetailed($"clipboard received remote length={value.Length}");
        ClipboardTextReceived?.Invoke(value);
    }

    private static string GetDebugLogDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        return Path.Combine(root, "CxShell", "Logs");
    }

    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(GetDebugLogDirectory());
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            lock (DebugLogLock)
                File.AppendAllText(DebugLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Debug logging must not break the RDP session.
        }
    }

    private static void DebugLogDetailed(string message)
    {
        if (DetailedDebugLogEnabled)
            DebugLog(message);
    }

    private static void DebugLogStatus(string status)
    {
        if (DetailedDebugLogEnabled || ShouldLogStatus(status))
            DebugLog($"status {SanitizeLogValue(status)}");
    }

    private static bool ShouldLogStatus(string status)
    {
        if (!status.StartsWith("RDP clipboard ", StringComparison.OrdinalIgnoreCase))
            return true;

        return status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("waiting for server MonitorReady", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        return value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameCallback(IntPtr userData, int width, int height, int stride, IntPtr bgraPixels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatusCallback(IntPtr userData, IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisconnectCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardTextCallback(IntPtr userData, IntPtr text);

    private static class NativeMethods
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr cxrdp_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_destroy(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_callbacks(
            IntPtr handle,
            FrameCallback frameCallback,
            StatusCallback statusCallback,
            DisconnectCallback disconnectCallback,
            IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_callback(
            IntPtr handle,
            ClipboardTextCallback clipboardTextCallback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int cxrdp_connect(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
            int port,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
            int width,
            int height);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_disconnect(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_pointer(IntPtr handle, ushort flags, ushort x, ushort y);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_key(IntPtr handle, uint key, int down);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_send_unicode_key(IntPtr handle, ushort code, int down);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cxrdp_set_clipboard_text(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr cxrdp_get_last_error(IntPtr handle);
    }
}
