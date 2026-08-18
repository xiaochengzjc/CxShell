using System;
using System.Collections.ObjectModel;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public sealed partial class KnownSshHostKeyItemViewModel : ObservableObject
{
    private readonly Action<KnownSshHostKeyItemViewModel> _remove;

    public KnownSshHostKeyItemViewModel(KnownSshHostKey hostKey, Action<KnownSshHostKeyItemViewModel> remove)
    {
        HostKey = hostKey;
        _remove = remove;
    }

    public KnownSshHostKey HostKey { get; }
    public string EndpointText => $"{HostKey.Host}:{HostKey.Port}";
    public string KeyTypeText => HostKey.KeyType;
    public string FingerprintText => HostKey.Fingerprint;
    public string LastSeenText => string.Format(
        LocalizationService.Shared.Text("ApplicationSettings.SshLastSeen"),
        HostKey.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    public string RemoveText => LocalizationService.Shared.Text("ApplicationSettings.SshRemoveKnownHost");

    [RelayCommand]
    private void Remove()
    {
        _remove(this);
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(LastSeenText));
        OnPropertyChanged(nameof(RemoveText));
    }
}

public partial class ApplicationSettingsViewModel : ObservableObject
{
    private readonly ApplicationSettings _settings;
    private readonly Action<ApplicationSettings> _saveSettings;
    private readonly Action<string> _applyLanguage;
    private readonly LocalizationService _localization = LocalizationService.Shared;
    private readonly SshHostKeyTrustService _hostKeyTrust = SshHostKeyTrustService.Shared;

    [ObservableProperty] private bool _showSessionManagerOnStartup;
    [ObservableProperty] private bool _showTabBar;
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private bool _includePrereleaseUpdates;
    [ObservableProperty] private bool _confirmSshHostKeyOnFirstConnection;
    [ObservableProperty] private bool _blockChangedSshHostKeys;
    [ObservableProperty] private bool _recordTerminalSessions;
    [ObservableProperty] private int _recordingRetentionDays;
    [ObservableProperty] private string _uiLanguage;

    public ObservableCollection<KnownSshHostKeyItemViewModel> KnownHosts { get; } = new();

    public string TitleText => Text("ApplicationSettings.Title");
    public string GeneralText => Text("ApplicationSettings.General");
    public string StartupText => Text("ApplicationSettings.Startup");
    public string AppearanceText => Text("ApplicationSettings.Appearance");
    public string UpdatesText => Text("ApplicationSettings.Updates");
    public string LanguageText => Text("ApplicationSettings.Language");
    public string SshSecurityText => Text("ApplicationSettings.SshSecurity");
    public string ConfirmSshHostKeyOnFirstConnectionText => Text("ApplicationSettings.ConfirmSshHostKeyOnFirstConnection");
    public string BlockChangedSshHostKeysText => Text("ApplicationSettings.BlockChangedSshHostKeys");
    public string KnownHostsText => Text("ApplicationSettings.KnownHosts");
    public string NoKnownHostsText => Text("ApplicationSettings.NoKnownHosts");
    public string RecordingText => Text("ApplicationSettings.Recording");
    public string RecordTerminalSessionsText => Text("ApplicationSettings.RecordTerminalSessions");
    public string RecordingDescriptionText => Text("ApplicationSettings.RecordingDescription");
    public string RecordingRetentionText => Text("ApplicationSettings.RecordingRetention");
    public string RecordingDaysText => Text("ApplicationSettings.Days");
    public string ShowSessionManagerOnStartupText => Text("ApplicationSettings.ShowSessionManagerOnStartup");
    public string ShowTabBarText => Text("ApplicationSettings.ShowTabBar");
    public string AutoCheckForUpdatesText => Text("ApplicationSettings.AutoCheckForUpdates");
    public string IncludePrereleaseUpdatesText => Text("ApplicationSettings.IncludePrereleaseUpdates");
    public string ChineseText => Text("Language.Chinese");
    public string EnglishText => Text("Language.English");
    public string CloseText => Text("ApplicationSettings.Close");
    public bool HasKnownHosts => KnownHosts.Count > 0;

    public bool IsChineseSelected
    {
        get => string.Equals(UiLanguage, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                UiLanguage = LocalizationService.Chinese;
        }
    }

    public bool IsEnglishSelected
    {
        get => string.Equals(UiLanguage, LocalizationService.English, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                UiLanguage = LocalizationService.English;
        }
    }

    public ApplicationSettingsViewModel(
        ApplicationSettings settings,
        Action<ApplicationSettings> saveSettings,
        Action<string> applyLanguage)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));

        _showSessionManagerOnStartup = settings.ShowSessionManagerOnStartup;
        _showTabBar = settings.ShowTabBar;
        _autoCheckForUpdates = settings.AutoCheckForUpdates;
        _includePrereleaseUpdates = settings.IncludePrereleaseUpdates;
        _confirmSshHostKeyOnFirstConnection = settings.ConfirmSshHostKeyOnFirstConnection;
        _blockChangedSshHostKeys = settings.BlockChangedSshHostKeys;
        _recordTerminalSessions = settings.RecordTerminalSessions;
        _recordingRetentionDays = settings.RecordingRetentionDays;
        _uiLanguage = NormalizeLanguage(settings.UiLanguage);
        _hostKeyTrust.Configure(settings);
        ReloadKnownHosts();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    partial void OnShowSessionManagerOnStartupChanged(bool value)
    {
        _settings.ShowSessionManagerOnStartup = value;
        Persist();
    }

    partial void OnShowTabBarChanged(bool value)
    {
        _settings.ShowTabBar = value;
        Persist();
    }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        _settings.AutoCheckForUpdates = value;
        Persist();
    }

    partial void OnIncludePrereleaseUpdatesChanged(bool value)
    {
        _settings.IncludePrereleaseUpdates = value;
        Persist();
    }

    partial void OnConfirmSshHostKeyOnFirstConnectionChanged(bool value)
    {
        _settings.ConfirmSshHostKeyOnFirstConnection = value;
        Persist();
    }

    partial void OnBlockChangedSshHostKeysChanged(bool value)
    {
        _settings.BlockChangedSshHostKeys = value;
        Persist();
    }

    partial void OnRecordTerminalSessionsChanged(bool value)
    {
        _settings.RecordTerminalSessions = value;
        Persist();
    }

    partial void OnRecordingRetentionDaysChanged(int value)
    {
        var normalized = Math.Clamp(value, 1, 3650);
        if (value != normalized)
        {
            RecordingRetentionDays = normalized;
            return;
        }

        _settings.RecordingRetentionDays = normalized;
        Persist();
    }

    partial void OnUiLanguageChanged(string value)
    {
        var language = NormalizeLanguage(value);
        if (!string.Equals(value, language, StringComparison.Ordinal))
        {
            UiLanguage = language;
            return;
        }

        _settings.UiLanguage = language;
        Persist();
        _applyLanguage(language);
        OnPropertyChanged(nameof(IsChineseSelected));
        OnPropertyChanged(nameof(IsEnglishSelected));
    }

    private void Persist()
    {
        _saveSettings(_settings);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(GeneralText));
        OnPropertyChanged(nameof(StartupText));
        OnPropertyChanged(nameof(AppearanceText));
        OnPropertyChanged(nameof(UpdatesText));
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(SshSecurityText));
        OnPropertyChanged(nameof(ConfirmSshHostKeyOnFirstConnectionText));
        OnPropertyChanged(nameof(BlockChangedSshHostKeysText));
        OnPropertyChanged(nameof(KnownHostsText));
        OnPropertyChanged(nameof(NoKnownHostsText));
        OnPropertyChanged(nameof(RecordingText));
        OnPropertyChanged(nameof(RecordTerminalSessionsText));
        OnPropertyChanged(nameof(RecordingDescriptionText));
        OnPropertyChanged(nameof(RecordingRetentionText));
        OnPropertyChanged(nameof(RecordingDaysText));
        OnPropertyChanged(nameof(ShowSessionManagerOnStartupText));
        OnPropertyChanged(nameof(ShowTabBarText));
        OnPropertyChanged(nameof(AutoCheckForUpdatesText));
        OnPropertyChanged(nameof(IncludePrereleaseUpdatesText));
        OnPropertyChanged(nameof(ChineseText));
        OnPropertyChanged(nameof(EnglishText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(IsChineseSelected));
        OnPropertyChanged(nameof(IsEnglishSelected));
        foreach (var item in KnownHosts)
            item.NotifyLocalizationChanged();
    }

    private void RemoveKnownHost(KnownSshHostKeyItemViewModel item)
    {
        _hostKeyTrust.RemoveKnownHost(
            item.HostKey.Host,
            item.HostKey.Port,
            item.HostKey.KeyType);
        ReloadKnownHosts();
    }

    private void ReloadKnownHosts()
    {
        KnownHosts.Clear();
        foreach (var host in _hostKeyTrust.GetKnownHosts())
            KnownHosts.Add(new KnownSshHostKeyItemViewModel(host, RemoveKnownHost));
        OnPropertyChanged(nameof(HasKnownHosts));
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, LocalizationService.English, StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.English
            : LocalizationService.Chinese;
    }

    private string Text(string key) => _localization.Text(key);
}
