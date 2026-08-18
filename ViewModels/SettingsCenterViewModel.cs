using System;
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
    TrustedHosts
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

    public string TitleText => Text("Settings.Title");
    public string ApplicationTitleText => Text("Settings.Application");
    public string ApplicationDescriptionText => Text("Settings.ApplicationDescription");
    public string AuditTitleText => Text("Settings.ConnectionAudit");
    public string AuditDescriptionText => Text("Settings.ConnectionAuditDescription");
    public string RecordingsTitleText => Text("Settings.SessionRecordings");
    public string RecordingsDescriptionText => Text("Settings.SessionRecordingsDescription");
    public string TrustedHostsTitleText => Text("Settings.TrustedHosts");
    public string TrustedHostsDescriptionText => Text("Settings.TrustedHostsDescription");
    public string CloseText => Text("ApplicationSettings.Close");

    public bool IsApplicationSelected => SelectedSection == SettingsSection.Application;
    public bool IsAuditSelected => SelectedSection == SettingsSection.ConnectionAudit;
    public bool IsRecordingsSelected => SelectedSection == SettingsSection.SessionRecordings;
    public bool IsTrustedHostsSelected => SelectedSection == SettingsSection.TrustedHosts;

    public string SelectedTitleText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditTitleText,
        SettingsSection.SessionRecordings => RecordingsTitleText,
        SettingsSection.TrustedHosts => TrustedHostsTitleText,
        _ => ApplicationTitleText
    };

    public string SelectedDescriptionText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditDescriptionText,
        SettingsSection.SessionRecordings => RecordingsDescriptionText,
        SettingsSection.TrustedHosts => TrustedHostsDescriptionText,
        _ => ApplicationDescriptionText
    };

    public SettingsCenterViewModel(
        ApplicationSettings settings,
        Action<ApplicationSettings> saveSettings,
        Action<string> applyLanguage,
        ConnectionAuditService connectionAuditService)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        ApplicationSettings = new ApplicationSettingsViewModel(settings, saveSettings, applyLanguage);
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
