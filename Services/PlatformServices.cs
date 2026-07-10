using System;
using System.Collections.Generic;
using System.IO.Ports;
using CxShell.Models;

namespace CxShell.Services;

public static class PlatformServices
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool SupportsVirtualFileDragOut =>
        IsWindows || (IsMacOS && MacOSFilePromiseDragDropService.IsAvailable);

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

    public static bool TryStartVirtualFileDragOut(
        nint nativeWindowOrView,
        IReadOnlyList<VirtualDragFile> files,
        out int effect,
        out string? error)
    {
        effect = 0;
        error = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                effect = WindowsVirtualFileDragDropService.DoDragDrop(files);
                return true;
            }

            if (OperatingSystem.IsMacOS() &&
                MacOSFilePromiseDragDropService.TryStart(nativeWindowOrView, files, out error))
            {
                effect = 1;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
