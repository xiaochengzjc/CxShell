using System.Text;
using Avalonia.Input;

namespace CxShell.Terminal;

[Flags]
public enum KittyKeyboardFlags
{
    None = 0,
    DisambiguateEscapeCodes = 1,
    ReportEventTypes = 2,
    ReportAlternateKeys = 4,
    ReportAllKeysAsEscapeCodes = 8,
    ReportAssociatedText = 16
}

public enum TerminalKeyEventType
{
    Press = 1,
    Repeat = 2,
    Release = 3
}

/// <summary>
/// Encodes modified keys and the progressive Kitty keyboard protocol while
/// preserving legacy terminal sequences for ordinary text input.
/// </summary>
public static class TerminalKeyboardEncoder
{
    public static bool TryEncode(
        Key key,
        KeyModifiers modifiers,
        int modifyOtherKeysMode,
        int kittyKeyboardFlags,
        out string sequence)
    {
        return TryEncode(
            key,
            modifiers,
            modifyOtherKeysMode,
            kittyKeyboardFlags,
            TerminalKeyEventType.Press,
            associatedText: null,
            out sequence);
    }

    public static bool TryEncode(
        Key key,
        KeyModifiers modifiers,
        int modifyOtherKeysMode,
        int kittyKeyboardFlags,
        TerminalKeyEventType eventType,
        out string sequence)
    {
        return TryEncode(
            key,
            modifiers,
            modifyOtherKeysMode,
            kittyKeyboardFlags,
            eventType,
            associatedText: null,
            out sequence);
    }

    public static bool TryEncode(
        Key key,
        KeyModifiers modifiers,
        int modifyOtherKeysMode,
        int kittyKeyboardFlags,
        TerminalKeyEventType eventType,
        string? associatedText,
        out string sequence)
    {
        sequence = string.Empty;
        var flags = NormalizeFlags(kittyKeyboardFlags);
        if (flags == KittyKeyboardFlags.None)
        {
            if (modifyOtherKeysMode <= 0 || !HasModifier(modifiers))
                return false;

            var modifyCode = GetKeyCode(key, modifiers);
            if (modifyCode <= 0)
                return false;

            sequence = $"\x1b[27;{GetModifierCode(modifiers)};{modifyCode}~";
            return true;
        }

        var reportAll = flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes);
        var hasModifier = HasModifier(modifiers);
        var hasNonShiftModifier = HasNonShiftModifier(modifiers);
        var isTextKey = IsTextKey(key);

        if (eventType != TerminalKeyEventType.Press &&
            !flags.HasFlag(KittyKeyboardFlags.ReportEventTypes))
        {
            return false;
        }

        // Text input is delivered separately through OnTextInput. Let that
        // event carry the press for plain and Shift-only text keys, while
        // control/Alt combinations are handled by OnKeyDown.
        if (eventType == TerminalKeyEventType.Press &&
            reportAll &&
            isTextKey &&
            !hasNonShiftModifier &&
            string.IsNullOrEmpty(associatedText))
        {
            return false;
        }

        if (!reportAll)
        {
            if (isTextKey)
            {
                // Disambiguation applies only to modified text keys. Plain
                // and Shift-only text continues through the text-input event.
                if (!hasModifier || !flags.HasFlag(KittyKeyboardFlags.DisambiguateEscapeCodes))
                    return false;
            }
            else if (!hasModifier && !IsKittySpecialKey(key))
            {
                return false;
            }
        }

        // Enter, Tab, and Backspace retain their legacy bytes until the
        // application explicitly asks for all keys as escape codes.
        if (!reportAll && IsLegacyControlKey(key))
            return false;

        var code = reportAll || flags.HasFlag(KittyKeyboardFlags.ReportAlternateKeys)
            ? GetKeyCode(key, modifiers & ~KeyModifiers.Shift)
            : GetKittyKeyCode(key, modifiers);
        if (code <= 0)
            return false;

        var alternateCode = flags.HasFlag(KittyKeyboardFlags.ReportAlternateKeys)
            ? GetAlternateKeyCode(key, modifiers, code)
            : null;
        var text = flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) && reportAll
            ? EncodeAssociatedText(associatedText)
            : null;

        sequence = FormatKittySequence(code, alternateCode, modifiers, eventType, flags, text);
        return true;
    }

    /// <summary>
    /// Encodes a text-input event when Kitty's report-all-keys mode is active.
    /// This is separate from key-down handling because Avalonia delivers IME
    /// and keyboard-layout text through OnTextInput.
    /// </summary>
    public static bool TryEncodeTextInput(
        string text,
        Key key,
        KeyModifiers modifiers,
        int kittyKeyboardFlags,
        out string sequence)
    {
        return TryEncodeTextInput(
            text,
            key,
            modifiers,
            kittyKeyboardFlags,
            TerminalKeyEventType.Press,
            out sequence);
    }

    public static bool TryEncodeTextInput(
        string text,
        Key key,
        KeyModifiers modifiers,
        int kittyKeyboardFlags,
        TerminalKeyEventType eventType,
        out string sequence)
    {
        sequence = string.Empty;
        var flags = NormalizeFlags(kittyKeyboardFlags);
        if (!flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes) ||
            string.IsNullOrEmpty(text))
        {
            return false;
        }

        var code = GetKeyCode(key, modifiers & ~KeyModifiers.Shift);
        var alternateCode = flags.HasFlag(KittyKeyboardFlags.ReportAlternateKeys)
            ? GetAlternateKeyCode(key, modifiers, code)
            : null;
        var associatedText = EncodeAssociatedText(text);
        if (associatedText == null)
            return false;

        sequence = FormatKittySequence(
            code,
            alternateCode,
            modifiers,
            eventType,
            flags,
            flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) ? associatedText : null);
        return true;
    }

    public static int GetModifierCode(KeyModifiers modifiers)
    {
        var value = 1;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            value += 1;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            value += 2;
        if (modifiers.HasFlag(KeyModifiers.Control))
            value += 4;
        if (modifiers.HasFlag(KeyModifiers.Meta))
            value += 8;
        return value;
    }

    public static KittyKeyboardFlags NormalizeFlags(int flags) =>
        (KittyKeyboardFlags)Math.Clamp(flags, 0, 31);

    private static bool HasModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) ||
        modifiers.HasFlag(KeyModifiers.Alt) ||
        modifiers.HasFlag(KeyModifiers.Control) ||
        modifiers.HasFlag(KeyModifiers.Meta);

    private static bool HasNonShiftModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt) ||
        modifiers.HasFlag(KeyModifiers.Control) ||
        modifiers.HasFlag(KeyModifiers.Meta);

    private static bool IsLegacyControlKey(Key key) =>
        key is Key.Enter or Key.Tab or Key.Back;

    private static bool IsTextKey(Key key) =>
        key is >= Key.A and <= Key.Z ||
        key is >= Key.D0 and <= Key.D9 ||
        key is Key.Space or Key.OemOpenBrackets or Key.OemBackslash or Key.Oem5 or
        Key.OemCloseBrackets or Key.OemMinus or Key.OemPlus or Key.OemComma or
        Key.OemPeriod or Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes;

    private static bool IsKittySpecialKey(Key key) => key switch
    {
        Key.Enter or Key.Tab or Key.Escape or Key.Back or Key.Delete or
        Key.Insert or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or
        Key.PageUp or Key.PageDown or Key.PrintScreen or Key.Pause or Key.CapsLock or
        Key.NumLock or Key.Scroll or
        Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6 or
        Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12 or
        Key.NumPad0 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or
        Key.NumPad4 or Key.NumPad5 or Key.NumPad6 or Key.NumPad7 or
        Key.NumPad8 or Key.NumPad9 or Key.Multiply or Key.Add or Key.Subtract or
        Key.Decimal or Key.Divide => true,
        _ => false
    };

    private static int GetKeyCode(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            var offset = (int)key - (int)Key.A;
            return (modifiers.HasFlag(KeyModifiers.Shift) ? 'A' : 'a') + offset;
        }

        return key switch
        {
            Key.D0 => GetShiftedKeyCode(modifiers, '0', ')'),
            Key.D1 => GetShiftedKeyCode(modifiers, '1', '!'),
            Key.D2 => GetShiftedKeyCode(modifiers, '2', '@'),
            Key.D3 => GetShiftedKeyCode(modifiers, '3', '#'),
            Key.D4 => GetShiftedKeyCode(modifiers, '4', '$'),
            Key.D5 => GetShiftedKeyCode(modifiers, '5', '%'),
            Key.D6 => GetShiftedKeyCode(modifiers, '6', '^'),
            Key.D7 => GetShiftedKeyCode(modifiers, '7', '&'),
            Key.D8 => GetShiftedKeyCode(modifiers, '8', '*'),
            Key.D9 => GetShiftedKeyCode(modifiers, '9', '('),
            Key.Space => ' ',
            Key.OemOpenBrackets => GetShiftedKeyCode(modifiers, '[', '{'),
            Key.OemBackslash or Key.Oem5 => GetShiftedKeyCode(modifiers, '\\', '|'),
            Key.OemCloseBrackets => GetShiftedKeyCode(modifiers, ']', '}'),
            Key.OemMinus => GetShiftedKeyCode(modifiers, '-', '_'),
            Key.OemPlus => GetShiftedKeyCode(modifiers, '=', '+'),
            Key.OemComma => GetShiftedKeyCode(modifiers, ',', '<'),
            Key.OemPeriod => GetShiftedKeyCode(modifiers, '.', '>'),
            Key.OemQuestion => GetShiftedKeyCode(modifiers, '/', '?'),
            Key.OemSemicolon => GetShiftedKeyCode(modifiers, ';', ':'),
            Key.OemQuotes => GetShiftedKeyCode(modifiers, '\'', '"'),
            Key.Enter => 13,
            Key.Tab => 9,
            Key.Escape => 27,
            Key.Back => 127,
            Key.Delete => 127,
            Key.Up => 57362,
            Key.Down => 57364,
            Key.Right => 57363,
            Key.Left => 57361,
            Key.Home => 57360,
            Key.End => 57367,
            Key.PageUp => 57365,
            Key.PageDown => 57366,
            Key.PrintScreen => 57361,
            Key.Pause => 57362,
            Key.CapsLock => 57358,
            Key.Scroll => 57359,
            Key.NumLock => 57360,
            Key.Insert => 57357,
            Key.F1 => 57376,
            Key.F2 => 57377,
            Key.F3 => 57378,
            Key.F4 => 57379,
            Key.F5 => 57380,
            Key.F6 => 57381,
            Key.F7 => 57382,
            Key.F8 => 57383,
            Key.F9 => 57384,
            Key.F10 => 57385,
            Key.F11 => 57386,
            Key.F12 => 57387,
            Key.NumPad0 => 57399,
            Key.NumPad1 => 57400,
            Key.NumPad2 => 57401,
            Key.NumPad3 => 57402,
            Key.NumPad4 => 57403,
            Key.NumPad5 => 57404,
            Key.NumPad6 => 57405,
            Key.NumPad7 => 57406,
            Key.NumPad8 => 57407,
            Key.NumPad9 => 57408,
            Key.Decimal => 57409,
            Key.Divide => 57410,
            Key.Multiply => 57411,
            Key.Subtract => 57412,
            Key.Add => 57413,
            _ => 0
        };
    }

    private static int? GetAlternateKeyCode(Key key, KeyModifiers modifiers, int code)
    {
        if (!IsTextKey(key))
            return null;

        var alternate = GetKeyCode(key, modifiers | KeyModifiers.Shift);
        return alternate > 0 && alternate != code ? alternate : null;
    }

    private static int GetKittyKeyCode(Key key, KeyModifiers modifiers)
    {
        return key switch
        {
            Key.Back => 127,
            Key.Insert => 57357,
            Key.Delete => 57358,
            _ => GetKeyCode(key, modifiers)
        };
    }

    private static int GetShiftedKeyCode(KeyModifiers modifiers, char normal, char shifted) =>
        modifiers.HasFlag(KeyModifiers.Shift) ? shifted : normal;

    private static string? EncodeAssociatedText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var codePoints = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x20 ||
                rune.Value is >= 0x7F and <= 0x9F)
            {
                return null;
            }

            codePoints.Add(rune.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return codePoints.Count == 0 ? null : string.Join(':', codePoints);
    }

    private static string FormatKittySequence(
        int code,
        int? alternateCode,
        KeyModifiers modifiers,
        TerminalKeyEventType eventType,
        KittyKeyboardFlags flags,
        string? associatedText)
    {
        var keyPart = alternateCode is { } alternate && alternate != code
            ? $"{code}:{alternate}"
            : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var modifierPart = GetModifierCode(modifiers).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (flags.HasFlag(KittyKeyboardFlags.ReportEventTypes))
            modifierPart += $":{(int)eventType}";

        var sequence = $"\x1b[{keyPart};{modifierPart}";
        if (associatedText != null)
            sequence += $";{associatedText}";
        return sequence + "u";
    }
}
