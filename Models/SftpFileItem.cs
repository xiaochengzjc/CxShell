using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChiXueSsh.Models;

public class SftpFileItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Permissions { get; set; } = "";
    public bool IsSymLink { get; set; }
    public string? SymLinkTarget { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            _isRenaming = value;
            if (value) _renamingText = Name; // 初始化为当前名称
            OnPropertyChanged();
        }
    }

    private string _renamingText = "";
    public string RenamingText
    {
        get => _renamingText;
        set { _renamingText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Icon => IsDirectory ? "📁" : GetFileIcon(Name);
    public string SizeDisplay => IsDirectory ? "<DIR>" : FormatSize(Size);
    public string DateDisplay => LastModified.ToString("MM-dd HH:mm");

    public string DisplayName => IsSymLink && SymLinkTarget != null
        ? $"{Name} → {SymLinkTarget}"
        : Name;

    private static string GetFileIcon(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" or ".md" or ".rst" => "📝",
            ".sh" or ".bash" or ".zsh" or ".fish" => "⚙",
            ".py" or ".rb" or ".js" or ".ts" or ".go" or ".rs" or ".cs" or ".java" => "📄",
            ".zip" or ".tar" or ".gz" or ".bz2" or ".xz" or ".7z" or ".rar" => "📦",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".webp" => "🖼",
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => "🎵",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "🎬",
            ".pdf" => "📕",
            ".json" or ".yaml" or ".yml" or ".toml" or ".xml" or ".ini" or ".conf" => "⚙",
            _ => "📄"
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
