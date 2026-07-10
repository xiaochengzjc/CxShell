using System.Net.Sockets;
using System.Runtime.InteropServices;

if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    var requireAudio = args.Contains("--require-audio", StringComparer.OrdinalIgnoreCase);
    return ProbeNativeBridge(requireAudio);
}

var host = Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_HOST")?.Trim();
var username = Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_USERNAME")?.Trim();
var password = Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_PASSWORD") ?? string.Empty;
var drivePath = Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_DRIVE_PATH")?.Trim();
var audioModeText = Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_AUDIO_MODE")?.Trim();
var microphoneEnabled = string.Equals(
    Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_MICROPHONE"),
    "1",
    StringComparison.OrdinalIgnoreCase);
var port = int.TryParse(Environment.GetEnvironmentVariable("CXSHELL_RDP_TEST_PORT"), out var configuredPort)
    ? configuredPort
    : 3389;

if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
{
    Console.WriteLine("Set CXSHELL_RDP_TEST_HOST and CXSHELL_RDP_TEST_USERNAME before running this integration test.");
    Console.WriteLine("Optional: CXSHELL_RDP_TEST_PASSWORD, CXSHELL_RDP_TEST_PORT, CXSHELL_RDP_TEST_DRIVE_PATH,");
    Console.WriteLine("          CXSHELL_RDP_TEST_AUDIO_MODE (local/remote/none), CXSHELL_RDP_TEST_MICROPHONE (1/0).");
    return 2;
}

Console.WriteLine($"Native bridge API {Native.cxrdp_get_api_version()}, capabilities 0x{Native.cxrdp_get_capabilities():X8}");

Console.WriteLine($"TCP test {host}:{port}");
using (var client = new TcpClient())
{
    var connectTask = client.ConnectAsync(host, port);
    if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5))) != connectTask)
    {
        Console.WriteLine("TCP timeout");
        return 2;
    }

    await connectTask;
    Console.WriteLine("TCP connected");
}

Console.WriteLine();
Console.WriteLine($"=== trying user '{username}' ===");
var completed = new ManualResetEventSlim(false);
var connected = false;

StatusCallback status = (_, message) =>
{
    var text = Marshal.PtrToStringUTF8(message) ?? string.Empty;
    Console.WriteLine(text);
    if (text.Contains("RDP connected.", StringComparison.OrdinalIgnoreCase))
    {
        connected = true;
        completed.Set();
    }
    else if (text.Contains("FreeRDP connection failed.", StringComparison.OrdinalIgnoreCase) &&
             text.Contains("mode=negotiate", StringComparison.OrdinalIgnoreCase))
    {
        completed.Set();
    }
};
DisconnectCallback disconnected = _ =>
{
    Console.WriteLine("DISCONNECTED CALLBACK");
    completed.Set();
};
FrameCallback frame = (_, width, height, stride, data) =>
{
    Console.WriteLine($"FRAME {width}x{height} stride={stride}");
    connected = true;
    completed.Set();
};

var handle = Native.cxrdp_create();
if (handle == IntPtr.Zero)
{
    Console.WriteLine("create failed");
    return 1;
}

try
{
    Native.cxrdp_set_callbacks(handle, frame, status, disconnected, IntPtr.Zero);
    if (!string.IsNullOrWhiteSpace(drivePath))
    {
        var driveResult = Native.cxrdp_set_drive_redirection(handle, 1, "CxShellTest", drivePath);
        Console.WriteLine($"drive redirection returned {driveResult}");
        if (driveResult != 0)
            return 1;
    }

    var audioMode = audioModeText?.ToLowerInvariant() switch
    {
        "local" => 0,
        "remote" => 1,
        _ => 2
    };
    if (audioMode != 2 || microphoneEnabled)
    {
        var audioResult = Native.cxrdp_set_audio_redirection(
            handle,
            audioMode,
            microphoneEnabled ? 1 : 0);
        Console.WriteLine($"audio redirection returned {audioResult}");
        if (audioResult != 0)
            return 1;
    }

    var keyboardResult = Native.cxrdp_set_keyboard_options(handle, 1);
    Console.WriteLine($"keyboard options returned {keyboardResult}");
    if (keyboardResult != 0)
        return 1;

    var result = Native.cxrdp_connect(handle, host, port, username, password, 1280, 720, 32);
    Console.WriteLine($"connect returned {result}");
    completed.Wait(TimeSpan.FromSeconds(20));
    Console.WriteLine($"connected={connected}");
    return connected ? 0 : 1;
}
finally
{
    Native.cxrdp_disconnect(handle);
    Native.cxrdp_destroy(handle);
}

static int ProbeNativeBridge(bool requireAudio)
{
    const uint minimumApiVersion = 4;
    const uint clipboardCapability = 0x00000001;
    const uint driveRedirectionCapability = 0x00000002;
    const uint audioPlaybackCapability = 0x00000004;
    const uint microphoneCapability = 0x00000008;
    const uint keyboardHookCapability = 0x00000010;

    try
    {
        var apiVersion = Native.cxrdp_get_api_version();
        var capabilities = Native.cxrdp_get_capabilities();
        var requiredCapabilities = clipboardCapability |
                                   driveRedirectionCapability |
                                   keyboardHookCapability;
        if (requireAudio)
            requiredCapabilities |= audioPlaybackCapability | microphoneCapability;

        Console.WriteLine($"Native bridge API {apiVersion}, capabilities 0x{capabilities:X8}");
        if (apiVersion < minimumApiVersion)
        {
            Console.Error.WriteLine($"Expected native bridge API >= {minimumApiVersion}.");
            return 1;
        }

        var missingCapabilities = requiredCapabilities & ~capabilities;
        if (missingCapabilities != 0)
        {
            Console.Error.WriteLine($"Missing required native capabilities: 0x{missingCapabilities:X8}.");
            return 1;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var probeHandle = Native.cxrdp_create();
            if (probeHandle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Native session creation failed on attempt {attempt}.");
                return 1;
            }

            try
            {
                if (Native.cxrdp_set_drive_redirection(probeHandle, 0, "CxShellProbe", string.Empty) != 0 ||
                    Native.cxrdp_set_audio_redirection(probeHandle, 2, 0) != 0 ||
                    Native.cxrdp_set_keyboard_options(probeHandle, 1) != 0)
                {
                    Console.Error.WriteLine($"Native session configuration failed on attempt {attempt}.");
                    return 1;
                }
            }
            finally
            {
                Native.cxrdp_disconnect(probeHandle);
                Native.cxrdp_destroy(probeHandle);
            }
        }

        Console.WriteLine("Native bridge probe passed (create/configure/close x2).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Native bridge probe failed: {ex.Message}");
        return 1;
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void FrameCallback(IntPtr userData, int width, int height, int stride, IntPtr bgraPixels);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void StatusCallback(IntPtr userData, IntPtr message);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void DisconnectCallback(IntPtr userData);

internal static partial class Native
{
    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint cxrdp_get_api_version();

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint cxrdp_get_capabilities();

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cxrdp_create();

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_destroy(IntPtr handle);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_set_callbacks(IntPtr handle, FrameCallback frame, StatusCallback status, DisconnectCallback disconnected, IntPtr userData);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cxrdp_connect(IntPtr handle, string host, int port, string username, string password, int width, int height, int colorDepth);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cxrdp_set_drive_redirection(IntPtr handle, int enabled, string driveName, string localPath);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cxrdp_set_audio_redirection(IntPtr handle, int playbackMode, int microphoneEnabled);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cxrdp_set_keyboard_options(IntPtr handle, int applyKeyCombinationsRemotely);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_disconnect(IntPtr handle);
}
