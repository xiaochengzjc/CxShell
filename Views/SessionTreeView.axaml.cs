using System;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Views;

public partial class SessionTreeView : UserControl
{
    private static string T(string key) => LocalizationService.Shared.Text(key);

    private static string Tf(string key, params object[] args) => string.Format(T(key), args);

    public SessionTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SessionTreeViewModel vm && SessionTree != null)
        {
            SessionTree.ItemsSource = vm.SessionRows;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (SessionTree != null)
        {
            SessionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        }
        UpdateColumnHeaders();
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;

        if (NewSessionBtn != null) NewSessionBtn.Click += OnNewClick;
        if (CopySessionBtn != null) CopySessionBtn.Click += OnCopyClick;
        if (PasteSessionBtn != null) PasteSessionBtn.Click += OnPasteClick;
        if (PropertiesSessionBtn != null) PropertiesSessionBtn.Click += OnEditClick;
        if (DeleteSessionBtn != null) DeleteSessionBtn.Click += OnDeleteClick;
        if (MoveSessionUpBtn != null) MoveSessionUpBtn.Click += OnMoveUpClick;
        if (MoveSessionDownBtn != null) MoveSessionDownBtn.Click += OnMoveDownClick;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        base.OnUnloaded(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        if (SessionTree?.Columns.Count >= 5)
        {
            SessionTree.Columns[0].Header = LocalizationService.Shared.Text("SessionManager.ColumnName");
            SessionTree.Columns[1].Header = LocalizationService.Shared.Text("SessionManager.ColumnHost");
            SessionTree.Columns[2].Header = LocalizationService.Shared.Text("SessionManager.ColumnUsername");
            SessionTree.Columns[3].Header = LocalizationService.Shared.Text("SessionManager.ColumnProtocol");
            SessionTree.Columns[4].Header = LocalizationService.Shared.Text("SessionManager.ColumnPort");
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        if (SessionTree == null) return;

        var point = e.GetCurrentPoint(SessionTree);

        // Right-click opens the context menu.
        if (point.Properties.IsRightButtonPressed)
        {
            var node = FindClickedNode(e);
            if (node != null)
            {
                vm.SelectedNode = node;
                SessionTree.SelectedItem = node;
                ShowSessionContextMenu(SessionTree, vm);
            }
            e.Handled = true;
            return;
        }

        // Double-click connects the selected session.
        if (e.ClickCount == 2)
        {
            var node = FindClickedNode(e);
            if (node != null)
            {
                vm.SelectedNode = node;
            }

            var session = vm.SelectedSession;
            if (session == null) return;

            var mainVm = GetMainWindowViewModel();
            if (mainVm != null)
            {
                _ = mainVm.ConnectSession(session);

                // Close the standalone session manager window after connecting.
                var window = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
                if (window is SessionManagerWindow)
                    window.Close();
            }
        }
    }

    private SessionNodeViewModel? FindClickedNode(PointerPressedEventArgs e)
    {
        var source = e.Source as Visual;
        if (source == null) return null;

        var current = source;
        while (current != null)
        {
            if (current.DataContext is SessionNodeViewModel node && node.Session != null)
            {
                return node;
            }
            current = current.GetVisualParent() as Visual;
        }

        return null;
    }

    private MainWindowViewModel? GetMainWindowViewModel()
    {
        // Prefer the view model's main window reference for standalone window mode.
        if (DataContext is SessionTreeViewModel vm)
            return vm.MainWindow;

        var window = TopLevel.GetTopLevel(this);
        return window?.DataContext as MainWindowViewModel;
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        var session = vm.SelectedSession;
        if (session == null) return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            await mainVm.EditSessionAsync(session);
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        var mainVm = GetMainWindowViewModel();
        mainVm?.NewSessionCommand.Execute(null);
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTreeViewModel vm)
        {
            vm.CopySelectedSession();
        }
    }

    private void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTreeViewModel vm)
        {
            vm.PasteCopiedSession();
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        var session = vm.SelectedSession;
        if (session == null) return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            await mainVm.ConnectSession(session);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm) return;
        var session = vm.SelectedSession;
        if (session == null) return;

        var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
        if (owner == null || !await ShowLocalizedDeleteConfirmWindow(owner, session.Name))
            return;

        var mainVm = GetMainWindowViewModel();
        mainVm?.DeleteSession(session);
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTreeViewModel vm)
            vm.MoveSelectedSessionUp();
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTreeViewModel vm)
            vm.MoveSelectedSessionDown();
    }

    private void ShowSessionContextMenu(Control anchor, SessionTreeViewModel vm)
    {
        var menu = new AtomContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor
        };

        void AddItem(string text, Action action)
        {
            var item = new AtomMenuItem { Header = text };
            item.Click += (_, _) =>
            {
                menu.Close();
                action();
            };
            menu.Items.Add(item);
        }

        AddItem(vm.PropertiesText, () => OnEditClick(anchor, new RoutedEventArgs()));
        AddItem(vm.ConnectText, () => OnConnectClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
        AddItem(vm.DeleteText, () => OnDeleteClick(anchor, new RoutedEventArgs()));
        menu.Open(anchor);
    }

    private static async System.Threading.Tasks.Task<bool> ShowLocalizedDeleteConfirmWindow(
        Avalonia.Controls.Window owner,
        string sessionName)
    {
        return await AtomUiDialogService.ShowConfirmAsync(
            owner,
            T("Dialog.SessionDelete.Title"),
            Tf("Dialog.SessionDelete.Message", sessionName),
            T("Common.Delete"),
            T("Common.Cancel"));
    }

}
