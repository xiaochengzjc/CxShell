using System.Collections.ObjectModel;

namespace CxShell.Services;

public static class RdpKeyboardShortcutSequences
{
    public const uint ExtendedEndScancode = 0x014F;
    public const uint ExtendedDeleteScancode = 0x0153;

    public static IReadOnlyList<uint> CtrlAltDelete { get; } =
        new ReadOnlyCollection<uint>([0x1D, 0x38, ExtendedDeleteScancode]);

    public static IReadOnlyList<uint> SaveRemoteScreenshot { get; } =
        new ReadOnlyCollection<uint>([0x0100 | 0x5B, 0x0100 | 0x37]);

    public static uint TranslateCtrlAltEnd(uint scancode, bool controlDown, bool altDown)
    {
        return controlDown && altDown && scancode == ExtendedEndScancode
            ? ExtendedDeleteScancode
            : scancode;
    }
}
