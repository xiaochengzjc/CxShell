using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Icons.AntDesign;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;

namespace CxShell.Views;

public partial class AgentPanelView : UserControl
{
    private AgentPanelViewModel? _viewModel;
    private INotifyCollectionChanged? _messages;
    private bool _followMessages = true;
    private bool _scrollQueued;
    private bool _isAutoScrolling;
    private readonly AtomContextMenu _promptContextMenu;
    private readonly AtomMenuItem _promptCutMenuItem;
    private readonly AtomMenuItem _promptCopyMenuItem;
    private readonly AtomMenuItem _promptPasteMenuItem;
    private readonly AtomMenuItem _promptSelectAllMenuItem;

    public event EventHandler? CloseRequested;

    public AgentPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        MessagesScrollViewer.ScrollChanged += OnMessagesScrollChanged;
        _promptContextMenu = new AtomContextMenu();
        _promptCutMenuItem = CreatePromptMenuItem("Agent.Cut", "Ctrl+X", new ScissorOutlined(), OnPromptCutClick);
        _promptCopyMenuItem = CreatePromptMenuItem("Agent.Copy", "Ctrl+C", new CopyOutlined(), OnPromptCopyClick);
        _promptPasteMenuItem = CreatePromptMenuItem("Agent.Paste", "Ctrl+V", new SnippetsOutlined(), OnPromptPasteClick);
        _promptSelectAllMenuItem = CreatePromptMenuItem("Agent.SelectAll", "Ctrl+A", new SelectOutlined(), OnPromptSelectAllClick);
        _promptContextMenu.Items.Add(_promptCutMenuItem);
        _promptContextMenu.Items.Add(_promptCopyMenuItem);
        _promptContextMenu.Items.Add(_promptPasteMenuItem);
        _promptContextMenu.Items.Add(_promptSelectAllMenuItem);
        _promptContextMenu.Opened += OnPromptContextMenuOpened;
        PromptTextBox.ContextMenu = _promptContextMenu;
        PromptTextBox.AddHandler(
            KeyDownEvent,
            OnPromptKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachViewModel(DataContext as AgentPanelViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        DetachViewModel();
        base.OnUnloaded(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        AttachViewModel(DataContext as AgentPanelViewModel);
    }

    private void AttachViewModel(AgentPanelViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel) && _messages != null)
            return;

        DetachViewModel();
        _viewModel = viewModel;
        if (viewModel == null)
            return;

        _messages = viewModel.Messages;
        _messages.CollectionChanged += OnMessagesCollectionChanged;
        foreach (var message in viewModel.Messages)
            message.PropertyChanged += OnMessagePropertyChanged;

        SetFollowMessages(true);
        QueueScrollToEnd();
    }

    private void DetachViewModel()
    {
        if (_messages != null)
        {
            _messages.CollectionChanged -= OnMessagesCollectionChanged;
            if (_viewModel != null)
            {
                foreach (var message in _viewModel.Messages)
                    message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        _messages = null;
        _viewModel = null;
        _scrollQueued = false;
        SetFollowMessages(true);
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<AgentPanelMessageViewModel>())
                item.PropertyChanged -= OnMessagePropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<AgentPanelMessageViewModel>())
                item.PropertyChanged += OnMessagePropertyChanged;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset && _viewModel != null)
        {
            foreach (var message in _viewModel.Messages)
                message.PropertyChanged += OnMessagePropertyChanged;
        }

        QueueScrollToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueScrollToEnd();
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isAutoScrolling)
            return;

        // Markdown streaming can increase the extent after the initial scroll pass.
        // Re-queue while following the conversation so the newest content remains visible.
        if (Math.Abs(e.ExtentDelta.Y) > 0.1 && _followMessages)
            QueueScrollToEnd();

        if (Math.Abs(e.OffsetDelta.Y) < 0.1)
            return;

        var maximumOffset = Math.Max(
            0,
            MessagesScrollViewer.Extent.Height - MessagesScrollViewer.Viewport.Height);
        SetFollowMessages(MessagesScrollViewer.Offset.Y >= maximumOffset - 8);
    }

    private void SetFollowMessages(bool follow)
    {
        _followMessages = follow;
        ScrollToLatestButton.IsVisible = !follow && this.IsAttachedToVisualTree();
    }

    private void QueueScrollToEnd()
    {
        if (!_followMessages || _scrollQueued || !this.IsAttachedToVisualTree())
            return;

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_followMessages || !this.IsAttachedToVisualTree())
            {
                _scrollQueued = false;
                return;
            }

            _isAutoScrolling = true;
            try
            {
                MessagesScrollViewer.ScrollToEnd();
            }
            finally
            {
                _isAutoScrolling = false;
            }

            // Streaming content can finish measuring after the first render pass.
            // Run one more pass so the viewport reaches the actual end of the list.
            Dispatcher.UIThread.Post(() =>
            {
                _scrollQueued = false;
                if (!_followMessages || !this.IsAttachedToVisualTree())
                    return;

                _isAutoScrolling = true;
                try
                {
                    MessagesScrollViewer.ScrollToEnd();
                }
                finally
                {
                    _isAutoScrolling = false;
                }
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Render);
    }

    private void OnScrollToLatestClick(object? sender, RoutedEventArgs e)
    {
        SetFollowMessages(true);
        QueueScrollToEnd();
        e.Handled = true;
    }

    private static AtomMenuItem CreatePromptMenuItem(
        string textKey,
        string gesture,
        Avalonia.Controls.PathIcon icon,
        EventHandler<RoutedEventArgs> clickHandler)
    {
        var item = new AtomMenuItem
        {
            Header = LocalizationService.Shared.Text(textKey),
            Icon = icon,
            InputGesture = KeyGesture.Parse(gesture)
        };
        item.Click += clickHandler;
        return item;
    }

    private void OnPromptContextMenuOpened(object? sender, EventArgs e)
    {
        var hasSelection = PromptTextBox.SelectionStart != PromptTextBox.SelectionEnd;
        var hasText = !string.IsNullOrEmpty(PromptTextBox.Text);
        _promptCutMenuItem.Header = Text("Agent.Cut");
        _promptCopyMenuItem.Header = Text("Agent.Copy");
        _promptPasteMenuItem.Header = Text("Agent.Paste");
        _promptSelectAllMenuItem.Header = Text("Agent.SelectAll");
        _promptCutMenuItem.IsEnabled = hasSelection && !PromptTextBox.IsReadOnly;
        _promptCopyMenuItem.IsEnabled = hasSelection;
        _promptPasteMenuItem.IsEnabled = !PromptTextBox.IsReadOnly;
        _promptSelectAllMenuItem.IsEnabled = hasText && !PromptTextBox.IsReadOnly;
    }

    private void OnPromptCutClick(object? sender, RoutedEventArgs e)
    {
        _promptContextMenu.Close();
        PromptTextBox.Cut();
        e.Handled = true;
    }

    private void OnPromptCopyClick(object? sender, RoutedEventArgs e)
    {
        _promptContextMenu.Close();
        PromptTextBox.Copy();
        e.Handled = true;
    }

    private async void OnPromptPasteClick(object? sender, RoutedEventArgs e)
    {
        _promptContextMenu.Close();
        await PasteClipboardIntoPromptAsync();
        e.Handled = true;
    }

    private void OnPromptSelectAllClick(object? sender, RoutedEventArgs e)
    {
        _promptContextMenu.Close();
        PromptTextBox.SelectAll();
        e.Handled = true;
    }

    private async void OnAttachFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentPanelViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel)
        {
            return;
        }

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = viewModel.AttachFileText,
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                    },
                    new FilePickerFileType("Documents and source files")
                    {
                        Patterns = [
                            "*.txt", "*.md", "*.markdown", "*.json", "*.xml", "*.yaml", "*.yml",
                            "*.csv", "*.log", "*.conf", "*.config", "*.ini", "*.properties", "*.env",
                            "*.sh", "*.bash", "*.ps1", "*.bat", "*.cmd", "*.py", "*.js", "*.ts",
                            "*.java", "*.cs", "*.cpp", "*.h", "*.sql", "*.html", "*.css", "*.toml",
                            "*.docx"
                        ]
                    },
                    FilePickerFileTypes.All
                ]
            });

            foreach (var file in files)
            {
                var path = file.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                    await viewModel.TryAddAttachmentAsync(path);
            }
        }
        catch (Exception)
        {
            // A cancelled picker or unavailable platform clipboard must not close the panel.
        }
    }

    private async void OnImagePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            (sender as Image)?.DataContext is not AgentAttachmentViewModel attachment ||
            attachment.Preview == null ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        e.Handled = true;
        var previewWindow = new ImagePreviewWindow
        {
            DataContext = attachment
        };
        await previewWindow.ShowDialog(owner);
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V ||
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        // Intercept before TextBox's default paste handler. Clipboard reads are
        // asynchronous, so setting Handled after awaiting is too late.
        e.Handled = true;
        _ = PasteClipboardIntoPromptAsync();
    }

    private async Task PasteClipboardIntoPromptAsync()
    {
        if (DataContext is not AgentPanelViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap != null)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                await viewModel.TryAddClipboardImageAsync(stream.ToArray());
                return;
            }

            var files = await clipboard.TryGetFilesAsync();
            var added = false;
            foreach (var file in files ?? [])
            {
                var path = file.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                    added |= await viewModel.TryAddAttachmentAsync(path);
            }

            if (added)
                return;

            // Preserve normal Ctrl+V behavior for text after taking ownership
            // of the shortcut so image and text pastes use one deterministic path.
            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                var current = PromptTextBox.Text ?? string.Empty;
                var start = Math.Clamp(PromptTextBox.SelectionStart, 0, current.Length);
                var end = Math.Clamp(PromptTextBox.SelectionEnd, start, current.Length);
                PromptTextBox.Text = current[..start] + text + current[end..];
                var caret = start + text.Length;
                PromptTextBox.SelectionStart = caret;
                PromptTextBox.SelectionEnd = caret;
            }
        }
        catch (Exception)
        {
            // Clipboard formats are platform-specific; a failed paste must not close the panel.
        }
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
