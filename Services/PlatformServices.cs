using System;
using System.Collections.Generic;
using System.IO.Ports;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public static class PlatformServices
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    public static string[] GetSerialPortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return [];
        }
    }

    public static string[] GetDefaultSerialPortNames()
    {
        if (IsWindows)
            return ["COM1", "COM2", "COM3", "COM4"];

        if (IsMacOS)
            return ["/dev/tty.usbserial", "/dev/tty.usbmodem", "/dev/cu.usbserial"];

        return ["/dev/ttyUSB0", "/dev/ttyACM0", "/dev/ttyS0"];
    }

    public static RdpLaunchResult LaunchRdp(SessionInfo session)
    {
        if (!IsWindows)
            throw new PlatformNotSupportedException("RDP launch currently requires the Windows Remote Desktop client.");

        return RdpLaunchService.Launch(session);
    }

    public static bool TryStartVirtualFileDragOut(
        IReadOnlyList<VirtualDragFile> files,
        out int effect,
        out string? error)
    {
        effect = 0;
        error = null;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            effect = WindowsVirtualFileDragDropService.DoDragDrop(files);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
