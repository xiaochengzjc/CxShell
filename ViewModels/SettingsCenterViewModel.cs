using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    Agent,
    ConnectionAudit,
    SessionRecordings,
    TrustedHosts,
    About,
    SupportDonate
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

public sealed class AboutDependencyViewModel
{
    public AboutDependencyViewModel(string name, string license)
    {
        Name = name;
        License = license;
    }

    public string Name { get; }
    public string License { get; }
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
    public SettingsNavigationItemViewModel AgentNavigation { get; }
    public SettingsNavigationItemViewModel AuditNavigation { get; }
    public SettingsNavigationItemViewModel RecordingsNavigation { get; }
    public SettingsNavigationItemViewModel TrustedHostsNavigation { get; }
    public SettingsNavigationItemViewModel AboutNavigation { get; }
    public SettingsNavigationItemViewModel SupportDonateNavigation { get; }
    public IReadOnlyList<AboutDependencyViewModel> AboutDependencies { get; }

    public ICommand CheckForUpdatesCommand { get; }

    private readonly string _appVersion;

    public string TitleText => Text("Settings.Title");
    public string ApplicationTitleText => Text("Settings.Application");
    public string ApplicationDescriptionText => Text("Settings.ApplicationDescription");
    public string AgentTitleText => Text("Settings.Agent");
    public string AgentDescriptionText => Text("Settings.AgentDescription");
    public string AuditTitleText => Text("Settings.ConnectionAudit");
    public string AuditDescriptionText => Text("Settings.ConnectionAuditDescription");
    public string RecordingsTitleText => Text("Settings.SessionRecordings");
    public string RecordingsDescriptionText => Text("Settings.SessionRecordingsDescription");
    public string TrustedHostsTitleText => Text("Settings.TrustedHosts");
    public string TrustedHostsDescriptionText => Text("Settings.TrustedHostsDescription");
    public string AboutTitleText => Text("Settings.About");
    public string AboutDescriptionText => Text("Settings.AboutDescription");
    public string UpdateDescriptionText => Text("Settings.UpdateDescription");
    public string AboutAppNameText => "CxShell";
    public string AboutVersionText => string.Format(Text("About.Version"), _appVersion);
    public string AboutContentText => Text("About.Description");
    public string AboutBuiltWithText => Text("About.BuiltWith");
    public string AboutGitHubLabelText => Text("About.GitHub");
    public string AboutGitHubUrlText => "https://github.com/xiaochengzjc/CxShell";
    public string AboutVersionBadgeText => $"v{_appVersion}";
    public string AboutStatusText => Text("About.Status");
    public string AboutSystemInfoText => Text("About.SystemInfo");
    public string AboutFrameworkLabelText => Text("About.Framework");
    public string AboutRuntimeLabelText => Text("About.Runtime");
    public string AboutSshLabelText => Text("About.SshLibrary");
    public string AboutOperatingSystemLabelText => Text("About.OperatingSystem");
    public string AboutConfigurationLabelText => Text("About.Configuration");
    public string AboutFrameworkValueText => "Avalonia UI 12.1.1";
    public string AboutRuntimeValueText => RuntimeInformation.FrameworkDescription;
    public string AboutSshValueText => "SSH.NET 2024.2.0";
    public string AboutOperatingSystemValueText => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
    public string AboutConfigurationValueText => SessionStorageService.GetStorageDirectory();
    public string AboutUpdateText => Text("About.Update");
    public string AboutUpdateDescriptionText => Text("About.UpdateDescription");
    public string AboutOpenSourceText => Text("About.OpenSource");
    public string AboutOpenSourceDescriptionText => Text("About.OpenSourceDescription");
    public string AboutLicenseLabelText => Text("About.License");
    public string AboutSupportText => Text("About.Support");
    public string AboutSupportDescriptionText => Text("About.SupportDescription");
    public string AboutOpenGitHubText => Text("About.OpenGitHub");
    public string AboutKoFiText => Text("About.KoFi");
    public string AboutKoFiUrlText => "https://ko-fi.com/xiaochengzjc";
    public string DonationIntroText => Text("Donation.Intro");
    public string DonationContributionTitleText => Text("Donation.ContributionTitle");
    public string DonationContributionDescriptionText => Text("Donation.ContributionDescription");
    public string DonationDomesticTitleText => Text("Donation.DomesticTitle");
    public string DonationDomesticDescriptionText => Text("Donation.DomesticDescription");
    public string DonationAlipayText => Text("Donation.Alipay");
    public string DonationWeChatPayText => Text("Donation.WeChatPay");
    public string DonationInternationalTitleText => Text("Donation.InternationalTitle");
    public string DonationInternationalDescriptionText => Text("Donation.InternationalDescription");
    public string DonationOpenKoFiText => Text("Donation.OpenKoFi");
    public string DonationOpenGitHubText => Text("Donation.OpenGitHub");
    public string DonationThanksText => Text("Donation.Thanks");
    public string DonationKoFiUrlText => "https://ko-fi.com/xiaochengzjc";
    public string DonationWeChatIdText => "ruochujiangzi";
    public string SupportDonateTitleText => Text("Settings.SupportDonate");
    public string SupportDonateDescriptionText => Text("Settings.SupportDonateDescription");
    public string UpdateCurrentVersionText => string.Format(Text("Settings.CurrentVersion"), _appVersion);
    public string CheckForUpdatesText => Text("Settings.CheckForUpdates");
    public string CloseText => Text("ApplicationSettings.Close");

    public bool IsApplicationSelected => SelectedSection == SettingsSection.Application;
    public bool IsAgentSelected => SelectedSection == SettingsSection.Agent;
    public bool IsAuditSelected => SelectedSection == SettingsSection.ConnectionAudit;
    public bool IsRecordingsSelected => SelectedSection == SettingsSection.SessionRecordings;
    public bool IsTrustedHostsSelected => SelectedSection == SettingsSection.TrustedHosts;
    public bool IsAboutSelected => SelectedSection == SettingsSection.About;
    public bool IsSupportDonateSelected => SelectedSection == SettingsSection.SupportDonate;

    public string SelectedTitleText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditTitleText,
        SettingsSection.Agent => AgentTitleText,
        SettingsSection.SessionRecordings => RecordingsTitleText,
        SettingsSection.TrustedHosts => TrustedHostsTitleText,
        SettingsSection.About => AboutTitleText,
        SettingsSection.SupportDonate => SupportDonateTitleText,
        _ => ApplicationTitleText
    };

    public string SelectedDescriptionText => SelectedSection switch
    {
        SettingsSection.ConnectionAudit => AuditDescriptionText,
        SettingsSection.Agent => AgentDescriptionText,
        SettingsSection.SessionRecordings => RecordingsDescriptionText,
        SettingsSection.TrustedHosts => TrustedHostsDescriptionText,
        SettingsSection.About => AboutDescriptionText,
        SettingsSection.SupportDonate => SupportDonateDescriptionText,
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

        ApplicationSettings = new ApplicationSettingsViewModel(
            settings,
            saveSettings,
            applyLanguage,
            applyTheme);
        ConnectionAudit = new ConnectionAuditViewModel(connectionAuditService);
        SessionRecordings = new SessionRecordingViewModel(SessionRecordingService.Shared.Store);

        ApplicationNavigation = new SettingsNavigationItemViewModel(
            ApplicationTitleText,
            CreateIcon(AntDesignIconKind.SettingOutlined));
        AgentNavigation = new SettingsNavigationItemViewModel(
            AgentTitleText,
            CreateIcon(AntDesignIconKind.RobotOutlined));
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
        SupportDonateNavigation = new SettingsNavigationItemViewModel(
            SupportDonateTitleText,
            CreateIcon(AntDesignIconKind.HeartOutlined));
        AboutDependencies =
        [
            new AboutDependencyViewModel("Avalonia UI", "MIT"),
            new AboutDependencyViewModel("AtomUI", "LGPL-3.0"),
            new AboutDependencyViewModel("SSH.NET", "MIT"),
            new AboutDependencyViewModel("FluentFTP", "MIT"),
            new AboutDependencyViewModel("FreeRDP", "Apache-2.0"),
            new AboutDependencyViewModel("Velopack", "MIT")
        ];

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public void Select(SettingsSection section)
    {
        SelectedSection = section;
    }

    [RelayCommand]
    private void SelectApplication() => Select(SettingsSection.Application);

    [RelayCommand]
    private void SelectAgent() => Select(SettingsSection.Agent);

    [RelayCommand]
    private void SelectAudit() => Select(SettingsSection.ConnectionAudit);

    [RelayCommand]
    private void SelectRecordings() => Select(SettingsSection.SessionRecordings);

    [RelayCommand]
    private void SelectTrustedHosts() => Select(SettingsSection.TrustedHosts);

    [RelayCommand]
    private void SelectAbout() => Select(SettingsSection.About);

    [RelayCommand]
    private void SelectSupportDonate() => Select(SettingsSection.SupportDonate);

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        ApplicationSettings.Dispose();
        SessionRecordings.Dispose();
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsApplicationSelected));
        OnPropertyChanged(nameof(IsAgentSelected));
        OnPropertyChanged(nameof(IsAuditSelected));
        OnPropertyChanged(nameof(IsRecordingsSelected));
        OnPropertyChanged(nameof(IsTrustedHostsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        OnPropertyChanged(nameof(IsSupportDonateSelected));
        OnPropertyChanged(nameof(SelectedTitleText));
        OnPropertyChanged(nameof(SelectedDescriptionText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ApplicationTitleText));
        OnPropertyChanged(nameof(ApplicationDescriptionText));
        OnPropertyChanged(nameof(AgentTitleText));
        OnPropertyChanged(nameof(AgentDescriptionText));
        OnPropertyChanged(nameof(AuditTitleText));
        OnPropertyChanged(nameof(AuditDescriptionText));
        OnPropertyChanged(nameof(RecordingsTitleText));
        OnPropertyChanged(nameof(RecordingsDescriptionText));
        OnPropertyChanged(nameof(TrustedHostsTitleText));
        OnPropertyChanged(nameof(TrustedHostsDescriptionText));
        OnPropertyChanged(nameof(AboutTitleText));
        OnPropertyChanged(nameof(AboutDescriptionText));
        OnPropertyChanged(nameof(UpdateDescriptionText));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(AboutContentText));
        OnPropertyChanged(nameof(AboutBuiltWithText));
        OnPropertyChanged(nameof(AboutGitHubLabelText));
        OnPropertyChanged(nameof(AboutVersionBadgeText));
        OnPropertyChanged(nameof(AboutStatusText));
        OnPropertyChanged(nameof(AboutSystemInfoText));
        OnPropertyChanged(nameof(AboutFrameworkLabelText));
        OnPropertyChanged(nameof(AboutRuntimeLabelText));
        OnPropertyChanged(nameof(AboutSshLabelText));
        OnPropertyChanged(nameof(AboutOperatingSystemLabelText));
        OnPropertyChanged(nameof(AboutConfigurationLabelText));
        OnPropertyChanged(nameof(AboutUpdateText));
        OnPropertyChanged(nameof(AboutUpdateDescriptionText));
        OnPropertyChanged(nameof(AboutOpenSourceText));
        OnPropertyChanged(nameof(AboutOpenSourceDescriptionText));
        OnPropertyChanged(nameof(AboutLicenseLabelText));
        OnPropertyChanged(nameof(AboutSupportText));
        OnPropertyChanged(nameof(AboutSupportDescriptionText));
        OnPropertyChanged(nameof(AboutOpenGitHubText));
        OnPropertyChanged(nameof(AboutKoFiText));
        OnPropertyChanged(nameof(DonationIntroText));
        OnPropertyChanged(nameof(DonationContributionTitleText));
        OnPropertyChanged(nameof(DonationContributionDescriptionText));
        OnPropertyChanged(nameof(DonationDomesticTitleText));
        OnPropertyChanged(nameof(DonationDomesticDescriptionText));
        OnPropertyChanged(nameof(DonationAlipayText));
        OnPropertyChanged(nameof(DonationWeChatPayText));
        OnPropertyChanged(nameof(DonationInternationalTitleText));
        OnPropertyChanged(nameof(DonationInternationalDescriptionText));
        OnPropertyChanged(nameof(DonationOpenKoFiText));
        OnPropertyChanged(nameof(DonationOpenGitHubText));
        OnPropertyChanged(nameof(DonationThanksText));
        OnPropertyChanged(nameof(SupportDonateTitleText));
        OnPropertyChanged(nameof(SupportDonateDescriptionText));
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
