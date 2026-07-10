namespace CxShell.Services;

public static class RdpSystemShortcutPolicy
{
    public const uint VirtualKeyTab = 0x09;
    public const uint VirtualKeyEscape = 0x1B;
    public const uint VirtualKeySpace = 0x20;
    public const uint VirtualKeyPrintScreen = 0x2C;
    public const uint VirtualKeyLeftWindows = 0x5B;
    public const uint VirtualKeyRightWindows = 0x5C;
    public const uint VirtualKeyApplications = 0x5D;

    public static bool ShouldCapture(
        uint virtualKey,
        bool altDown,
        bool controlDown,
        bool windowsKeyDown)
    {
        if (virtualKey is VirtualKeyLeftWindows or
            VirtualKeyRightWindows or
            VirtualKeyApplications or
            VirtualKeyPrintScreen)
        {
            return true;
        }

        if (windowsKeyDown)
            return true;

        if (altDown && virtualKey is VirtualKeyTab or VirtualKeyEscape or VirtualKeySpace)
            return true;

        return controlDown && virtualKey == VirtualKeyEscape;
    }
}
