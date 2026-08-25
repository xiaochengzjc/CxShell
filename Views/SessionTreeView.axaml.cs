using System;
using System.Collections.Generic;
using System.Linq;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Views;

public partial class SessionTreeView : UserControl
{
    private bool _selectionSyncPending;
    private int _selectionSyncGeneration;
    private SessionNodeViewModel? _selectionAnchorNode;

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
            _selectionSyncGeneration++;
            _selectionSyncPending = false;
            SessionTree.ItemsSource = vm.SessionRows;
            SessionTree.SelectedItems.Clear();
            SessionTree.SelectedItem = null;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _selectionSyncGeneration++;

        if (SessionTree != null)
        {
            SessionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
            SessionTree.SelectionChanged += OnTreeSelectionChanged;
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
        if (ImportSessionBtn != null) ImportSessionBtn.Click += OnImportClick;
        if (ExportSessionBtn != null) ExportSessionBtn.Click += OnExportClick;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _selectionSyncGeneration++;
        _selectionSyncPending = false;
        if (SessionTree != null)
            SessionTree.SelectionChanged -= OnTreeSelectionChanged;
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
        var node = FindClickedNode(e);

        // Right-click opens the context menu.
        if (point.Properties.IsRightButtonPressed)
        {
            if (node != null)
            {
                var selectedNodes = GetSelectedNodes();
                if (!selectedNodes.Contains(node))
                {
                    SetGridSelectedItems([node]);
                    vm.SetSelectedNodes([node]);
                    _selectionAnchorNode = node;
                }
                else
                    vm.SetSelectedNodes(selectedNodes);

                ShowSessionContextMenu(SessionTree, vm);
            }
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || node == null)
            return;

        SessionTree.Focus();
        ApplySessionSelection(vm, node, e.KeyModifiers);
        e.Handled = true;

        if (e.ClickCount != 2 || node.Session == null)
            return;

        var mainVm = GetMainWindowViewModel();
        if (mainVm != null)
        {
            _ = mainVm.ConnectSession(node.Session);

            // Close the standalone session manager window after connecting.
            var window = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
            if (window is SessionManagerWindow)
                window.Close();
        }
    }

    private void ApplySessionSelection(
        SessionTreeViewModel vm,
        SessionNodeViewModel node,
        KeyModifiers modifiers)
    {
        var rows = vm.SessionRows.ToList();
        var selected = GetSelectedNodes().ToList();
        var hasCommandModifier = modifiers.HasFlag(KeyModifiers.Control) ||
                                 modifiers.HasFlag(KeyModifiers.Meta);
        var hasShift = modifiers.HasFlag(KeyModifiers.Shift);

        if (hasShift && _selectionAnchorNode != null)
        {
            var anchorIndex = rows.IndexOf(_selectionAnchorNode);
            var nodeIndex = rows.IndexOf(node);
            if (anchorIndex >= 0 && nodeIndex >= 0)
            {
                var start = Math.Min(anchorIndex, nodeIndex);
                var count = Math.Abs(anchorIndex - nodeIndex) + 1;
                var range = rows.Skip(start).Take(count).ToList();
                selected = hasCommandModifier
                    ? selected.Concat(range).Distinct().OrderBy(item => rows.IndexOf(item)).ToList()
                    : range;
            }
            else
            {
                selected = [node];
                _selectionAnchorNode = node;
            }
        }
        else if (hasCommandModifier)
        {
            if (selected.Contains(node))
                selected.Remove(node);
            else
                selected.Add(node);

            selected = selected.OrderBy(item => rows.IndexOf(item)).ToList();
            _selectionAnchorNode = node;
        }
        else
        {
            selected = [node];
            _selectionAnchorNode = node;
        }

        SetGridSelectedItems(selected);
        vm.SetSelectedNodes(selected);
    }

    private void SetGridSelectedItems(IReadOnlyList<SessionNodeViewModel> selected)
    {
        var selectedItems = SessionTree.SelectedItems;
        selectedItems.Clear();
        foreach (var item in selected)
        {
            if (!selectedItems.Contains(item))
                selectedItems.Add(item);
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
        if (!vm.CanUseSelectedSession) return;
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
        if (!vm.CanUseSelectedSession) return;
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
        var sessions = vm.GetSelectedSessions();
        if (sessions.Count == 0 || !vm.CanDeleteSelectedSessions) return;

        var owner = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
        if (owner == null || !await ShowLocalizedDeleteConfirmWindow(owner, sessions))
            return;

        vm.DeleteSelectedSessions();
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

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("SessionManager.Import"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CxShell session export")
                {
                    Patterns = ["*.cxsessions.json", "*.json"]
                },
                new FilePickerFileType("OpenSSH config")
                {
                    Patterns = ["config", "*.conf"]
                },
                FilePickerFileTypes.All
            ]
        });
        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            var result = vm.ImportFile(file.Path.LocalPath);
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ImportSuccessTitle"),
                Tf(
                    result.IsOpenSshConfig
                        ? "SessionManager.ImportSshConfigSuccessMessage"
                        : "SessionManager.ImportSuccessMessage",
                    result.Count),
                MessageBoxStyle.Success);
        }
        catch (Exception ex)
        {
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ImportFailedTitle"),
                Tf("SessionManager.ImportFailedMessage", ex.Message),
                MessageBoxStyle.Error);
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm ||
            !vm.CanExportSelectedSessions ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("SessionManager.Export"),
            SuggestedFileName = $"CxShell-Sessions-{DateTime.Now:yyyy-MM-dd}.cxsessions.json",
            DefaultExtension = "cxsessions.json",
            FileTypeChoices =
            [
                new FilePickerFileType("CxShell session export")
                {
                    Patterns = ["*.cxsessions.json"]
                }
            ]
        });
        if (file == null)
            return;

        try
        {
            vm.ExportSelectedSessions(file.Path.LocalPath);
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ExportSuccessTitle"),
                Tf("SessionManager.ExportSuccessMessage", vm.SelectedSessionCount),
                MessageBoxStyle.Success);
        }
        catch (Exception ex)
        {
            await AtomUiDialogService.ShowMessageAsync(
                topLevel,
                T("SessionManager.ExportFailedTitle"),
                Tf("SessionManager.ExportFailedMessage", ex.Message),
                MessageBoxStyle.Error);
        }
    }

    private void ShowSessionContextMenu(Control anchor, SessionTreeViewModel vm)
    {
        var menu = new AtomContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor
        };

        void AddItem(string text, Func<System.Threading.Tasks.Task> action)
        {
            var item = new AtomMenuItem { Header = text };
            item.Click += async (_, _) =>
            {
                menu.Close();
                await action();
            };
            menu.Items.Add(item);
        }

        AddItem(vm.PropertiesText, () =>
        {
            OnEditClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        });
        AddItem(vm.ConnectText, () =>
        {
            OnConnectClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        });
        if (vm.SelectedSession is { } session)
        {
            menu.Items.Add(new AtomMenuSeparator());
            if (session.Protocol == SessionProtocol.SSH)
            {
                AddItem(vm.DiagnosticsText, () =>
                {
                    var mainVm = GetMainWindowViewModel();
                    return mainVm?.ShowConnectionDiagnosticsAsync(session)
                        ?? System.Threading.Tasks.Task.CompletedTask;
                });
            }
            AddItem(T("SessionManager.CopyFullPath"), () => CopyTextAsync(anchor, vm.GetSessionPath(session)));
            AddItem(T("SessionManager.CopySessionId"), () => CopyTextAsync(anchor, session.Id.ToString()));
            AddItem(T("SessionManager.CopyLaunchCommand"), () => CopyTextAsync(anchor, vm.BuildSessionLaunchCommand(session)));
        }
        menu.Items.Add(new AtomMenuSeparator());
        AddItem(vm.DeleteText, () =>
        {
            OnDeleteClick(anchor, new RoutedEventArgs());
            return System.Threading.Tasks.Task.CompletedTask;
        });
        menu.Open(anchor);
    }

    private static async System.Threading.Tasks.Task CopyTextAsync(Control anchor, string text)
    {
        var clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
        if (clipboard == null)
            return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch
        {
            // Clipboard access can fail on some platforms; copying is a convenience action.
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectionSyncPending)
            return;

        _selectionSyncPending = true;
        var generation = _selectionSyncGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _selectionSyncGeneration || !IsLoaded)
            {
                _selectionSyncPending = false;
                return;
            }

            _selectionSyncPending = false;
            if (DataContext is not SessionTreeViewModel vm)
                return;

            var selectedNodes = GetSelectedNodes();
            if (selectedNodes.Count == 0 &&
                SessionTree.SelectedItem is SessionNodeViewModel selectedItem &&
                selectedItem.Session != null)
            {
                // AtomUI's DataGrid can update SelectedItem before its extended
                // SelectedItems collection. Preserve a real single-row selection.
                selectedNodes = [selectedItem];
            }

            vm.SetSelectedNodes(selectedNodes);
        }, DispatcherPriority.Background);
    }

    private IReadOnlyList<SessionNodeViewModel> GetSelectedNodes()
    {
        return SessionTree?.SelectedItems
            .OfType<SessionNodeViewModel>()
            .Where(node => node.Session != null)
            .ToList() ?? [];
    }

    private static async System.Threading.Tasks.Task<bool> ShowLocalizedDeleteConfirmWindow(
        Avalonia.Controls.Window owner,
        IReadOnlyList<SessionInfo> sessions)
    {
        var message = sessions.Count == 1
            ? Tf("Dialog.SessionDelete.Message", sessions[0].Name)
            : Tf("Dialog.SessionDelete.BatchMessage", sessions.Count);
        return await AtomUiDialogService.ShowConfirmAsync(
            owner,
            T("Dialog.SessionDelete.Title"),
            message,
            T("Common.Delete"),
            T("Common.Cancel"));
    }

}
