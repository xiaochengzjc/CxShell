using System.Net.Sockets;
using System.Runtime.InteropServices;

const string host = "117.72.38.235";
const int port = 3389;
const string password = "123456";

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

var users = new[]
{
    "rdpuser",
    @".\rdpuser",
    $@"{host}\rdpuser"
};

foreach (var user in users)
{
    Console.WriteLine();
    Console.WriteLine($"=== trying user '{user}' ===");
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
        continue;
    }

    try
    {
        Native.cxrdp_set_callbacks(handle, frame, status, disconnected, IntPtr.Zero);
        var result = Native.cxrdp_connect(handle, host, port, user, password, 1280, 720);
        Console.WriteLine($"connect returned {result}");
        completed.Wait(TimeSpan.FromSeconds(20));
        Console.WriteLine($"connected={connected}");
        if (connected)
            return 0;
    }
    finally
    {
        Native.cxrdp_disconnect(handle);
        Native.cxrdp_destroy(handle);
    }
}

return 1;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void FrameCallback(IntPtr userData, int width, int height, int stride, IntPtr bgraPixels);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void StatusCallback(IntPtr userData, IntPtr message);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void DisconnectCallback(IntPtr userData);

internal static partial class Native
{
    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr cxrdp_create();

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_destroy(IntPtr handle);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_set_callbacks(IntPtr handle, FrameCallback frame, StatusCallback status, DisconnectCallback disconnected, IntPtr userData);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern int cxrdp_connect(IntPtr handle, string host, int port, string username, string password, int width, int height);

    [DllImport("CxRdpBridge", CallingConvention = CallingConvention.Cdecl)]
    public static extern void cxrdp_disconnect(IntPtr handle);
}
