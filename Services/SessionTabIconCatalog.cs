using AtomUI.Icons.AntDesign;
using Avalonia.Controls;

namespace CxShell.Services;

public static class SessionTabIconCatalog
{
    public const string Default = "Default";
    public const string Server = "Server";
    public const string Database = "Database";
    public const string Desktop = "Desktop";
    public const string Code = "Code";
    public const string Cloud = "Cloud";

    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        Default,
        Server,
        Database,
        Desktop,
        Code,
        Cloud
    };

    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !SupportedKeys.Contains(key))
            return Default;

        return SupportedKeys.First(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
    }

    public static PathIcon? CreateIcon(string? key)
    {
        var kind = Normalize(key) switch
        {
            Server => AntDesignIconKind.CloudServerOutlined,
            Database => AntDesignIconKind.DatabaseOutlined,
            Desktop => AntDesignIconKind.DesktopOutlined,
            Code => AntDesignIconKind.CodeOutlined,
            Cloud => AntDesignIconKind.CloudOutlined,
            _ => (AntDesignIconKind?)null
        };

        return kind.HasValue
            ? (PathIcon)new AntDesignIconProvider(kind.Value).ProvideValue(null!)
            : null;
    }
}
