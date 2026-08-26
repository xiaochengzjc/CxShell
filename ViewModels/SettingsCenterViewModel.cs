using System;
using System.Windows.Input;
using AtomUI.Icons.AntDesign;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public enum SettingsSection
{
    Application,
    ConnectionAudit,
    SessionRecordings,
    TrustedHosts,
    About,
    Update
}

public sealed class SettingsNavigationItemViewModel
{
    public SettingsNavigationItemViewModel(string text, PathIcon icon)
    {
        Text = text;
        Icon = icon;
    }

    public string Text { get; }
    public PathIcon Icon { get; }
}

public partial class SettingsCenterViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization = LocalizationService.Shared;

    [ObservableProperty]
    private SettingsSection _selectedSection = SettingsSection.Application;

    public ApplicationSettingsViewModel ApplicationSettings { get; }
    public ConnectionAuditViewModel ConnectionAudit { get; }
    public SessionRecordingViewModel SessionRecordings { get; }

    public SettingsNavigationItemViewModel ApplicationNavigation { get; }
    public SettingsNavigationItemViewModel AuditNavigation { get; }
    public SettingsNavigationItemViewModel RecordingsNavigation { get; }
    public SettingsNavigationItemViewModel TrustedHostsNavigation { get; }
    public SettingsNavigationItemViewModel AboutNavigation { get; }
    public SettingsNavigationItemViewModel UpdateNavigation { get; }

    public ICommand CheckForUpdatesCommand { get; }

    private readonly string _appVersion;

    public string TitleText => Text("Settings.Title");
    public string ApplicationTitleText => Text("Settings.Application");
    public string ApplicationDescriptionText => Text("Settings.ApplicationDescription");
    public string AuditTitleText => Text("Settings.ConnectionAudit");
    public string AuditDescriptionText => Text("Settings.ConnectionAuditDescription");
    public string RecordingsTitleText => Text("Settings.SessionRecordings");
    public string RecordingsDescriptionText => Text("Settings.SessionRecordingsDescription");
    public string TrustedHostsTitleText => Text("Settings.TrustedHosts");
    public string TrustedHostsDescriptionText => Text("Settings.TrustedHostsDescription");
    public string AboutTitleText => Text("Settings.About");
    public string AboutDescriptionText => Text("Settings.AboutDescription");
    public string UpdateTitleText => Text("Settings.Update");
    public string UpdateDescriptionText => Text("Settings.UpdateDescription");
    public string AboutAppNameText => "CxShell";
    public string AboutVersionText => string.Format(Text("About.Version"), _appVersion);
    public string AboutContentText => Text("About.Description");
    public string AboutBuiltWithText => Text("About.BuiltWith");
    public string AboutGitHubLabelText => Text("About.GitHub");
    public string AboutGitHubUrlText => "https://github.com/xiaochengzjc/CxShell";
    public string UpdateCurrentVersionText => string.Format(Text("Settings.CurrentVersion"), _appVersion);
    public string CheckForUpdatesText => Text("Settings.CheckForUpdates");
    public string CloseText => Text("ApplicationSettings.Close");

    public bool IsApplicationSelected => SelectedSection == SettingsSection.Application;
    public bool IsAuditSelected => SelectedSection == SettingsSection.ConnectionAudit;
    public bool IsRecordingsSelected => SelectedSection == SettingsSection.SessionRecordings;
    public bool IsTrustedHostsSelected => SelectedSection == SettingsSection.TrustedHosts;
    public bool IsAboutSelected => SelectedSection == SettingsSection.About;
    public bool IsUpdateSelected => SelectedSection == SettingsSection.Update;

    public string SelectedTitleText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditTitleText,
        SettingsSection.SessionRecordings => RecordingsTitleText,
        SettingsSection.TrustedHosts => TrustedHostsTitleText,
        SettingsSection.About => AboutTitleText,
        SettingsSection.Update => UpdateTitleText,
        _ => ApplicationTitleText
    };

    public string SelectedDescriptionText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditDescriptionText,
        SettingsSection.SessionRecordings => RecordingsDescriptionText,
        SettingsSection.TrustedHosts => TrustedHostsDescriptionText,
        SettingsSection.About => AboutDescriptionText,
        SettingsSection.Update => UpdateDescriptionText,
        _ => ApplicationDescriptionText
    };

    public SettingsCenterViewModel(
        ApplicationSettings settings,
        Action<ApplicationSettings> saveSettings,
        Action<string> applyLanguage,
        Action<string> applyTheme,
        ConnectionAuditService connectionAuditService,
        string appVersion,
        ICommand checkForUpdatesCommand)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion;
        CheckForUpdatesCommand = checkForUpdatesCommand ?? throw new ArgumentNullException(nameof(checkForUpdatesCommand));

        ApplicationSettings = new ApplicationSettingsViewModel(settings, saveSettings, applyLanguage, applyTheme);
        ConnectionAudit = new ConnectionAuditViewModel(connectionAuditService);
        SessionRecordings = new SessionRecordingViewModel(SessionRecordingService.Shared.Store);

        ApplicationNavigation = new SettingsNavigationItemViewModel(
            ApplicationTitleText,
            CreateIcon(AntDesignIconKind.SettingOutlined));
        AuditNavigation = new SettingsNavigationItemViewModel(
            AuditTitleText,
            CreateIcon(AntDesignIconKind.FileSearchOutlined));
        RecordingsNavigation = new SettingsNavigationItemViewModel(
            RecordingsTitleText,
            CreateIcon(AntDesignIconKind.PlayCircleOutlined));
        TrustedHostsNavigation = new SettingsNavigationItemViewModel(
            TrustedHostsTitleText,
            CreateIcon(AntDesignIconKind.SafetyCertificateOutlined));
        AboutNavigation = new SettingsNavigationItemViewModel(
            AboutTitleText,
            CreateIcon(AntDesignIconKind.InfoCircleOutlined));
        UpdateNavigation = new SettingsNavigationItemViewModel(
            UpdateTitleText,
            CreateIcon(AntDesignIconKind.CloudSyncOutlined));

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public void Select(SettingsSection section)
    {
        SelectedSection = section;
    }

    [RelayCommand]
    private void SelectApplication() => Select(SettingsSection.Application);

    [RelayCommand]
    private void SelectAudit() => Select(SettingsSection.ConnectionAudit);

    [RelayCommand]
    private void SelectRecordings() => Select(SettingsSection.SessionRecordings);

    [RelayCommand]
    private void SelectTrustedHosts() => Select(SettingsSection.TrustedHosts);

    [RelayCommand]
    private void SelectAbout() => Select(SettingsSection.About);

    [RelayCommand]
    private void SelectUpdate() => Select(SettingsSection.Update);

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        ApplicationSettings.Dispose();
        SessionRecordings.Dispose();
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsApplicationSelected));
        OnPropertyChanged(nameof(IsAuditSelected));
        OnPropertyChanged(nameof(IsRecordingsSelected));
        OnPropertyChanged(nameof(IsTrustedHostsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(IsUpdateSelected));
        OnPropertyChanged(nameof(SelectedTitleText));
        OnPropertyChanged(nameof(SelectedDescriptionText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ApplicationTitleText));
        OnPropertyChanged(nameof(ApplicationDescriptionText));
        OnPropertyChanged(nameof(AuditTitleText));
        OnPropertyChanged(nameof(AuditDescriptionText));
        OnPropertyChanged(nameof(RecordingsTitleText));
        OnPropertyChanged(nameof(RecordingsDescriptionText));
        OnPropertyChanged(nameof(TrustedHostsTitleText));
        OnPropertyChanged(nameof(TrustedHostsDescriptionText));
        OnPropertyChanged(nameof(AboutTitleText));
        OnPropertyChanged(nameof(AboutDescriptionText));
        OnPropertyChanged(nameof(UpdateTitleText));
        OnPropertyChanged(nameof(UpdateDescriptionText));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(AboutContentText));
        OnPropertyChanged(nameof(AboutBuiltWithText));
        OnPropertyChanged(nameof(AboutGitHubLabelText));
        OnPropertyChanged(nameof(UpdateCurrentVersionText));
        OnPropertyChanged(nameof(CheckForUpdatesText));
        OnPropertyChanged(nameof(SelectedTitleText));
        OnPropertyChanged(nameof(SelectedDescriptionText));
        OnPropertyChanged(nameof(CloseText));
    }

    private string Text(string key) => _localization.Text(key);

    private static PathIcon CreateIcon(AntDesignIconKind kind)
    {
        return (PathIcon)new AntDesignIconProvider(kind).ProvideValue(null!);
    }
}
