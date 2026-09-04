using System;
using System.Collections.ObjectModel;
using AtomUI.Desktop.Controls;
using CxShell.Models;
using CxShell.Services;
using CxShell.Services.Agent;
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
    private readonly Action<string> _applyTheme;
    private readonly LocalizationService _localization = LocalizationService.Shared;
    private readonly SshHostKeyTrustService _hostKeyTrust = SshHostKeyTrustService.Shared;

    [ObservableProperty] private bool _showSessionManagerOnStartup;
    [ObservableProperty] private bool _showTabBar;
    [ObservableProperty] private bool _showSftpPanel;
    [ObservableProperty] private bool _showMonitorPanel;
    [ObservableProperty] private bool _showAgentPanel;
    [ObservableProperty] private bool _enableCommandSuggestions;
    [ObservableProperty] private string _themeMode;
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private bool _includePrereleaseUpdates;
    [ObservableProperty] private bool _confirmSshHostKeyOnFirstConnection;
    [ObservableProperty] private bool _blockChangedSshHostKeys;
    [ObservableProperty] private bool _recordTerminalSessions;
    [ObservableProperty] private int _recordingRetentionDays;
    [ObservableProperty] private string _uiLanguage;
    [ObservableProperty] private bool _agentEnabled;
    [ObservableProperty] private ISelectOption? _agentPermissionModeOption;
    [ObservableProperty] private bool _agentAllowCommandExecution;
    [ObservableProperty] private bool _agentRequireApprovalForDangerousCommands;
    [ObservableProperty] private bool _agentRequireApprovalForChangeCommands;
    [ObservableProperty] private bool _agentReadOnlyMode;
    [ObservableProperty] private string _agentAllowedCommandPrefixes;
    [ObservableProperty] private string _agentBlockedCommandPrefixes;
    [ObservableProperty] private string _agentProviderName;
    [ObservableProperty] private string _agentBaseUrl;
    [ObservableProperty] private string _agentModel;
    [ObservableProperty] private ISelectOption? _agentModelOption;
    [ObservableProperty] private string _agentApiKey;
    [ObservableProperty] private int _agentRequestTimeoutSeconds;
    [ObservableProperty] private bool _agentWebEnabled;
    [ObservableProperty] private string _agentSearxngBaseUrl;
    [ObservableProperty] private bool _agentAllowPrivateNetwork;
    [ObservableProperty] private string _agentAllowedPrivateHosts;
    [ObservableProperty] private int _agentWebMaxResults;
    [ObservableProperty] private int _agentWebMaxFetchCharacters;
    [ObservableProperty] private string _agentStatusText = string.Empty;
    [ObservableProperty] private bool _agentIsReady;
    [ObservableProperty] private string _agentModelCatalogStatus = string.Empty;

    private readonly AgentModelCatalogClient _agentModelCatalogClient = new();

    public ObservableCollection<KnownSshHostKeyItemViewModel> KnownHosts { get; } = new();
    public ObservableCollection<ISelectOption> AgentPermissionModeOptions { get; } = new();
    public ObservableCollection<ISelectOption> AgentModelOptions { get; } = new();

    public string TitleText => Text("ApplicationSettings.Title");
    public string GeneralText => Text("ApplicationSettings.General");
    public string StartupText => Text("ApplicationSettings.Startup");
    public string AppearanceText => Text("ApplicationSettings.Appearance");
    public string ThemeText => Text("ApplicationSettings.Theme");
    public string DarkThemeText => Text("ApplicationSettings.DarkTheme");
    public string LightThemeText => Text("ApplicationSettings.LightTheme");
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
    public string ShowSftpPanelText => Text("ApplicationSettings.ShowSftpPanel");
    public string ShowMonitorPanelText => Text("ApplicationSettings.ShowMonitorPanel");
    public string ShowAgentPanelText => Text("ApplicationSettings.ShowAgentPanel");
    public string EnableCommandSuggestionsText => Text("ApplicationSettings.EnableCommandSuggestions");
    public string AutoCheckForUpdatesText => Text("ApplicationSettings.AutoCheckForUpdates");
    public string IncludePrereleaseUpdatesText => Text("ApplicationSettings.IncludePrereleaseUpdates");
    public string AgentText => Text("ApplicationSettings.Agent");
    public string AgentDescriptionText => Text("ApplicationSettings.AgentDescription");
    public string AgentEnabledText => Text("ApplicationSettings.AgentEnabled");
    public string AgentPermissionModeText => Text("ApplicationSettings.AgentPermissionMode");
    public string AgentPermissionModeDescriptionText => AgentPermissionPolicy.NormalizePermissionMode(
        AgentPermissionModeOption?.Content?.ToString()) switch
    {
        AgentPermissionPolicy.AskBeforeEachCommandMode => Text("ApplicationSettings.AgentPermissionModeAskDescription"),
        AgentPermissionPolicy.FullAccessMode => Text("ApplicationSettings.AgentPermissionModeFullDescription"),
        _ => Text("ApplicationSettings.AgentPermissionModeRiskDescription")
    };
    public string AgentAllowCommandExecutionText => Text("ApplicationSettings.AgentAllowCommandExecution");
    public string AgentRequireApprovalForDangerousCommandsText => Text("ApplicationSettings.AgentRequireApprovalForDangerousCommands");
    public string AgentRequireApprovalForChangeCommandsText => Text("ApplicationSettings.AgentRequireApprovalForChangeCommands");
    public string AgentReadOnlyModeText => Text("ApplicationSettings.AgentReadOnlyMode");
    public string AgentAllowedCommandPrefixesText => Text("ApplicationSettings.AgentAllowedCommandPrefixes");
    public string AgentBlockedCommandPrefixesText => Text("ApplicationSettings.AgentBlockedCommandPrefixes");
    public string AgentCommandPolicyDescriptionText => Text("ApplicationSettings.AgentCommandPolicyDescription");
    public string AgentProviderText => Text("ApplicationSettings.AgentProvider");
    public string AgentProviderTypeText => AgentProviderConfiguration.IsResponsesProvider(EnsureAgentProvider())
        ? Text("ApplicationSettings.AgentProviderTypeResponses")
        : Text("ApplicationSettings.AgentProviderTypeChat");
    public string AgentProviderNameText => Text("ApplicationSettings.AgentProviderName");
    public string AgentBaseUrlText => Text("ApplicationSettings.AgentBaseUrl");
    public string AgentModelText => Text("ApplicationSettings.AgentModel");
    public string AgentPlanKeyText => Text("ApplicationSettings.AgentPlanKey");
    public string AgentPlanKeyDescriptionText => Text("ApplicationSettings.AgentPlanKeyDescription");
    public string AgentRoutinRegistrationText => Text("ApplicationSettings.AgentRoutinRegistration");
    public string AgentRoutinRegistrationDescriptionText => Text("ApplicationSettings.AgentRoutinRegistrationDescription");
    public string AgentRoutinRegistrationUrlText => "https://routin.ai/register?planInviteCode=PE32VR2X";
    public string AgentOpenRoutinRegistrationText => Text("ApplicationSettings.AgentOpenRoutinRegistration");
    public string AgentRequestTimeoutText => Text("ApplicationSettings.AgentRequestTimeout");
    public string AgentRefreshModelsText => Text("ApplicationSettings.AgentRefreshModels");
    public string AgentModelCatalogStatusText => AgentModelCatalogStatus;
    public string AgentWebText => Text("ApplicationSettings.AgentWeb");
    public string AgentWebEnabledText => Text("ApplicationSettings.AgentWebEnabled");
    public string AgentSearxngBaseUrlText => Text("ApplicationSettings.AgentSearxngBaseUrl");
    public string AgentAllowPrivateNetworkText => Text("ApplicationSettings.AgentAllowPrivateNetwork");
    public string AgentAllowedPrivateHostsText => Text("ApplicationSettings.AgentAllowedPrivateHosts");
    public string AgentWebMaxResultsText => Text("ApplicationSettings.AgentWebMaxResults");
    public string AgentWebMaxFetchCharactersText => Text("ApplicationSettings.AgentWebMaxFetchCharacters");
    public string AgentSecondsText => Text("ApplicationSettings.Seconds");
    public string AgentReadyText => Text("ApplicationSettings.AgentReady");
    public string AgentUseRoutinPresetText => Text("ApplicationSettings.AgentUseRoutinPreset");
    public string ChineseText => Text("Language.Chinese");
    public string EnglishText => Text("Language.English");
    public string CloseText => Text("ApplicationSettings.Close");
    public bool HasKnownHosts => KnownHosts.Count > 0;

    public bool IsDarkThemeSelected
    {
        get => string.Equals(ThemeMode, ApplicationSettings.DarkThemeMode, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                ThemeMode = ApplicationSettings.DarkThemeMode;
        }
    }

    public bool IsLightThemeSelected
    {
        get => string.Equals(ThemeMode, ApplicationSettings.LightThemeMode, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
                ThemeMode = ApplicationSettings.LightThemeMode;
        }
    }

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
        Action<string> applyLanguage,
        Action<string> applyTheme)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _applyLanguage = applyLanguage ?? throw new ArgumentNullException(nameof(applyLanguage));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));

        _showSessionManagerOnStartup = settings.ShowSessionManagerOnStartup;
        _showTabBar = settings.ShowTabBar;
        _showSftpPanel = settings.ShowSftpPanel;
        _showMonitorPanel = settings.ShowMonitorPanel;
        _showAgentPanel = settings.ShowAgentPanel;
        _enableCommandSuggestions = settings.EnableCommandSuggestions;
        _themeMode = NormalizeThemeMode(settings.ThemeMode);
        _autoCheckForUpdates = settings.AutoCheckForUpdates;
        _includePrereleaseUpdates = settings.IncludePrereleaseUpdates;
        _confirmSshHostKeyOnFirstConnection = settings.ConfirmSshHostKeyOnFirstConnection;
        _blockChangedSshHostKeys = settings.BlockChangedSshHostKeys;
        _recordTerminalSessions = settings.RecordTerminalSessions;
        _recordingRetentionDays = settings.RecordingRetentionDays;
        _uiLanguage = NormalizeLanguage(settings.UiLanguage);
        var agentProvider = EnsureAgentProvider();
        _agentEnabled = agentProvider.Enabled;
        var permissionMode = AgentPermissionPolicy.NormalizePermissionMode(settings.AgentPermissionMode);
        if (string.IsNullOrEmpty(permissionMode))
            permissionMode = AgentPermissionPolicy.RiskBasedApprovalMode;
        settings.AgentPermissionMode = permissionMode;
        RebuildAgentPermissionModeOptions(permissionMode);
        _agentAllowCommandExecution = settings.AgentAllowCommandExecution;
        _agentRequireApprovalForDangerousCommands = settings.AgentRequireApprovalForDangerousCommands;
        _agentRequireApprovalForChangeCommands = settings.AgentRequireApprovalForChangeCommands;
        _agentReadOnlyMode = settings.AgentReadOnlyMode;
        _agentAllowedCommandPrefixes = settings.AgentAllowedCommandPrefixes ?? string.Empty;
        _agentBlockedCommandPrefixes = settings.AgentBlockedCommandPrefixes ?? string.Empty;
        _agentProviderName = agentProvider.Name ?? string.Empty;
        _agentBaseUrl = agentProvider.BaseUrl ?? string.Empty;
        _agentModel = AgentProviderConfiguration.GetEffectiveModelId(agentProvider);
        _agentApiKey = AgentProviderConfiguration.GetApiKey(agentProvider);
        _agentRequestTimeoutSeconds = agentProvider.RequestTimeoutSeconds;
        RebuildAgentModelOptions();
        _agentModelOption = AgentModelOptions.FirstOrDefault(option =>
            string.Equals(option.Content?.ToString(), _agentModel, StringComparison.OrdinalIgnoreCase));
        var web = settings.AgentWeb ??= new AgentWebSettings();
        web.Normalize();
        _agentWebEnabled = web.Enabled;
        _agentSearxngBaseUrl = web.SearxngBaseUrl;
        _agentAllowPrivateNetwork = web.AllowPrivateNetwork;
        _agentAllowedPrivateHosts = web.AllowedPrivateHosts;
        _agentWebMaxResults = web.MaxResults;
        _agentWebMaxFetchCharacters = web.MaxFetchCharacters;
        _hostKeyTrust.Configure(settings);
        ReloadKnownHosts();
        RefreshAgentProviderStatus();
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

    partial void OnShowSftpPanelChanged(bool value)
    {
        _settings.ShowSftpPanel = value;
        Persist();
    }

    partial void OnShowMonitorPanelChanged(bool value)
    {
        _settings.ShowMonitorPanel = value;
        Persist();
    }

    partial void OnShowAgentPanelChanged(bool value)
    {
        _settings.ShowAgentPanel = value;
        Persist();
    }

    partial void OnEnableCommandSuggestionsChanged(bool value)
    {
        _settings.EnableCommandSuggestions = value;
        Persist();
    }

    partial void OnThemeModeChanged(string value)
    {
        var normalized = NormalizeThemeMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            ThemeMode = normalized;
            return;
        }

        _settings.ThemeMode = normalized;
        Persist();
        _applyTheme(normalized);
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
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

    partial void OnAgentEnabledChanged(bool value)
    {
        EnsureAgentProvider().Enabled = value;
        PersistAgentProvider();
    }

    partial void OnAgentPermissionModeOptionChanged(ISelectOption? value)
    {
        var mode = AgentPermissionPolicy.NormalizePermissionMode(value?.Content?.ToString());
        if (string.IsNullOrEmpty(mode))
            return;

        _settings.AgentPermissionMode = mode;
        switch (mode)
        {
            case AgentPermissionPolicy.AskBeforeEachCommandMode:
            case AgentPermissionPolicy.RiskBasedApprovalMode:
                _settings.AgentRequireApprovalForDangerousCommands = true;
                _settings.AgentRequireApprovalForChangeCommands = true;
                break;
            case AgentPermissionPolicy.FullAccessMode:
                _settings.AgentRequireApprovalForDangerousCommands = false;
                _settings.AgentRequireApprovalForChangeCommands = false;
                break;
        }

        Persist();
        OnPropertyChanged(nameof(AgentPermissionModeDescriptionText));
    }

    partial void OnAgentAllowCommandExecutionChanged(bool value)
    {
        _settings.AgentAllowCommandExecution = value;
        Persist();
    }

    partial void OnAgentRequireApprovalForDangerousCommandsChanged(bool value)
    {
        _settings.AgentRequireApprovalForDangerousCommands = value;
        Persist();
    }

    partial void OnAgentRequireApprovalForChangeCommandsChanged(bool value)
    {
        _settings.AgentRequireApprovalForChangeCommands = value;
        Persist();
    }

    partial void OnAgentReadOnlyModeChanged(bool value)
    {
        _settings.AgentReadOnlyMode = value;
        Persist();
    }

    partial void OnAgentAllowedCommandPrefixesChanged(string value)
    {
        _settings.AgentAllowedCommandPrefixes = value ?? string.Empty;
        Persist();
    }

    partial void OnAgentBlockedCommandPrefixesChanged(string value)
    {
        _settings.AgentBlockedCommandPrefixes = value ?? string.Empty;
        Persist();
    }

    partial void OnAgentProviderNameChanged(string value)
    {
        EnsureAgentProvider().Name = value.Trim();
        PersistAgentProvider();
    }

    partial void OnAgentBaseUrlChanged(string value)
    {
        EnsureAgentProvider().BaseUrl = value.Trim();
        PersistAgentProvider();
    }

    partial void OnAgentModelChanged(string value)
    {
        var provider = EnsureAgentProvider();
        provider.Model = value?.Trim() ?? string.Empty;
        var active = AgentProviderConfiguration.EnsureActiveModel(provider);
        active.ModelId = provider.Model;
        if (string.IsNullOrWhiteSpace(active.Name))
            active.Name = provider.Model;
        RebuildAgentModelOptions();
        PersistAgentProvider();
    }

    partial void OnAgentModelOptionChanged(ISelectOption? value)
    {
        var model = value?.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(model) && !string.Equals(model, AgentModel, StringComparison.Ordinal))
            AgentModel = model;
    }

    partial void OnAgentApiKeyChanged(string value)
    {
        AgentProviderConfiguration.SetApiKey(EnsureAgentProvider(), value);
        PersistAgentProvider();
    }

    partial void OnAgentRequestTimeoutSecondsChanged(int value)
    {
        var normalized = Math.Clamp(value, 5, 600);
        if (value != normalized)
        {
            AgentRequestTimeoutSeconds = normalized;
            return;
        }

        EnsureAgentProvider().RequestTimeoutSeconds = normalized;
        PersistAgentProvider();
    }

    partial void OnAgentWebEnabledChanged(bool value)
    {
        EnsureWebSettings().Enabled = value;
        Persist();
    }

    partial void OnAgentSearxngBaseUrlChanged(string value)
    {
        EnsureWebSettings().SearxngBaseUrl = value?.Trim() ?? string.Empty;
        Persist();
    }

    partial void OnAgentAllowPrivateNetworkChanged(bool value)
    {
        EnsureWebSettings().AllowPrivateNetwork = value;
        Persist();
    }

    partial void OnAgentAllowedPrivateHostsChanged(string value)
    {
        EnsureWebSettings().AllowedPrivateHosts = value ?? string.Empty;
        Persist();
    }

    partial void OnAgentWebMaxResultsChanged(int value)
    {
        var normalized = Math.Clamp(value, 1, 20);
        if (value != normalized)
        {
            AgentWebMaxResults = normalized;
            return;
        }

        EnsureWebSettings().MaxResults = normalized;
        Persist();
    }

    partial void OnAgentWebMaxFetchCharactersChanged(int value)
    {
        var normalized = Math.Clamp(value, 2_000, 400_000);
        if (value != normalized)
        {
            AgentWebMaxFetchCharacters = normalized;
            return;
        }

        EnsureWebSettings().MaxFetchCharacters = normalized;
        Persist();
    }

    [RelayCommand]
    private async Task RefreshAgentModels()
    {
        var provider = EnsureAgentProvider();
        AgentModelCatalogStatus = Text("ApplicationSettings.AgentModelCatalogLoading");
        OnPropertyChanged(nameof(AgentModelCatalogStatusText));
        // Keep the continuation on Avalonia's UI context because the command
        // updates bound collections and status properties below.
        var result = await _agentModelCatalogClient.FetchAsync(provider);
        if (result.Success)
        {
            AgentProviderConfiguration.ApplyModelCatalog(provider, result.Models);
            AgentModel = AgentProviderConfiguration.GetEffectiveModelId(provider);
            AgentModelCatalogStatus = string.Format(Text("ApplicationSettings.AgentModelCatalogLoaded"), result.Models.Count);
        }
        else
        {
            AgentModelCatalogStatus = result.Error ?? Text("ApplicationSettings.AgentModelCatalogFailed");
        }

        RebuildAgentModelOptions();
        OnPropertyChanged(nameof(AgentModelCatalogStatusText));
        PersistAgentProvider();
    }

    [RelayCommand]
    private void UseRoutinPreset()
    {
        var preset = AgentProviderPresets.CreateRoutinPlan();
        var provider = EnsureAgentProvider();
        provider.Type = preset.Type;
        provider.BuiltinId = preset.BuiltinId;
        AgentEnabled = true;
        AgentProviderName = preset.Name;
        AgentBaseUrl = preset.BaseUrl;
        AgentModel = preset.Model;
        AgentRequestTimeoutSeconds = preset.RequestTimeoutSeconds;
        RefreshAgentProviderStatus();
        OnPropertyChanged(nameof(AgentProviderTypeText));
    }

    private void PersistAgentProvider()
    {
        RefreshAgentProviderStatus();
        Persist();
    }

    private void RebuildAgentPermissionModeOptions(string? preferredMode = null)
    {
        var mode = AgentPermissionPolicy.NormalizePermissionMode(
            preferredMode ?? _settings.AgentPermissionMode);
        if (string.IsNullOrEmpty(mode))
            mode = AgentPermissionPolicy.RiskBasedApprovalMode;

        AgentPermissionModeOptions.Clear();
        AgentPermissionModeOptions.Add(new SelectOption
        {
            Header = Text("ApplicationSettings.AgentPermissionModeAsk"),
            Content = AgentPermissionPolicy.AskBeforeEachCommandMode
        });
        AgentPermissionModeOptions.Add(new SelectOption
        {
            Header = Text("ApplicationSettings.AgentPermissionModeRisk"),
            Content = AgentPermissionPolicy.RiskBasedApprovalMode
        });
        AgentPermissionModeOptions.Add(new SelectOption
        {
            Header = Text("ApplicationSettings.AgentPermissionModeFull"),
            Content = AgentPermissionPolicy.FullAccessMode
        });

        AgentPermissionModeOption = AgentPermissionModeOptions.FirstOrDefault(option =>
            string.Equals(option.Content?.ToString(), mode, StringComparison.Ordinal));
        OnPropertyChanged(nameof(AgentPermissionModeDescriptionText));
    }

    private AgentProviderSettings EnsureAgentProvider()
    {
        return _settings.AgentProvider ??= new AgentProviderSettings();
    }

    private AgentWebSettings EnsureWebSettings()
    {
        return _settings.AgentWeb ??= new AgentWebSettings();
    }

    private void RebuildAgentModelOptions()
    {
        AgentModelOptions.Clear();
        var provider = EnsureAgentProvider();
        foreach (var model in provider.Models
                     .Where(model => model.Enabled && !string.IsNullOrWhiteSpace(model.ModelId))
                     .Select(model => model.ModelId.Trim())
                     .Concat(provider.AvailableModels ?? [])
                     .Concat([provider.Model])
                     .Where(model => !string.IsNullOrWhiteSpace(model))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(100))
        {
            AgentModelOptions.Add(new SelectOption { Header = model, Content = model });
        }
    }

    private void RefreshAgentProviderStatus()
    {
        var validation = AgentProviderConfiguration.Validate(EnsureAgentProvider());
        AgentIsReady = validation.IsValid;
        AgentStatusText = validation.Status switch
        {
            AgentProviderValidationStatus.Valid => AgentReadyText,
            AgentProviderValidationStatus.Disabled => Text("ApplicationSettings.AgentStatusDisabled"),
            AgentProviderValidationStatus.MissingBaseUrl => Text("ApplicationSettings.AgentStatusMissingBaseUrl"),
            AgentProviderValidationStatus.InvalidBaseUrl => Text("ApplicationSettings.AgentStatusInvalidBaseUrl"),
            AgentProviderValidationStatus.InsecureBaseUrl => Text("ApplicationSettings.AgentStatusInsecureBaseUrl"),
            AgentProviderValidationStatus.MissingModel => Text("ApplicationSettings.AgentStatusMissingModel"),
            AgentProviderValidationStatus.MissingApiKey => Text("ApplicationSettings.AgentStatusMissingPlanKey"),
            AgentProviderValidationStatus.UnsupportedProvider => Text("ApplicationSettings.AgentStatusUnsupported"),
            _ => validation.Message
        };
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
        OnPropertyChanged(nameof(ThemeText));
        OnPropertyChanged(nameof(DarkThemeText));
        OnPropertyChanged(nameof(LightThemeText));
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
        OnPropertyChanged(nameof(ShowSftpPanelText));
        OnPropertyChanged(nameof(ShowMonitorPanelText));
        OnPropertyChanged(nameof(ShowAgentPanelText));
        OnPropertyChanged(nameof(EnableCommandSuggestionsText));
        OnPropertyChanged(nameof(AutoCheckForUpdatesText));
        OnPropertyChanged(nameof(IncludePrereleaseUpdatesText));
        OnPropertyChanged(nameof(AgentText));
        OnPropertyChanged(nameof(AgentDescriptionText));
        OnPropertyChanged(nameof(AgentEnabledText));
        RebuildAgentPermissionModeOptions(
            AgentPermissionPolicy.NormalizePermissionMode(_settings.AgentPermissionMode));
        OnPropertyChanged(nameof(AgentPermissionModeText));
        OnPropertyChanged(nameof(AgentPermissionModeDescriptionText));
        OnPropertyChanged(nameof(AgentAllowCommandExecutionText));
        OnPropertyChanged(nameof(AgentRequireApprovalForDangerousCommandsText));
        OnPropertyChanged(nameof(AgentRequireApprovalForChangeCommandsText));
        OnPropertyChanged(nameof(AgentReadOnlyModeText));
        OnPropertyChanged(nameof(AgentAllowedCommandPrefixesText));
        OnPropertyChanged(nameof(AgentBlockedCommandPrefixesText));
        OnPropertyChanged(nameof(AgentCommandPolicyDescriptionText));
        OnPropertyChanged(nameof(AgentProviderText));
        OnPropertyChanged(nameof(AgentProviderTypeText));
        OnPropertyChanged(nameof(AgentProviderNameText));
        OnPropertyChanged(nameof(AgentBaseUrlText));
        OnPropertyChanged(nameof(AgentModelText));
        OnPropertyChanged(nameof(AgentPlanKeyText));
        OnPropertyChanged(nameof(AgentPlanKeyDescriptionText));
        OnPropertyChanged(nameof(AgentRoutinRegistrationText));
        OnPropertyChanged(nameof(AgentRoutinRegistrationDescriptionText));
        OnPropertyChanged(nameof(AgentOpenRoutinRegistrationText));
        OnPropertyChanged(nameof(AgentRequestTimeoutText));
        OnPropertyChanged(nameof(AgentRefreshModelsText));
        OnPropertyChanged(nameof(AgentModelCatalogStatusText));
        OnPropertyChanged(nameof(AgentWebText));
        OnPropertyChanged(nameof(AgentWebEnabledText));
        OnPropertyChanged(nameof(AgentSearxngBaseUrlText));
        OnPropertyChanged(nameof(AgentAllowPrivateNetworkText));
        OnPropertyChanged(nameof(AgentAllowedPrivateHostsText));
        OnPropertyChanged(nameof(AgentWebMaxResultsText));
        OnPropertyChanged(nameof(AgentWebMaxFetchCharactersText));
        OnPropertyChanged(nameof(AgentSecondsText));
        OnPropertyChanged(nameof(AgentReadyText));
        OnPropertyChanged(nameof(AgentUseRoutinPresetText));
        RefreshAgentProviderStatus();
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

    private static string NormalizeThemeMode(string? themeMode)
    {
        return string.Equals(themeMode, ApplicationSettings.LightThemeMode, StringComparison.OrdinalIgnoreCase)
            ? ApplicationSettings.LightThemeMode
            : ApplicationSettings.DarkThemeMode;
    }

    private string Text(string key) => _localization.Text(key);
}
