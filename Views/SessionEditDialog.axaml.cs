using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AtomUI;
using AtomUI.Controls;
using AtomUI.Controls.Utils;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class SessionEditDialog : AtomUI.Desktop.Controls.Window
{
    private bool _isLoaded;
    private bool _saveAndConnectRequested;
    private bool _isInitializingSelections;

    public bool ShouldConnect { get; private set; }

    protected override Type StyleKeyOverride { get; } = typeof(AtomUI.Desktop.Controls.Window);

    public SessionEditDialog()
    {
        InitializeComponent();
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (_isLoaded)
            return;

        _isLoaded = true;
        SaveBtn.Click += OnSaveClick;
        ConnectBtn.Click += OnConnectClick;
        CancelBtn.Click += OnCancelClick;
        _isInitializingSelections = true;
        SetSelectedProtocol();
        SetSelectedProxyOptions();
        SetSelectedSshOptions();
        SetSelectedTelnetOptions();
        SetSelectedRloginOptions();
        SetSelectedSerialOptions();
        SetSelectedRdpOptions();
        _isInitializingSelections = false;
        CategoryTree.SelectionChanged += OnCategorySelectionChanged;
        ProxySelect.SelectionChanged += OnProxySelectionChanged;
        ShowCategoryPage("Connection", "连接");
    }

    private void OnCategorySelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (CategoryTree.SelectedItem is not AtomUI.Desktop.Controls.TreeViewItem item)
            return;

        var key = item.Value?.ToString() ?? "Connection";
        var title = item.Header?.ToString() ?? "连接";
        ShowCategoryPage(key, title);
    }

    private void ShowCategoryPage(string key, string title)
    {
        if (key == "SshSftp")
            title = "连接 > SSH > SFTP (Secure File Transfer)";
        else if (key == "Rlogin")
            title = "连接 > RLOGIN";

        if (key == "Proxy")
            title = "连接 > 代理";

        else if (key == "KeepAlive")
            title = "连接 > 保持活动状态";
        PageTitleText.Text = title;
        ConnectionPage.IsVisible = key == "Connection";
        AuthPage.IsVisible = key == "Auth";
        LoginPromptPage.IsVisible = key == "LoginPrompt";
        SshPage.IsVisible = key == "Ssh";
        SshSecurityPage.IsVisible = key == "SshSecurity";
        SshTunnelPage.IsVisible = key == "SshTunnel";
        TelnetPage.IsVisible = key == "Telnet";
        ProxyPage.IsVisible = key == "Proxy";
        KeepAlivePage.IsVisible = key == "KeepAlive";
        RloginPage.IsVisible = key == "Rlogin";
        SftpPage.IsVisible = key == "SshSftp";
        SerialPage.IsVisible = key == "Serial";
        RdpPage.IsVisible = key == "Rdp";
        PlaceholderPage.IsVisible = key != "Connection" && key != "Auth" && key != "LoginPrompt" && key != "Ssh" && key != "SshSecurity" && key != "SshTunnel" && key != "Telnet" && key != "Proxy" && key != "KeepAlive" && key != "Rlogin" && key != "SshSftp" && key != "Serial" && key != "Rdp";
        PlaceholderTitleText.Text = title;
    }

    private void OnProtocolSelectionChanged(object? sender, SelectSelectionChangedEventArgs e)
    {
        var protocolText = GetSelectedProtocolText();
        SetProtocol(protocolText, updateDefaultPort: !_isInitializingSelections);
    }

    private void SetProtocol(string protocolText, bool updateDefaultPort)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        vm.Protocol = protocolText;

        if (!Enum.TryParse<SessionProtocol>(vm.Protocol, out var protocol))
            protocol = SessionProtocol.SSH;

        if (updateDefaultPort)
            PortBox.Text = SessionEditViewModel.GetDefaultPort(protocol).ToString();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _saveAndConnectRequested = false;
        SaveCurrentSession();
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        _saveAndConnectRequested = true;
        SaveCurrentSession();
    }

    private void SaveCurrentSession()
    {
        if (DataContext is SessionEditViewModel vm)
        {
            vm.SessionName = NameBox?.Text ?? string.Empty;
            vm.Protocol = GetSelectedProtocolText();
            vm.Host = HostBox?.Text ?? string.Empty;
            vm.Port = PortBox?.Text ?? string.Empty;
            vm.Username = UsernameBox?.Text ?? string.Empty;
            vm.SshRemoteCommand = SshRemoteCommandBox?.Text ?? vm.SshRemoteCommand;
            vm.SshVersionPolicy = GetSelectedOptionText(SshVersionPolicySelect, vm.SshVersionPolicy);
            vm.SshCipherAlgorithms = GetSelectedAlgorithmText(SshCipherSelect, vm.SshCipherAlgorithms);
            vm.SshMacAlgorithms = GetSelectedAlgorithmText(SshMacSelect, vm.SshMacAlgorithms);
            vm.SshKeyExchangeAlgorithms = GetSelectedAlgorithmText(SshKeyExchangeSelect, vm.SshKeyExchangeAlgorithms);
            vm.SshX11UseXmanager = SshX11XmanagerButton.IsChecked == true;
            vm.SshX11Display = SshX11DisplayBox?.Text ?? vm.SshX11Display;
            vm.TelnetXDisplayLocation = TelnetXDisplayLocationBox?.Text ?? vm.TelnetXDisplayLocation;
            vm.TelnetOptionMode = TelnetActiveOptionButton.IsChecked == true ? "Active" : "Passive";
            vm.TelnetUsernamePrompt = TelnetUsernamePromptBox?.Text ?? vm.TelnetUsernamePrompt;
            vm.TelnetPasswordPrompt = TelnetPasswordPromptBox?.Text ?? vm.TelnetPasswordPrompt;
            vm.RloginPasswordPrompt = RloginPasswordPromptBox?.Text ?? vm.RloginPasswordPrompt;
            vm.RloginTerminalSpeed = GetSelectedOptionText(RloginTerminalSpeedSelect, vm.RloginTerminalSpeed);
            vm.SftpLocalStartDirectory = SftpLocalStartDirectoryBox?.Text ?? vm.SftpLocalStartDirectory;
            vm.SftpRemoteStartDirectory = SftpRemoteStartDirectoryBox?.Text ?? vm.SftpRemoteStartDirectory;
            vm.SftpCustomServerCommand = SftpCustomServerCommandBox?.Text ?? vm.SftpCustomServerCommand;
            vm.SerialPortName = GetSelectedOptionText(SerialPortSelect, vm.SerialPortName);
            vm.SerialBaudRate = GetSelectedOptionText(SerialBaudRateSelect, vm.SerialBaudRate);
            vm.SerialDataBits = GetSelectedOptionText(SerialDataBitsSelect, vm.SerialDataBits);
            vm.SerialStopBits = GetSelectedOptionText(SerialStopBitsSelect, vm.SerialStopBits);
            vm.SerialParity = GetSelectedOptionText(SerialParitySelect, vm.SerialParity);
            vm.SerialFlowControl = GetSelectedOptionText(SerialFlowControlSelect, vm.SerialFlowControl);
            vm.RdpWindowSize = GetSelectedOptionText(RdpWindowSizeSelect, vm.RdpWindowSize);
            ApplyRdpPresetSize(vm);
            vm.RdpDesktopWidth = RdpWidthBox?.Text ?? vm.RdpDesktopWidth;
            vm.RdpDesktopHeight = RdpHeightBox?.Text ?? vm.RdpDesktopHeight;
            vm.RdpResizeMode = GetSelectedOptionText(RdpResizeModeSelect, vm.RdpResizeMode);
            vm.RdpScreenScale = GetSelectedOptionText(RdpScreenScaleSelect, vm.RdpScreenScale);
            vm.RdpColorQuality = GetSelectedOptionText(RdpColorQualitySelect, vm.RdpColorQuality);
            vm.RdpAudioMode = GetSelectedOptionText(RdpAudioModeSelect, vm.RdpAudioMode);

            vm.SaveCommand.Execute(null);
            if (vm.SavedSession != null)
            {
                ShouldConnect = _saveAndConnectRequested;
                _saveAndConnectRequested = false;
                Close();
                return;
            }

            ShouldConnect = false;
            _saveAndConnectRequested = false;

            if (vm.HasValidationError)
            {
                ShowCategoryPage("Connection", "连接");
                FocusFirstInvalidField(vm);
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnBrowseSftpLocalDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var options = new FolderPickerOpenOptions
        {
            Title = "Select local SFTP start folder",
            AllowMultiple = false
        };

        var currentPath = SftpLocalStartDirectoryBox.Text;
        if (!string.IsNullOrWhiteSpace(currentPath) && System.IO.Directory.Exists(currentPath))
            options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(currentPath);

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        var folder = folders.FirstOrDefault();
        if (folder != null)
            SftpLocalStartDirectoryBox.Text = folder.Path.LocalPath;
    }

    private async void OnAddSshTunnelRuleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        var rule = await ShowSshTunnelRuleDialogAsync(null);
        if (rule == null)
            return;

        vm.SshTunnelRules.Add(rule);
        vm.SelectedSshTunnelRule = rule;
    }

    private async void OnEditSshTunnelRuleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm || vm.SelectedSshTunnelRule == null)
            return;

        var editing = SessionEditViewModel.CloneTunnelRule(vm.SelectedSshTunnelRule);
        var rule = await ShowSshTunnelRuleDialogAsync(editing);
        if (rule == null)
            return;

        var index = vm.SshTunnelRules.IndexOf(vm.SelectedSshTunnelRule);
        if (index < 0)
            return;

        vm.SshTunnelRules[index] = rule;
        vm.SelectedSshTunnelRule = rule;
    }

    private void OnDeleteSshTunnelRuleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm || vm.SelectedSshTunnelRule == null)
            return;

        var index = vm.SshTunnelRules.IndexOf(vm.SelectedSshTunnelRule);
        vm.SshTunnelRules.Remove(vm.SelectedSshTunnelRule);
        if (vm.SshTunnelRules.Count == 0)
        {
            vm.SelectedSshTunnelRule = null;
            return;
        }

        vm.SelectedSshTunnelRule = vm.SshTunnelRules[Math.Clamp(index, 0, vm.SshTunnelRules.Count - 1)];
    }

    private string GetSelectedProtocolText()
    {
        return ProtocolSelect.SelectedOption?.Content?.ToString()
               ?? (DataContext is SessionEditViewModel vm ? vm.Protocol : SessionProtocol.SSH.ToString());
    }

    private void SetSelectedProtocol()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        ProtocolSelect.SelectedOption = vm.ProtocolOptions.FirstOrDefault(option =>
            string.Equals(option.Content?.ToString(), vm.Protocol, StringComparison.OrdinalIgnoreCase));
    }

    private void FocusFirstInvalidField(SessionEditViewModel vm)
    {
        if (vm.IsNameInvalid)
        {
            NameBox.Focus();
            return;
        }

        if (vm.IsHostInvalid)
        {
            HostBox.Focus();
            return;
        }

        if (vm.IsPortInvalid)
        {
            PortBox.Focus();
            return;
        }

        if (vm.IsSerialPortInvalid)
        {
            ShowCategoryPage("Serial", "Serial");
            SerialPortSelect.Focus();
        }
    }

    private void SetSelectedTelnetOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        TelnetActiveOptionButton.IsChecked = string.Equals(vm.TelnetOptionMode, "Active", StringComparison.OrdinalIgnoreCase);
        TelnetPassiveOptionButton.IsChecked = TelnetActiveOptionButton.IsChecked != true;
    }

    private void SetSelectedSshOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        SelectOption(SshVersionPolicySelect, vm.SshVersionPolicy);
        SelectAlgorithmOption(SshCipherSelect, vm.SshCipherAlgorithms);
        SelectAlgorithmOption(SshMacSelect, vm.SshMacAlgorithms);
        SelectAlgorithmOption(SshKeyExchangeSelect, vm.SshKeyExchangeAlgorithms);
        SshX11XmanagerButton.IsChecked = vm.SshX11UseXmanager;
        SshX11DisplayButton.IsChecked = !vm.SshX11UseXmanager;
    }

    private void SetSelectedProxyOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        SelectOption(ProxySelect, vm.SelectedProxyKey);
    }

    private void OnProxySelectionChanged(object? sender, SelectSelectionChangedEventArgs e)
    {
        if (_isInitializingSelections || DataContext is not SessionEditViewModel vm)
            return;

        var selected = ProxySelect.SelectedOption?.Content?.ToString();
        if (Guid.TryParse(selected, out var proxyId))
        {
            vm.SelectProxy(proxyId);
            return;
        }

        if (!string.Equals(selected, "None", StringComparison.OrdinalIgnoreCase))
            return;

        vm.ClearProxy();
        SelectOption(ProxySelect, "None");
    }

    private async void OnBrowseProxyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        await ShowProxyListDialogAsync(vm);
        SetSelectedProxyOptions();
    }

    private async Task ShowProxyListDialogAsync(SessionEditViewModel vm)
    {
        var proxies = new ObservableCollection<ProxySettings>(
            vm.ProxyServers.Select(SessionEditViewModel.CloneProxy));
        RefreshProxyNextProxyDisplay(proxies);

        var dialog = new Avalonia.Controls.Window
        {
            Title = "列表代理",
            Width = 846,
            Height = 646,
            MinWidth = 720,
            MinHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            ShowInTaskbar = false
        };

        var grid = new DataGrid
        {
            ItemsSource = proxies,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            SizeType = SizeType.Middle,
            GridLinesVisibility = DataGridGridLinesVisibility.Vertical,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            IsHideOnSinglePage = true,
            Margin = new Thickness(20, 16, 20, 0)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.Name)), Width = new DataGridLength(230) });
        grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.TypeDisplay)), Width = new DataGridLength(120) });
        grid.Columns.Add(new DataGridTextColumn { Header = "主机", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.Host)), Width = new DataGridLength(160) });
        grid.Columns.Add(new DataGridTextColumn { Header = "端口", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.PortDisplay)), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "用户名", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.Username)), Width = new DataGridLength(110) });
        grid.Columns.Add(new DataGridTextColumn { Header = "下一代理", Binding = new Avalonia.Data.Binding(nameof(ProxySettings.NextProxyDisplay)), Width = new DataGridLength(110) });

        var addButton = CreateDialogButton("添加(A)", 118);
        var editButton = CreateDialogButton("编辑(E)", 118);
        var deleteButton = CreateDialogButton("删除(R)", 118);
        var closeButton = CreateDialogButton("关闭", 140);

        void UpdateListButtons()
        {
            var hasSelection = grid.SelectedItem is ProxySettings;
            editButton.IsEnabled = hasSelection;
            deleteButton.IsEnabled = hasSelection;
        }

        addButton.Click += async (_, _) =>
        {
            var proxy = await ShowProxySettingsDialogAsync(null, proxies);
            if (proxy == null)
                return;

            proxies.Add(proxy);
            grid.SelectedItem = proxy;
            RefreshProxyNextProxyDisplay(proxies);
            UpdateListButtons();
        };

        editButton.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not ProxySettings current)
                return;

            var proxy = await ShowProxySettingsDialogAsync(SessionEditViewModel.CloneProxy(current), proxies);
            if (proxy == null)
                return;

            var index = proxies.IndexOf(current);
            if (index >= 0)
                proxies[index] = proxy;
            grid.SelectedItem = proxy;
            RefreshProxyNextProxyDisplay(proxies);
            UpdateListButtons();
        };

        deleteButton.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not ProxySettings current)
                return;

            if (!await ShowConfirmDialogAsync(dialog, "删除代理", $"确定删除代理“{current.DisplayName}”吗？"))
                return;

            foreach (var proxy in proxies.Where(proxy => proxy.NextProxyId == current.Id))
                proxy.NextProxyId = null;
            proxies.Remove(current);
            RefreshProxyNextProxyDisplay(proxies);
            UpdateListButtons();
        };

        closeButton.Click += (_, _) => dialog.Close();
        grid.SelectionChanged += (_, _) => UpdateListButtons();

        var leftButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14
        };
        leftButtons.Children.Add(addButton);
        leftButtons.Children.Add(editButton);
        leftButtons.Children.Add(deleteButton);

        var buttonGrid = new Grid
        {
            Margin = new Thickness(20, 20, 20, 16),
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        buttonGrid.Children.Add(leftButtons);
        buttonGrid.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);

        UpdateListButtons();

        var content = new DockPanel();
        DockPanel.SetDock(buttonGrid, Dock.Bottom);
        content.Children.Add(buttonGrid);
        content.Children.Add(grid);
        dialog.Content = content;

        await dialog.ShowDialog(this);

        var selectedId = grid.SelectedItem is ProxySettings selected
            ? selected.Id
            : (vm.CreateProxySettings().IsEnabled ? vm.CreateProxySettings().Id : (Guid?)null);
        if (selectedId.HasValue && proxies.All(proxy => proxy.Id != selectedId.Value))
            selectedId = null;

        vm.ReplaceProxyServers(proxies, selectedId);
    }

    private async Task<ProxySettings?> ShowProxySettingsDialogAsync(
        ProxySettings? source,
        ObservableCollection<ProxySettings> existingProxies)
    {
        var editing = source ?? new ProxySettings { Protocol = ProxyProtocol.Socks5, Port = 1080 };
        ProxySettings? result = null;
        var dialog = new Avalonia.Controls.Window
        {
            Title = "代理服务器设置",
            Width = 636,
            Height = 646,
            MinWidth = 560,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var nameBox = CreateLineEdit(editing.Name);
        var protocolSelect = new Select
        {
            Mode = SelectMode.Single,
            ShouldUseOverlayPopup = false,
            OptionsSource = CreateProxyProtocolOptions(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SelectOption(protocolSelect, editing.Protocol == ProxyProtocol.None ? ProxyProtocol.Socks5.ToString() : editing.Protocol.ToString());

        var hostBox = CreateLineEdit(editing.Host);
        var portBox = CreateLineEdit(editing.Port > 0 ? editing.Port.ToString() : "1080");
        portBox.Width = 116;
        portBox.HorizontalAlignment = HorizontalAlignment.Left;
        var usernameBox = CreateLineEdit(editing.Username);
        var passwordBox = CreateLineEdit(editing.Password);
        var sessionFileCheck = new AtomUI.Desktop.Controls.CheckBox
        {
            Content = "会话文件(S)",
            IsEnabled = false,
            IsChecked = editing.UseSessionFile
        };
        var sessionFileBox = CreateLineEdit(editing.SessionFilePath);
        sessionFileBox.IsEnabled = false;
        var sessionFileButton = CreateDialogButton("...", 32);
        sessionFileButton.IsEnabled = false;
        sessionFileButton.Click += async (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(dialog);
            if (topLevel == null)
                return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择会话文件",
                AllowMultiple = false
            });
            var file = files.FirstOrDefault();
            if (file != null)
                sessionFileBox.Text = file.Path.LocalPath;
        };

        var nextProxySelect = new Select
        {
            Mode = SelectMode.Single,
            ShouldUseOverlayPopup = false,
            OptionsSource = CreateNextProxyOptions(existingProxies, editing.Id),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SelectOption(nextProxySelect, editing.NextProxyId?.ToString() ?? "None");
        var browseNextButton = CreateDialogButton("浏览(B)...", 150);
        browseNextButton.Click += (_, _) => nextProxySelect.Focus();

        void UpdateProxyProtocolState()
        {
            var protocol = GetSelectedProxyProtocol(protocolSelect);
            var isSocks4 = protocol is ProxyProtocol.Socks4 or ProxyProtocol.Socks4A;
            var isJumpHost = protocol == ProxyProtocol.JumpHost;

            usernameBox.IsEnabled = !isSocks4;
            passwordBox.IsEnabled = !isSocks4;
            if (isSocks4)
            {
                usernameBox.Text = string.Empty;
                passwordBox.Text = string.Empty;
            }

            sessionFileCheck.IsEnabled = isJumpHost;
            sessionFileBox.IsEnabled = isJumpHost && sessionFileCheck.IsChecked == true;
            sessionFileButton.IsEnabled = isJumpHost && sessionFileCheck.IsChecked == true;
            nextProxySelect.IsEnabled = isJumpHost;
            browseNextButton.IsEnabled = isJumpHost;
            if (!isJumpHost)
            {
                sessionFileCheck.IsChecked = false;
                SelectOption(nextProxySelect, "None");
            }
        }

        protocolSelect.SelectionChanged += (_, _) => UpdateProxyProtocolState();
        sessionFileCheck.IsCheckedChanged += (_, _) => UpdateProxyProtocolState();

        var errorText = new Avalonia.Controls.TextBlock
        {
            IsVisible = false,
            Foreground = Avalonia.Media.Brushes.Firebrick,
            Margin = new Thickness(20, 0, 20, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var form = new Grid
        {
            Margin = new Thickness(20, 16, 20, 10),
            RowDefinitions = new RowDefinitions("Auto,18,1,18,Auto,10,Auto,10,Auto,10,Auto,10,Auto,14,Auto,10,Auto,10,Auto,10,Auto"),
            ColumnDefinitions = new ColumnDefinitions("160,*,150")
        };
        AddFormLabel(form, "名称(N):", 0);
        form.Children.Add(nameBox);
        Grid.SetRow(nameBox, 0);
        Grid.SetColumn(nameBox, 1);
        Grid.SetColumnSpan(nameBox, 2);

        var separator = new Border { Background = Avalonia.Media.Brushes.Gray, Opacity = 0.6 };
        form.Children.Add(separator);
        Grid.SetRow(separator, 2);
        Grid.SetColumnSpan(separator, 3);

        AddFormLabel(form, "类型(T):", 4);
        form.Children.Add(protocolSelect);
        Grid.SetRow(protocolSelect, 4);
        Grid.SetColumn(protocolSelect, 1);
        Grid.SetColumnSpan(protocolSelect, 2);

        AddFormLabel(form, "主机(H):", 6);
        form.Children.Add(hostBox);
        Grid.SetRow(hostBox, 6);
        Grid.SetColumn(hostBox, 1);
        Grid.SetColumnSpan(hostBox, 2);

        AddFormLabel(form, "端口号(P):", 8);
        form.Children.Add(portBox);
        Grid.SetRow(portBox, 8);
        Grid.SetColumn(portBox, 1);

        AddFormLabel(form, "用户名(U):", 10);
        form.Children.Add(usernameBox);
        Grid.SetRow(usernameBox, 10);
        Grid.SetColumn(usernameBox, 1);
        Grid.SetColumnSpan(usernameBox, 2);

        AddFormLabel(form, "密码(A):", 12);
        form.Children.Add(passwordBox);
        Grid.SetRow(passwordBox, 12);
        Grid.SetColumn(passwordBox, 1);
        Grid.SetColumnSpan(passwordBox, 2);

        form.Children.Add(sessionFileCheck);
        Grid.SetRow(sessionFileCheck, 14);
        Grid.SetColumnSpan(sessionFileCheck, 3);

        form.Children.Add(sessionFileBox);
        Grid.SetRow(sessionFileBox, 16);
        Grid.SetColumn(sessionFileBox, 0);
        Grid.SetColumnSpan(sessionFileBox, 2);
        form.Children.Add(sessionFileButton);
        Grid.SetRow(sessionFileButton, 16);
        Grid.SetColumn(sessionFileButton, 2);

        AddFormLabel(form, "下一代理(X):", 18);
        form.Children.Add(nextProxySelect);
        Grid.SetRow(nextProxySelect, 20);
        Grid.SetColumn(nextProxySelect, 0);
        Grid.SetColumnSpan(nextProxySelect, 2);
        form.Children.Add(browseNextButton);
        Grid.SetRow(browseNextButton, 20);
        Grid.SetColumn(browseNextButton, 2);

        var okButton = CreateDialogButton("确定", 138);
        okButton.ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary;
        var cancelButton = CreateDialogButton("取消", 138);

        okButton.Click += (_, _) =>
        {
            errorText.IsVisible = false;
            nameBox.Status = InputControlStatus.Default;
            hostBox.Status = InputControlStatus.Default;
            portBox.Status = InputControlStatus.Default;

            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                nameBox.Status = InputControlStatus.Error;
                errorText.Text = "请输入代理名称。";
                errorText.IsVisible = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(hostBox.Text))
            {
                hostBox.Status = InputControlStatus.Error;
                errorText.Text = "请输入代理服务器主机。";
                errorText.IsVisible = true;
                return;
            }

            if (!int.TryParse(portBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            {
                portBox.Status = InputControlStatus.Error;
                errorText.Text = "端口必须是 1 到 65535 之间的整数。";
                errorText.IsVisible = true;
                return;
            }

            var protocolText = protocolSelect.SelectedOption?.Content?.ToString();
            if (!Enum.TryParse<ProxyProtocol>(protocolText, out var protocol) || protocol == ProxyProtocol.None)
                protocol = ProxyProtocol.Socks5;

            var isSocks4 = protocol is ProxyProtocol.Socks4 or ProxyProtocol.Socks4A;
            var isJumpHost = protocol == ProxyProtocol.JumpHost;
            Guid? nextProxyId = null;
            if (isJumpHost && Guid.TryParse(nextProxySelect.SelectedOption?.Content?.ToString(), out var parsedNextProxyId))
                nextProxyId = parsedNextProxyId;

            result = new ProxySettings
            {
                Id = editing.Id,
                Name = nameBox.Text.Trim(),
                Protocol = protocol,
                Host = hostBox.Text.Trim(),
                Port = port,
                Username = isSocks4 ? string.Empty : usernameBox.Text?.Trim() ?? string.Empty,
                Password = isSocks4 ? string.Empty : passwordBox.Text ?? string.Empty,
                UseSessionFile = isJumpHost && sessionFileCheck.IsChecked == true,
                SessionFilePath = isJumpHost ? sessionFileBox.Text?.Trim() ?? string.Empty : string.Empty,
                NextProxyId = nextProxyId
            };
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 14,
            Margin = new Thickness(20, 8, 20, 16)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(errorText, Dock.Bottom);
        content.Children.Add(buttons);
        content.Children.Add(errorText);
        content.Children.Add(form);
        dialog.Content = content;
        UpdateProxyProtocolState();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<ProxySettings?> ShowProxyDialogAsync(ProxySettings current)
    {
        ProxySettings? result = null;
        var dialog = new Avalonia.Controls.Window
        {
            Title = "代理服务器",
            Width = 500,
            Height = 360,
            MinWidth = 460,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var protocolSelect = new Select
        {
            Mode = SelectMode.Single,
            ShouldUseOverlayPopup = false,
            OptionsSource = new ObservableCollection<ISelectOption>
            {
                new SelectOption { Header = "HTTP CONNECT", Content = ProxyProtocol.Http.ToString() },
                new SelectOption { Header = "SOCKS4", Content = ProxyProtocol.Socks4.ToString() },
                new SelectOption { Header = "SOCKS5", Content = ProxyProtocol.Socks5.ToString() }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SelectOption(protocolSelect, current.Protocol == ProxyProtocol.None ? ProxyProtocol.Http.ToString() : current.Protocol.ToString());

        var hostBox = CreateLineEdit(current.Host);
        var portBox = CreateLineEdit(current.Port > 0 ? current.Port.ToString() : string.Empty);
        var usernameBox = CreateLineEdit(current.Username);
        var passwordBox = CreateLineEdit(current.Password);
        var errorText = new Avalonia.Controls.TextBlock
        {
            IsVisible = false,
            Foreground = Avalonia.Media.Brushes.Firebrick,
            Margin = new Thickness(24, 0, 24, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var form = new Grid
        {
            Margin = new Thickness(24, 20, 24, 10),
            RowDefinitions = new RowDefinitions("Auto,12,Auto,12,Auto,12,Auto,12,Auto"),
            ColumnDefinitions = new ColumnDefinitions("110,*")
        };
        AddFormLabel(form, "类型(T):", 0);
        form.Children.Add(protocolSelect);
        Grid.SetRow(protocolSelect, 0);
        Grid.SetColumn(protocolSelect, 1);

        AddFormLabel(form, "主机(H):", 2);
        form.Children.Add(hostBox);
        Grid.SetRow(hostBox, 2);
        Grid.SetColumn(hostBox, 1);

        AddFormLabel(form, "端口(P):", 4);
        form.Children.Add(portBox);
        Grid.SetRow(portBox, 4);
        Grid.SetColumn(portBox, 1);

        AddFormLabel(form, "用户名(U):", 6);
        form.Children.Add(usernameBox);
        Grid.SetRow(usernameBox, 6);
        Grid.SetColumn(usernameBox, 1);

        AddFormLabel(form, "密码(W):", 8);
        form.Children.Add(passwordBox);
        Grid.SetRow(passwordBox, 8);
        Grid.SetColumn(passwordBox, 1);

        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "确定",
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = SizeType.Middle
        };
        var deleteButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "删除",
            Width = 86,
            SizeType = SizeType.Middle
        };
        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "取消",
            Width = 86,
            SizeType = SizeType.Middle
        };

        okButton.Click += (_, _) =>
        {
            errorText.IsVisible = false;
            if (string.IsNullOrWhiteSpace(hostBox.Text))
            {
                errorText.Text = "请输入代理服务器主机。";
                errorText.IsVisible = true;
                return;
            }

            if (!int.TryParse(portBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
            {
                errorText.Text = "端口必须是 1 到 65535 之间的整数。";
                errorText.IsVisible = true;
                return;
            }

            var protocolText = protocolSelect.SelectedOption?.Content?.ToString();
            if (!Enum.TryParse<ProxyProtocol>(protocolText, out var protocol) || protocol == ProxyProtocol.None)
                protocol = ProxyProtocol.Http;

            result = new ProxySettings
            {
                Protocol = protocol,
                Host = hostBox.Text.Trim(),
                Port = port,
                Username = usernameBox.Text?.Trim() ?? string.Empty,
                Password = passwordBox.Text ?? string.Empty
            };
            dialog.Close();
        };
        deleteButton.Click += (_, _) =>
        {
            result = new ProxySettings();
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 8, 24, 18)
        };
        buttons.Children.Add(deleteButton);
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(errorText, Dock.Bottom);
        content.Children.Add(buttons);
        content.Children.Add(errorText);
        content.Children.Add(form);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return result;
    }

    private void SetSelectedRloginOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        SelectOption(RloginTerminalSpeedSelect, vm.RloginTerminalSpeed);
    }

    private async Task<SshTunnelRule?> ShowSshTunnelRuleDialogAsync(SshTunnelRule? source)
    {
        var rule = source ?? new SshTunnelRule();
        var dialog = new Avalonia.Controls.Window
        {
            Title = "转移规则",
            Width = 620,
            Height = 470,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var typeOptions = new ObservableCollection<ISelectOption>
        {
            new SelectOption { Header = "本地（拨出）", Content = SshTunnelRuleType.Local.ToString() },
            new SelectOption { Header = "远程（传入）", Content = SshTunnelRuleType.Remote.ToString() },
            new SelectOption { Header = "Dynamic (SOCKS4/5)", Content = SshTunnelRuleType.Dynamic.ToString() }
        };
        var typeSelect = new Select
        {
            Mode = SelectMode.Single,
            OptionsSource = typeOptions,
            ShouldUseOverlayPopup = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        SelectOption(typeSelect, rule.Type.ToString());

        var sourceHostBox = CreateLineEdit(string.IsNullOrWhiteSpace(rule.SourceHost) ? "localhost" : rule.SourceHost);
        var listenPortSelect = CreateServiceAutoComplete();
        SelectServicePortOption(listenPortSelect, rule.ListenPort);
        var acceptLocalOnlyBox = new AtomUI.Desktop.Controls.CheckBox
        {
            Content = "仅接受本地连接(A)",
            IsChecked = rule.AcceptLocalConnectionsOnly
        };
        var destinationHostBox = CreateLineEdit(string.IsNullOrWhiteSpace(rule.DestinationHost) ? "localhost" : rule.DestinationHost);
        var destinationPortSelect = CreateServiceAutoComplete();
        SelectServicePortOption(destinationPortSelect, rule.DestinationPort);
        var descriptionBox = CreateLineEdit(rule.Description);

        var errorText = new Avalonia.Controls.TextBlock
        {
            Foreground = Avalonia.Media.Brushes.DarkRed,
            IsVisible = false,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var form = new Grid
        {
            Margin = new Thickness(24, 18, 24, 10),
            RowDefinitions = new RowDefinitions("Auto,10,Auto,10,Auto,10,Auto,10,Auto,10,Auto,18,Auto"),
            ColumnDefinitions = new ColumnDefinitions("150,*,150")
        };

        AddFormLabel(form, "类型 (方向)(T):", 0);
        form.Children.Add(typeSelect);
        Grid.SetRow(typeSelect, 0);
        Grid.SetColumn(typeSelect, 1);
        Grid.SetColumnSpan(typeSelect, 2);

        AddFormLabel(form, "源主机(S):", 2);
        form.Children.Add(sourceHostBox);
        Grid.SetRow(sourceHostBox, 2);
        Grid.SetColumn(sourceHostBox, 1);
        Grid.SetColumnSpan(sourceHostBox, 2);

        AddFormLabel(form, "侦听端口(L):", 4);
        form.Children.Add(listenPortSelect);
        Grid.SetRow(listenPortSelect, 4);
        Grid.SetColumn(listenPortSelect, 1);
        Grid.SetColumnSpan(listenPortSelect, 2);

        form.Children.Add(acceptLocalOnlyBox);
        Grid.SetRow(acceptLocalOnlyBox, 6);
        Grid.SetColumn(acceptLocalOnlyBox, 1);
        Grid.SetColumnSpan(acceptLocalOnlyBox, 2);

        AddFormLabel(form, "目标主机(H):", 8);
        form.Children.Add(destinationHostBox);
        Grid.SetRow(destinationHostBox, 8);
        Grid.SetColumn(destinationHostBox, 1);
        Grid.SetColumnSpan(destinationHostBox, 2);

        AddFormLabel(form, "目标端口(P):", 10);
        form.Children.Add(destinationPortSelect);
        Grid.SetRow(destinationPortSelect, 10);
        Grid.SetColumn(destinationPortSelect, 1);
        Grid.SetColumnSpan(destinationPortSelect, 2);

        AddFormLabel(form, "说明(D):", 12);
        form.Children.Add(descriptionBox);
        Grid.SetRow(descriptionBox, 12);
        Grid.SetColumn(descriptionBox, 1);
        Grid.SetColumnSpan(descriptionBox, 2);

        SshTunnelRule? result = null;
        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "确定",
            Width = 112,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = SizeType.Middle
        };
        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "取消",
            Width = 112,
            SizeType = SizeType.Middle
        };

        void UpdateRuleDialogState()
        {
            var type = GetSelectedTunnelType(typeSelect);
            var isDynamic = type == SshTunnelRuleType.Dynamic;
            destinationHostBox.IsEnabled = !isDynamic;
            destinationPortSelect.IsEnabled = !isDynamic;
            acceptLocalOnlyBox.IsEnabled = type != SshTunnelRuleType.Remote;
            if (isDynamic)
            {
                destinationHostBox.Text = string.Empty;
                destinationPortSelect.SelectedOption = null;
            }
        }

        typeSelect.SelectionChanged += (_, _) => UpdateRuleDialogState();
        UpdateRuleDialogState();

        okButton.Click += (_, _) =>
        {
            errorText.IsVisible = false;
            listenPortSelect.Status = InputControlStatus.Default;
            destinationPortSelect.Status = InputControlStatus.Default;

            var type = GetSelectedTunnelType(typeSelect);
            if (!TryReadPort(GetSelectedServicePort(listenPortSelect), out var listenPort))
            {
                listenPortSelect.Status = InputControlStatus.Error;
                errorText.Text = "请选择侦听端口。";
                errorText.IsVisible = true;
                return;
            }

            var destinationPort = 0;
            if (type != SshTunnelRuleType.Dynamic && !TryReadPort(GetSelectedServicePort(destinationPortSelect), out destinationPort))
            {
                destinationPortSelect.Status = InputControlStatus.Error;
                errorText.Text = "请选择目标端口。";
                errorText.IsVisible = true;
                return;
            }

            result = new SshTunnelRule
            {
                Id = rule.Id,
                Type = type,
                SourceHost = string.IsNullOrWhiteSpace(sourceHostBox.Text) ? "localhost" : sourceHostBox.Text.Trim(),
                ListenPort = listenPort,
                AcceptLocalConnectionsOnly = acceptLocalOnlyBox.IsChecked == true,
                DestinationHost = type == SshTunnelRuleType.Dynamic
                    ? string.Empty
                    : (string.IsNullOrWhiteSpace(destinationHostBox.Text) ? "localhost" : destinationHostBox.Text.Trim()),
                DestinationPort = destinationPort,
                Description = descriptionBox.Text?.Trim() ?? string.Empty
            };
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(24, 8, 24, 18)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(errorText, Dock.Bottom);
        errorText.Margin = new Thickness(24, 0, 24, 0);
        content.Children.Add(buttons);
        content.Children.Add(errorText);
        content.Children.Add(form);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return result;
    }

    private async void OnEditSshCipherListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        var result = await ShowAlgorithmListEditorAsync("编辑加密算法", SshAlgorithmPreferenceService.DefaultCipherAlgorithms, vm.SshCipherAlgorithms);
        if (result == null)
            return;

        vm.SshCipherAlgorithms = result;
        SelectAlgorithmOption(SshCipherSelect, vm.SshCipherAlgorithms);
    }

    private async void OnEditSshMacListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        var result = await ShowAlgorithmListEditorAsync("编辑 MAC 算法", SshAlgorithmPreferenceService.DefaultMacAlgorithms, vm.SshMacAlgorithms);
        if (result == null)
            return;

        vm.SshMacAlgorithms = result;
        SelectAlgorithmOption(SshMacSelect, vm.SshMacAlgorithms);
    }

    private async void OnEditSshKeyExchangeListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        var result = await ShowAlgorithmListEditorAsync("编辑密钥交换算法", SshAlgorithmPreferenceService.DefaultKeyExchangeAlgorithms, vm.SshKeyExchangeAlgorithms);
        if (result == null)
            return;

        vm.SshKeyExchangeAlgorithms = result;
        SelectAlgorithmOption(SshKeyExchangeSelect, vm.SshKeyExchangeAlgorithms);
    }

    private void SetSelectedSerialOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        SelectOption(SerialPortSelect, vm.SerialPortName);
        SelectOption(SerialBaudRateSelect, vm.SerialBaudRate);
        SelectOption(SerialDataBitsSelect, vm.SerialDataBits);
        SelectOption(SerialStopBitsSelect, vm.SerialStopBits);
        SelectOption(SerialParitySelect, vm.SerialParity);
        SelectOption(SerialFlowControlSelect, vm.SerialFlowControl);
    }

    private void SetSelectedRdpOptions()
    {
        if (DataContext is not SessionEditViewModel vm)
            return;

        SelectOption(RdpWindowSizeSelect, vm.RdpWindowSize);
        SelectOption(RdpResizeModeSelect, vm.RdpResizeMode);
        SelectOption(RdpScreenScaleSelect, vm.RdpScreenScale);
        SelectOption(RdpColorQualitySelect, vm.RdpColorQuality);
        SelectOption(RdpAudioModeSelect, vm.RdpAudioMode);
    }

    private void ApplyRdpPresetSize(SessionEditViewModel vm)
    {
        var size = vm.RdpWindowSize;
        if (!size.Contains('x', StringComparison.OrdinalIgnoreCase))
            return;

        var parts = size.Split('x');
        if (parts.Length != 2)
            return;

        RdpWidthBox.Text = parts[0];
        RdpHeightBox.Text = parts[1];
    }

    private static void SelectOption(Select select, string value)
    {
        select.SelectedOption = select.OptionsSource?
            .FirstOrDefault(option => string.Equals(option.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSelectedOptionText(Select select, string fallback)
    {
        return select.SelectedOption?.Content?.ToString() ?? fallback;
    }

    private static string GetSelectedAlgorithmText(Select select, string fallback)
    {
        return select.SelectedOption?.Content?.ToString() ?? fallback;
    }

    private static void SelectAlgorithmOption(Select select, string value)
    {
        var listOption = select.OptionsSource?.FirstOrDefault();
        var selected = SplitAlgorithms(value).ToArray();
        if (selected.Length == 1)
        {
            if (listOption != null)
                listOption.Content = string.Empty;
            SelectOption(select, selected[0]);
        }
        else
        {
            if (listOption != null)
            {
                listOption.Content = value ?? string.Empty;
                select.SelectedOption = listOption;
            }
            else
            {
                SelectOption(select, string.Empty);
            }
        }
    }

    private async Task<string?> ShowAlgorithmListEditorAsync(
        string title,
        IReadOnlyList<string> algorithms,
        string currentValue)
    {
        var current = SplitAlgorithms(currentValue).ToArray();
        var selected = current.Length == 0
            ? new HashSet<string>(algorithms, StringComparer.Ordinal)
            : new HashSet<string>(current, StringComparer.Ordinal);
        var checkBoxes = new List<Avalonia.Controls.CheckBox>();

        var listPanel = new StackPanel { Spacing = 6 };
        foreach (var algorithm in algorithms)
        {
            var checkBox = new Avalonia.Controls.CheckBox
            {
                Content = algorithm,
                IsChecked = selected.Contains(algorithm),
                Margin = new Thickness(0, 2)
            };
            checkBoxes.Add(checkBox);
            listPanel.Children.Add(checkBox);
        }

        string? result = null;
        var dialog = new Avalonia.Controls.Window
        {
            Title = title,
            Width = 460,
            Height = 560,
            MinWidth = 420,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            ShowInTaskbar = false
        };

        var selectAllButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "全选",
            Width = 78,
            SizeType = SizeType.Middle
        };
        selectAllButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = true;
        };

        var clearButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "清空",
            Width = 78,
            SizeType = SizeType.Middle
        };
        clearButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = false;
        };

        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "确定",
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = SizeType.Middle
        };
        okButton.Click += (_, _) =>
        {
            var checkedAlgorithms = checkBoxes
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => checkBox.Content?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();

            result = checkedAlgorithms.Length == algorithms.Count
                ? string.Empty
                : string.Join(';', checkedAlgorithms);
            dialog.Close();
        };

        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = "取消",
            Width = 86,
            SizeType = SizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var topButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 14, 16, 8)
        };
        topButtons.Children.Add(selectAllButton);
        topButtons.Children.Add(clearButton);

        var bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 8, 16, 14)
        };
        bottomButtons.Children.Add(okButton);
        bottomButtons.Children.Add(cancelButton);

        dialog.Content = new DockPanel
        {
            Children =
            {
                topButtons,
                bottomButtons,
                new Avalonia.Controls.ScrollViewer
                {
                    Margin = new Thickness(16, 0),
                    Content = listPanel
                }
            }
        };
        DockPanel.SetDock(topButtons, Dock.Top);
        DockPanel.SetDock(bottomButtons, Dock.Bottom);

        await dialog.ShowDialog(this);
        return result;
    }

    private static AtomUI.Desktop.Controls.Button CreateDialogButton(string content, double width)
    {
        return new AtomUI.Desktop.Controls.Button
        {
            Content = content,
            Width = width,
            SizeType = SizeType.Middle
        };
    }

    private static ObservableCollection<ISelectOption> CreateProxyProtocolOptions()
    {
        return
        [
            new SelectOption { Header = "SOCKS4", Content = ProxyProtocol.Socks4.ToString() },
            new SelectOption { Header = "SOCKS4A", Content = ProxyProtocol.Socks4A.ToString() },
            new SelectOption { Header = "SOCKS5", Content = ProxyProtocol.Socks5.ToString() },
            new SelectOption { Header = "HTTP 1.1", Content = ProxyProtocol.Http.ToString() },
            new SelectOption { Header = "SSH_PASSTHROUGH", Content = ProxyProtocol.SshPassthrough.ToString() },
            new SelectOption { Header = "JUMPHOST", Content = ProxyProtocol.JumpHost.ToString() }
        ];
    }

    private static ProxyProtocol GetSelectedProxyProtocol(Select select)
    {
        var value = select.SelectedOption?.Content?.ToString();
        return Enum.TryParse<ProxyProtocol>(value, out var protocol)
            ? protocol
            : ProxyProtocol.Socks5;
    }

    private static ObservableCollection<ISelectOption> CreateNextProxyOptions(
        IEnumerable<ProxySettings> proxies,
        Guid editingProxyId)
    {
        var options = new ObservableCollection<ISelectOption>
        {
            new SelectOption { Header = "<无>", Content = "None" }
        };
        foreach (var proxy in proxies.Where(proxy => proxy.Id != editingProxyId && proxy.IsEnabled))
            options.Add(new SelectOption { Header = proxy.DisplayName, Content = proxy.Id.ToString() });
        return options;
    }

    private static void RefreshProxyNextProxyDisplay(IEnumerable<ProxySettings> proxies)
    {
        var proxyList = proxies.ToArray();
        foreach (var proxy in proxyList)
        {
            proxy.NextProxyDisplay = proxy.NextProxyId.HasValue
                ? proxyList.FirstOrDefault(item => item.Id == proxy.NextProxyId.Value)?.DisplayName ?? string.Empty
                : string.Empty;
        }
    }

    private async Task<bool> ShowConfirmDialogAsync(Avalonia.Controls.Window owner, string title, string message)
    {
        var result = false;
        var dialog = new Avalonia.Controls.Window
        {
            Title = title,
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var messageText = new Avalonia.Controls.TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        var okButton = CreateDialogButton("确定", 96);
        okButton.ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary;
        var cancelButton = CreateDialogButton("取消", 96);

        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(20, 0, 20, 16)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        content.Children.Add(buttons);
        content.Children.Add(messageText);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return result;
    }

    private static LineEdit CreateLineEdit(string? text)
    {
        return new LineEdit
        {
            Text = text ?? string.Empty,
            SizeType = SizeType.Middle,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Select CreateServiceAutoComplete()
    {
        return new Select
        {
            Mode = SelectMode.Single,
            OptionsSource = LoadTcpServiceOptions(),
            PlaceholderText = "搜索服务或端口",
            IsAllowClear = true,
            IsFilterEnabled = true,
            ShouldUseOverlayPopup = false,
            DisplayPageSize = 14,
            Filter = new StringContainsFilter(),
            FilterValueSelector = ServicePortFilterValueSelector,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static void AddFormLabel(Grid grid, string text, int row)
    {
        var label = new Avalonia.Controls.TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Avalonia.Media.Brushes.Black
        };
        grid.Children.Add(label);
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
    }

    private static SshTunnelRuleType GetSelectedTunnelType(Select select)
    {
        var value = select.SelectedOption?.Content?.ToString();
        return Enum.TryParse<SshTunnelRuleType>(value, out var type)
            ? type
            : SshTunnelRuleType.Local;
    }

    private static bool TryReadPort(string? text, out int port)
    {
        return int.TryParse(text?.Trim(), out port) && port is >= 1 and <= 65535;
    }

    private static string? GetSelectedServicePort(Select select)
    {
        if (select.SelectedOption is TcpServiceOption selected)
            return selected.Port.ToString();

        return select.SelectedOption?.Content?.ToString();
    }

    private static void SelectServicePortOption(Select select, int port)
    {
        if (port <= 0)
            return;

        var selected = select.OptionsSource?
            .OfType<TcpServiceOption>()
            .FirstOrDefault(option => option.Port == port);
        if (selected == null)
        {
            selected = new TcpServiceOption
            {
                Header = port.ToString(),
                Content = port.ToString(),
                ItemKey = $"custom-port-{port}",
                Name = port.ToString(),
                Port = port
            };
            if (select.OptionsSource is ICollection<ISelectOption> options)
                options.Add(selected);
        }

        select.SelectedOption = selected;
    }

    private static readonly DefaultFilterValueSelector ServicePortFilterValueSelector = value =>
    {
        if (value is TcpServiceOption option)
            return $"{option.Name} {option.Port}";
        return value?.ToString();
    };

    private static ObservableCollection<ISelectOption> LoadTcpServiceOptions()
    {
        var options = new ObservableCollection<ISelectOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var service in TcpServices.Value)
        {
            if (!seen.Add(service.Name))
                continue;

            options.Add(new TcpServiceOption
            {
                Header = service.Name,
                Content = service.Name,
                ItemKey = $"service-{index++}-{service.Name}-{service.Port}",
                Name = service.Name,
                Port = service.Port
            });
        }

        if (options.Count == 0)
        {
            foreach (var service in DefaultTcpServices)
            {
                options.Add(new TcpServiceOption
                {
                    Header = service.Name,
                    Content = service.Name,
                    ItemKey = $"service-{index++}-{service.Name}-{service.Port}",
                    Name = service.Name,
                    Port = service.Port
                });
            }
        }

        return options;
    }

    private static readonly Lazy<IReadOnlyList<(string Name, int Port)>> TcpServices = new(() => ReadTcpServices().ToArray());

    private sealed record TcpServiceOption : SelectOption
    {
        public string Name { get; init; } = string.Empty;
        public int Port { get; init; }
    }

    private static IEnumerable<(string Name, int Port)> ReadTcpServices()
    {
        var servicesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers",
            "etc",
            "services");

        if (!File.Exists(servicesPath))
            return DefaultTcpServices;

        try
        {
            return File.ReadLines(servicesPath)
                .Select(ParseServiceLine)
                .Where(service => service.HasValue)
                .Select(service => service!.Value)
                .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return DefaultTcpServices;
        }
    }

    private static (string Name, int Port)? ParseServiceLine(string line)
    {
        var content = line.Split('#', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var portParts = parts[1].Split('/');
        if (portParts.Length != 2 || !string.Equals(portParts[1], "tcp", StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(portParts[0], out var port) && port is >= 1 and <= 65535
            ? (parts[0], port)
            : null;
    }

    private static readonly (string Name, int Port)[] DefaultTcpServices =
    [
        ("ssh", 22),
        ("telnet", 23),
        ("smtp", 25),
        ("domain", 53),
        ("http", 80),
        ("pop3", 110),
        ("imap", 143),
        ("ldap", 389),
        ("https", 443),
        ("smtps", 465),
        ("submission", 587),
        ("imaps", 993),
        ("pop3s", 995),
        ("mysql", 3306),
        ("rdp", 3389),
        ("postgresql", 5432),
        ("redis", 6379),
        ("mongodb", 27017)
    ];

    private static IEnumerable<string> SplitAlgorithms(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
