using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using CxShell.Models;
using CxShell.Services;
using CxShell.Terminal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CxShell.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private LocalizationService L => LocalizationService.Shared;

    public string ConnectingText => L.Text("Terminal.Connecting");
    public string CopyText => L.Text("Terminal.Copy");
    public string PasteText => L.Text("Terminal.Paste");
    public string ExportText => L.Text("Terminal.Export");
    public string SearchPlaceholderText => L.Text("Terminal.SearchPlaceholder");
    public string SearchPreviousText => L.Text("Terminal.SearchPrevious");
    public string SearchNextText => L.Text("Terminal.SearchNext");
    public string SearchCloseText => L.Text("Terminal.SearchClose");
    public string QuickCommandsText => L.Text("TabMenu.QuickCommands");
    public string QuickCommandsEmptyText => L.Text("TabMenu.QuickCommandsEmpty");

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _supportsPosixShellFeatures = true;
    [ObservableProperty] private string _hostInfo = string.Empty;
    [ObservableProperty] private string _remoteTitle = string.Empty;
    [ObservableProperty] private int _columns = 80;
    [ObservableProperty] private int _rows = 24;

    private bool _enableCommandSuggestions = true;

    public TerminalBuffer Buffer { get; private set; }
    public AnsiParser Parser { get; private set; }

    private ITerminalConnectionService? _connection;
    private TerminalSendQueue? _sendQueue;
    private SessionInfo? _session;
    private string? _password;
    private CancellationTokenSource? _connectionCts;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentQueue<PendingTerminalOutput> _pendingTerminalOutput = new();
    private int _terminalOutputDrainScheduled;
    private const int TerminalOutputBatchChunkLimit = 64;
    private const int TerminalOutputBatchCharacterLimit = 192 * 1024;
    private bool _manualDisconnect = true;
    private int _connectionGeneration;
    private long _reconnectAttemptId;
    private long _reconnectTaskAttemptId;
    private Task? _reconnectTask;
    private readonly object _zmodemLock = new();
    private readonly List<byte[]> _zmodemPendingBytes = new();
    private readonly List<byte> _zmodemProbeBytes = new();
    private readonly object _xymodemLock = new();
    private readonly List<byte[]> _xymodemPendingBytes = new();
    private readonly StringBuilder _outgoingCommandLine = new();
    private readonly TerminalCommandHistory _commandHistory = new();
    private Decoder _terminalByteDecoder = Encoding.UTF8.GetDecoder();
    private ZmodemTransfer? _zmodemTransfer;
    private bool _zmodemStarting;
    private ZmodemTransferDirection _zmodemStartingDirection;
    private DateTimeOffset _suppressZmodemOverAndOutUntil = DateTimeOffset.MinValue;
    private bool _pendingZmodemOverAndOutO;
    private XymodemTransfer? _xymodemTransfer;
    private bool _xymodemStarting;
    private XymodemProtocol? _pendingXymodemUploadProtocol;
    private DateTimeOffset _pendingXymodemUploadAt = DateTimeOffset.MinValue;
    private XymodemProtocol? _pendingXymodemDownloadProtocol;
    private string? _pendingXymodemDownloadFileName;
    private DateTimeOffset _pendingXymodemDownloadAt = DateTimeOffset.MinValue;
    private int _pendingXymodemDownloadGeneration;
    private DateTimeOffset _suppressXymodemResidualUntil = DateTimeOffset.MinValue;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private DateTimeOffset _lastUserInputAt = DateTimeOffset.UtcNow;
    private readonly StringBuilder _loginScriptProbeBuffer = new();
    private List<LoginScriptRule> _pendingLoginScriptRules = new();
    private readonly StringBuilder _terminalTriggerProbeBuffer = new();
    private List<LoginScriptRule> _activeTerminalTriggers = new();
    private readonly Dictionary<Guid, DateTimeOffset> _terminalTriggerLastFiredAt = new();
    private static readonly TimeSpan TerminalTriggerCooldown = TimeSpan.FromMilliseconds(1500);
    private readonly object _recentOutputLock = new();
    private readonly StringBuilder _recentOutputBuffer = new();
    private DateTimeOffset _lastBellAt = DateTimeOffset.MinValue;
    private DateTimeOffset _bellMutedUntil = DateTimeOffset.MinValue;
    private int _inputEscapeSequenceState;
    private bool _suppressNextCommandHistoryEntry;
    private string? _remoteHomeDirectory;
    private string? _previousRemoteCurrentDirectory;
    private int _remoteDirectoryQueryId;
    private SessionLogWriter? _sessionLogWriter;
    private SessionRecorder? _sessionRecorder;
    private static readonly Regex WindowsPromptPathRegex = new(
        @"(?m)(?:^|[\r\n])(?:[^\r\n<>]*?\s)?(?<path>[A-Za-z]:\\[^\r\n<>]*)>\s*$",
        RegexOptions.CultureInvariant);

    private enum DirectoryChangeKind
    {
        Home,
        Previous,
        Path
    }

    private readonly record struct DirectoryChangeRequest(DirectoryChangeKind Kind, string? Path);
    private readonly record struct PendingTerminalOutput(
        int Generation,
        ITerminalConnectionService Connection,
        string Data);

    public Func<Task<IReadOnlyList<string>>>? PickZmodemUploadFilesAsync { get; set; }
    public Func<Task<string?>>? PickZmodemDownloadFolderAsync { get; set; }
    public Func<Task<string?>>? PickSessionLogFileAsync { get; set; }
    public Func<string, Task>? SetClipboardTextAsync { get; set; }
    public string ZmodemUploadStartDirectory => _session?.FileTransferUploadDirectory ?? string.Empty;
    public bool IsTerminalSizeFixed => _session?.TerminalFixedSize == true;
    public bool EnableCommandSuggestions => _enableCommandSuggestions;
    public string KeyboardFunctionKeyMode => _session?.TerminalKeyboardFunctionKeyMode ?? "Default";
    public string KeyboardMappingFile => _session?.TerminalKeyboardMappingFile ?? string.Empty;
    public string DeleteKeySequence => _session?.TerminalDeleteKeySequence ?? "VT220";
    public string BackspaceKeySequence => _session?.TerminalBackspaceKeySequence ?? "Backspace";
    public bool LeftAltAsMeta => _session?.TerminalLeftAltAsMeta == true;
    public bool RightAltAsMeta => _session?.TerminalRightAltAsMeta == true;
    public bool CtrlAltAsAltGr => _session?.TerminalCtrlAltAsAltGr ?? true;
    public bool NewLineMode => _session?.TerminalVtNewLineMode == true;
    public bool EchoMode => _session?.TerminalVtEchoMode == true;
    public string CursorKeyMode => _session?.TerminalVtCursorKeyMode ?? "Normal";
    public string NumericKeypadMode => _session?.TerminalVtNumericKeypadMode ?? "Normal";
    public bool UseApplicationCursorMode => _session?.TerminalAdvancedUseApplicationCursorMode ?? true;
    public bool ShiftLimitsApplicationCursorMode => _session?.TerminalAdvancedShiftLimitsApplicationCursorMode ?? true;
    public bool ScrollToBottomOnInputOutput => _session?.TerminalAdvancedScrollToBottomOnInputOutput ?? true;
    public bool SuspendScrollToBottomOnScrollLock => _session?.TerminalAdvancedSuspendScrollToBottomOnScrollLock == true;
    public bool ScrollToBottomByKey => _session?.TerminalAdvancedScrollToBottomByKey == true;
    public bool DestructiveBackspace => _session?.TerminalAdvancedDestructiveBackspace == true;
    public bool UseRxvtHomeEnd => _session?.TerminalAdvancedUseRxvtHomeEnd == true;
    public string AppearanceFontFamily => _session?.AppearanceFontFamily ?? "DejaVu Sans Mono";
    public string AppearanceFontStyle => _session?.AppearanceFontStyle ?? "Normal";
    public double AppearanceFontSize => Math.Clamp(_session?.AppearanceFontSize ?? 14, 6, 96);
    public string AppearanceCjkFontFamily => _session?.AppearanceCjkFontFamily ?? AppearanceFontFamily;
    public string AppearanceCjkFontStyle => _session?.AppearanceCjkFontStyle ?? "Normal";
    public double AppearanceCjkFontSize => Math.Clamp(_session?.AppearanceCjkFontSize ?? 14, 6, 96);
    public bool AppearanceUseVariablePitchFont => _session?.AppearanceUseVariablePitchFont == true;
    public string AppearanceFontQuality => _session?.AppearanceFontQuality ?? "Default";
    public Color AppearanceCursorColor => ParseColorOrDefault(_session?.AppearanceCursorColor, "#00FF00");
    public Color AppearanceCursorTextColor => ParseColorOrDefault(_session?.AppearanceCursorTextColor, "#000000");
    public string AppearanceCursorShape => _session?.AppearanceCursorShape ?? "Block";
    public bool AppearanceUseBlinkingCursor => _session?.AppearanceUseBlinkingCursor == true;
    public int AppearanceCursorBlinkSpeedMilliseconds => Math.Clamp(_session?.AppearanceCursorBlinkSpeedMilliseconds ?? 500, 1, 5000);
    public Thickness AppearanceTerminalPadding => new(
        Math.Clamp(_session?.AppearanceWindowPaddingLeft ?? 5, 0, 200),
        Math.Clamp(_session?.AppearanceWindowPaddingTop ?? 5, 0, 200),
        Math.Clamp(_session?.AppearanceWindowPaddingRight ?? 5, 0, 200),
        Math.Clamp(_session?.AppearanceWindowPaddingBottom ?? 5, 0, 200));
    public double AppearanceLineSpacing => Math.Clamp(_session?.AppearanceLineSpacing ?? 0, -5, 32);
    public double AppearanceCharacterSpacing => Math.Clamp(_session?.AppearanceCharacterSpacing ?? 0, -5, 32);
    public string AppearanceBackgroundImagePath => _session?.AppearanceBackgroundImagePath ?? string.Empty;
    public string AppearanceBackgroundImagePosition => _session?.AppearanceBackgroundImagePosition ?? "Center";
    public bool FlashInactiveWindowOnBell => _session?.AdvancedBellFlashInactiveWindow == true;
    public IReadOnlyList<HighlightRule> AppearanceHighlightRules
    {
        get
        {
            if (_session == null || string.Equals(_session.AppearanceHighlightSetId, "None", StringComparison.OrdinalIgnoreCase))
                return [];

            return _session.AppearanceHighlightSets
                .FirstOrDefault(set => string.Equals(set.Id.ToString(), _session.AppearanceHighlightSetId, StringComparison.OrdinalIgnoreCase))
                ?.Rules
                .OrderBy(rule => rule.SortOrder)
                .Select(SessionEditViewModel.CloneHighlightRule)
                .ToArray() ?? [];
        }
    }

    public TerminalViewModel()
    {
        Buffer = new TerminalBuffer(Columns, Rows);
        Parser = new AnsiParser(Buffer);
        AttachParserHandlers(Parser);
        LocalizationService.Shared.LanguageChanged += (_, _) => RefreshLocalization();
    }

    private void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ConnectingText));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(PasteText));
        OnPropertyChanged(nameof(ExportText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchPreviousText));
        OnPropertyChanged(nameof(SearchNextText));
        OnPropertyChanged(nameof(SearchCloseText));
        OnPropertyChanged(nameof(QuickCommandsText));
        OnPropertyChanged(nameof(QuickCommandsEmptyText));
    }

    public void RefreshSessionOptions()
    {
        if (_session != null)
        {
            Buffer.ApplyColorScheme(
                ParseColorOrDefault(_session.AppearanceForegroundColor, "#CCCCCC"),
                ParseColorOrDefault(_session.AppearanceBackgroundColor, "#000000"),
                ParseColorOrDefault(_session.AppearanceBoldForegroundColor, "#33FF33"),
                ParseAnsiColors(_session.AppearanceAnsiColors));

            if (!_session.TerminalAdvancedAllowTitleChange)
                RemoteTitle = string.Empty;
        }

        NotifyKeyboardOptionsChanged();
        BufferChanged?.Invoke();
    }

    public event Action? BufferChanged;
    public event Action? BellRequested;
    public event Action? CommandLineChanged;
    public event Action? SshTunnelRuntimeChanged;
    public event Action<string>? RemoteCurrentDirectoryChanged;
    public string? RemoteCurrentDirectory { get; private set; }

    public IReadOnlyList<QuickCommandItem> GetQuickCommands()
    {
        if (_session == null || !IsConnected)
            return [];

        return QuickCommandService.GetCommands(_session, SupportsPosixShellFeatures);
    }

    public void ExecuteQuickCommand(QuickCommandItem? command)
    {
        if (command == null ||
            !IsConnected ||
            string.IsNullOrWhiteSpace(command.CommandText))
        {
            return;
        }

        SendInput(command.CommandText.TrimEnd() + "\r");
    }

    /// <summary>
    /// Handles shell history while a locally tracked command line or a
    /// conventional shell prompt is visible. This keeps arrow keys available
    /// to most full-screen remote applications.
    /// </summary>
    public bool TryHandleCommandHistoryKey(Key key)
    {
        if (!IsConnected || key is not (Key.Up or Key.Down) || _commandHistory.Count == 0)
            return false;

        var currentLine = _outgoingCommandLine.ToString();
        if (key == Key.Up)
        {
            if (currentLine.Length == 0 &&
                !_commandHistory.IsNavigating &&
                !IsLikelyShellPromptVisible())
                return false;

            var previous = _commandHistory.MovePrevious(currentLine);
            return previous != null && ReplaceCurrentCommandLine(previous);
        }

        if (!_commandHistory.IsNavigating)
            return false;

        var next = _commandHistory.MoveNext();
        return next != null && ReplaceCurrentCommandLine(next);
    }

    public string? GetCommandSuggestion()
    {
        if (!IsConnected ||
            !_enableCommandSuggestions ||
            _suppressNextCommandHistoryEntry ||
            !IsLikelyShellPromptVisible())
            return null;

        return TerminalCommandSuggestionService.FindBest(
            _outgoingCommandLine.ToString(),
            _commandHistory.Entries,
            GetQuickCommands());
    }

    public void SetCommandSuggestionsEnabled(bool enabled)
    {
        if (_enableCommandSuggestions == enabled)
            return;

        _enableCommandSuggestions = enabled;
        CommandLineChanged?.Invoke();
    }

    public string GetCommandLineText() => _outgoingCommandLine.ToString();

    public bool TryAcceptCommandSuggestion()
    {
        var currentLine = _outgoingCommandLine.ToString();
        var suggestion = GetCommandSuggestion();
        if (suggestion == null || suggestion.Length <= currentLine.Length)
            return false;

        SendInput(suggestion[currentLine.Length..]);
        return true;
    }

    public Task<string> RunRemoteCommandAsync(string commandText, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var connection = _connection as SshConnectionService;
        if (connection == null || !connection.IsConnected)
            throw new InvalidOperationException("Current terminal connection does not support remote commands.");

        return connection.RunCommandAsync(commandText, timeout, cancellationToken);
    }

    public IReadOnlyList<SshTunnelRuntimeSnapshot> GetSshTunnelRuntimeSnapshot()
    {
        return _connection is SshConnectionService connection
            ? connection.GetTunnelRuntimeSnapshot()
            : [];
    }

    public Task<SshTunnelOperationResult> StartSshTunnelAsync(Guid ruleId)
    {
        return RunSshTunnelOperationAsync(connection => connection.StartTunnel(ruleId));
    }

    public Task<SshTunnelOperationResult> StopSshTunnelAsync(Guid ruleId)
    {
        return RunSshTunnelOperationAsync(connection => connection.StopTunnel(ruleId));
    }

    public Task<SshTunnelOperationResult> RestartSshTunnelAsync(Guid ruleId)
    {
        return RunSshTunnelOperationAsync(connection => connection.RestartTunnel(ruleId));
    }

    private Task<SshTunnelOperationResult> RunSshTunnelOperationAsync(
        Func<SshConnectionService, SshTunnelOperationResult> operation)
    {
        if (_connection is not SshConnectionService connection || !connection.IsConnected)
        {
            return Task.FromResult(
                SshTunnelOperationResult.Failed("The SSH connection is not active."));
        }

        return Task.Run(() => operation(connection));
    }

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();
        _session = session;
        RemoteTitle = string.Empty;
        OnPropertyChanged(nameof(IsTerminalSizeFixed));
        NotifyKeyboardOptionsChanged();
        _password = password;
        _manualDisconnect = false;
        _connectionCts = new CancellationTokenSource();
        _terminalByteDecoder = TerminalSessionOptions.GetEncoding(session).GetDecoder();

        if (session.TerminalFixedSize || session.TerminalResetSizeOnConnect)
        {
            Columns = Math.Clamp(session.TerminalColumns, 20, 500);
            Rows = Math.Clamp(session.TerminalRows, 5, 200);
        }

        Buffer = new TerminalBuffer(
            Columns,
            Rows,
            Math.Clamp(session.TerminalScrollbackSize, 0, 200000),
            session.TerminalPushClearedScreenToScrollback,
            session.TerminalTreatAmbiguousAsWide,
            session.TerminalVtAutoWrapMode,
            session.TerminalVtOriginMode,
            session.TerminalVtReverseVideoMode,
            session.TerminalVtNewLineMode,
            session.TerminalVtInsertMode,
            string.Equals(session.TerminalVtCursorKeyMode, "Application", StringComparison.OrdinalIgnoreCase),
            string.Equals(session.TerminalVtNumericKeypadMode, "Application", StringComparison.OrdinalIgnoreCase),
            session.TerminalAdvancedClearScreenBackground,
            session.TerminalAdvancedDisableAlternateScreen,
            session.TerminalAdvancedDisableBlinkingText,
            !session.TerminalAdvancedAllowTitleChange,
            session.TerminalAdvancedDisableTerminalPrint,
            session.TerminalAdvancedIgnoreResizeRequest,
            session.TerminalAdvancedUseBuiltinLineDrawing,
            session.TerminalAdvancedUseBuiltinPowerline,
            ParseColorOrDefault(session.AppearanceForegroundColor, "#CCCCCC"),
            ParseColorOrDefault(session.AppearanceBackgroundColor, "#000000"),
            ParseColorOrDefault(session.AppearanceBoldForegroundColor, "#33FF33"),
            ParseAnsiColors(session.AppearanceAnsiColors),
            session.AppearanceBoldTextMode);
        Parser = new AnsiParser(Buffer);
        AttachParserHandlers(Parser);
        RemoteCurrentDirectory = null;
        _remoteHomeDirectory = null;
        _previousRemoteCurrentDirectory = null;
        _remoteDirectoryQueryId = 0;
        SupportsPosixShellFeatures = true;
        _terminalByteDecoder.Reset();
        lock (_recentOutputLock)
            _recentOutputBuffer.Clear();
        OnPropertyChanged(nameof(Buffer));

        await ConnectCoreAsync(_connectionCts.Token, isReconnect: false);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken, bool isReconnect)
    {
        if (_session == null)
            return;

        await _connectGate.WaitAsync(cancellationToken);
        ITerminalConnectionService? connection = null;
        TerminalSendQueue? sendQueue = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int generation = ++_connectionGeneration;
            _pendingTerminalOutput.Clear();
            var previous = _connection;
            _connection = null;
            var previousSendQueue = Interlocked.Exchange(ref _sendQueue, null);
            previousSendQueue?.Dispose();
            previous?.Dispose();

            connection = CreateConnectionService(_session.Protocol);
            _connection = connection;
            sendQueue = new TerminalSendQueue();
            _sendQueue = sendQueue;
            PrepareLoginScript(_session);

            if (connection is SshConnectionService sshConnection)
            {
                sshConnection.AutoStartConfiguredTunnels = !isReconnect || _session.SshAutoRestoreTunnels;
                sshConnection.TunnelRuntimeChanged += () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _connectionGeneration &&
                            ReferenceEquals(_connection, sshConnection))
                        {
                            SshTunnelRuntimeChanged?.Invoke();
                        }
                    });
                };
            }

            connection.DataReceived += data =>
            {
                EnqueueTerminalOutput(generation, connection, data);
            };

            connection.BinaryDataReceived += bytes => HandleBinaryData(generation, bytes);

            connection.ConnectionClosed += reason =>
            {
                Dispatcher.UIThread.Post(() => HandleConnectionClosed(generation, reason));
            };

            connection.ErrorOccurred += error =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _connectionGeneration || _manualDisconnect)
                        return;

                    AppendStatusMessage($"[Connection error: {error}]", "31");
                });
            };

            await connection.ConnectAsync(_session, _password, Columns, Rows, cancellationToken);
            SupportsPosixShellFeatures = ConnectionSupportsPosixShellFeatures(connection);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _connectionGeneration || _manualDisconnect)
            {
                if (ReferenceEquals(_connection, connection))
                    _connection = null;

                if (ReferenceEquals(_sendQueue, sendQueue))
                    _sendQueue = null;

                try
                {
                    sendQueue?.Dispose();
                    connection.Dispose();
                }
                catch
                {
                    // A stale connection is already being torn down; do not mask cancellation.
                }
                return;
            }

            connection.ResizeTerminal(Columns, Rows);
            IsConnected = true;
            HostInfo = GetHostInfo(_session);
            RefreshRecordingOptions();
            await StartSessionLogIfNeededAsync(_session);
            StartKeepAliveLoop(generation, _session, connection, cancellationToken);
            StartLoginScriptFileAsync(generation, _session, connection, cancellationToken);
            SendPreinputString(connection, _session);
            StartRemoteDirectoryTrackingAsync(generation, _session, connection, cancellationToken);
        }
        catch
        {
            StopSessionRecording();
            if (connection != null)
            {
                if (ReferenceEquals(_connection, connection))
                    _connection = null;

                var failedSendQueue = Interlocked.Exchange(ref _sendQueue, null);
                failedSendQueue?.Dispose();

                try
                {
                    connection.Dispose();
                }
                catch
                {
                    // Connection failures should not hide the original exception.
                }
            }

            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void AttachParserHandlers(AnsiParser parser)
    {
        parser.BellReceived += OnBellReceived;
        parser.OperatingSystemCommandReceived += OnOperatingSystemCommandReceived;
    }

    private void EnqueueTerminalOutput(
        int generation,
        ITerminalConnectionService connection,
        string data)
    {
        if (string.IsNullOrEmpty(data) || generation != _connectionGeneration)
            return;

        _pendingTerminalOutput.Enqueue(new PendingTerminalOutput(generation, connection, data));
        if (Interlocked.Exchange(ref _terminalOutputDrainScheduled, 1) == 0)
            Dispatcher.UIThread.Post(DrainTerminalOutput);
    }

    private void DrainTerminalOutput()
    {
        var processedChunks = 0;
        var processedCharacters = 0;
        var changed = false;

        try
        {
            while (processedChunks < TerminalOutputBatchChunkLimit &&
                   processedCharacters < TerminalOutputBatchCharacterLimit &&
                   _pendingTerminalOutput.TryDequeue(out var pending))
            {
                processedChunks++;
                processedCharacters += pending.Data.Length;
                changed |= ProcessTerminalOutput(pending);
            }

            if (changed)
            {
                Buffer.MarkAllDirty();
                BufferChanged?.Invoke();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
            if (!_pendingTerminalOutput.IsEmpty &&
                Interlocked.Exchange(ref _terminalOutputDrainScheduled, 1) == 0)
            {
                Dispatcher.UIThread.Post(DrainTerminalOutput);
            }
        }
    }

    private bool ProcessTerminalOutput(PendingTerminalOutput pending)
    {
        if (pending.Generation != _connectionGeneration ||
            !ReferenceEquals(_connection, pending.Connection))
        {
            return false;
        }

        var terminalData = ProcessAnswerback(pending.Data, pending.Connection);
        terminalData = TerminalSessionOptions.NormalizeReceiveLineEndings(terminalData, _session);
        if (string.IsNullOrEmpty(terminalData))
            return false;

        _sessionRecorder?.Write(terminalData);
        LogTerminalData(terminalData);
        AppendRecentOutput(terminalData);
        if (LooksLikePasswordPrompt(terminalData))
            _suppressNextCommandHistoryEntry = true;
        TryUpdateWindowsCurrentDirectoryFromOutput(terminalData, pending.Connection);
        HandleLoginScriptData(pending.Generation, pending.Connection, terminalData);
        HandleTerminalTriggerData(pending.Generation, pending.Connection, terminalData);
        TryDetectPendingXymodemUploadFromOutput(terminalData);
        TryStartPendingXymodemDownloadFromOutput(pending.Generation, terminalData);
        Parser.Process(terminalData);
        return true;
    }

    private bool IsLikelyShellPromptVisible()
    {
        var visibleLine = GetVisibleXymodemCommandLine();
        if (string.IsNullOrWhiteSpace(visibleLine))
            return false;

        var cursorIndex = Math.Clamp(Buffer.CursorCol, 0, visibleLine.Length);
        if (cursorIndex == 0)
            return false;

        var textBeforeCursor = visibleLine[..cursorIndex];
        return LastPromptMarkerIndex(textBeforeCursor) >= 0;
    }

    private static bool LooksLikePasswordPrompt(string text)
    {
        var normalized = text.TrimEnd();
        if (!normalized.EndsWith(':'))
            return false;

        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("passcode", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearPendingTerminalOutput()
    {
        _pendingTerminalOutput.Clear();
    }

    private void OnOperatingSystemCommandReceived(string command)
    {
        if (TryParseOsc7CurrentDirectory(command, out var path))
        {
            SetRemoteCurrentDirectory(path);
            return;
        }

        if (_session?.TerminalAdvancedAllowOsc52Clipboard == true &&
            TerminalOscCommand.TryParseClipboard(command, out var clipboardText) &&
            SetClipboardTextAsync is { } setClipboardText)
        {
            _ = ApplyOsc52ClipboardAsync(setClipboardText, clipboardText);
            return;
        }

        if (_session?.TerminalAdvancedAllowTitleChange == true &&
            TerminalOscCommand.TryParseTitle(command, out var title))
        {
            RemoteTitle = title;
        }
    }

    private static async Task ApplyOsc52ClipboardAsync(Func<string, Task> setClipboardText, string text)
    {
        try
        {
            await setClipboardText(text);
        }
        catch
        {
            // Clipboard backends can reject writes while the app is closing or inactive.
        }
    }

    private void TryUpdateWindowsCurrentDirectoryFromOutput(string data, ITerminalConnectionService connection)
    {
        if (ConnectionSupportsPosixShellFeatures(connection) || string.IsNullOrEmpty(data))
            return;

        if (!TryParseWindowsPromptCurrentDirectory(data, out var path))
            return;

        SetRemoteCurrentDirectory(path);
    }

    private void SetRemoteCurrentDirectory(string path)
    {
        if (string.Equals(RemoteCurrentDirectory, path, StringComparison.Ordinal))
            return;

        _previousRemoteCurrentDirectory = RemoteCurrentDirectory;
        RemoteCurrentDirectory = path;
        RemoteCurrentDirectoryChanged?.Invoke(path);
    }

    private static bool TryParseOsc7CurrentDirectory(string command, out string path)
    {
        path = string.Empty;

        if (!command.StartsWith("7;", StringComparison.Ordinal))
            return false;

        var value = command[2..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string pathPart;
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = value[7..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
                return false;

            pathPart = remainder[slashIndex..];
        }
        else
        {
            pathPart = value;
        }

        try
        {
            pathPart = Uri.UnescapeDataString(pathPart);
        }
        catch (UriFormatException)
        {
            return false;
        }

        pathPart = pathPart.Replace('\\', '/').Trim();
        if (pathPart.Length is 0 or > 4096 ||
            !pathPart.StartsWith("/", StringComparison.Ordinal) ||
            pathPart.Contains('\0'))
        {
            return false;
        }

        path = pathPart;
        return true;
    }

    private static bool TryParseWindowsPromptCurrentDirectory(string data, out string path)
    {
        path = string.Empty;

        var normalized = data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var match = WindowsPromptPathRegex.Matches(normalized).Cast<Match>().LastOrDefault(match => match.Success);
        if (match == null)
            return false;

        var windowsPath = match.Groups["path"].Value.Trim();
        if (windowsPath.Length < 3 ||
            windowsPath[1] != ':' ||
            windowsPath[2] != '\\' ||
            !char.IsLetter(windowsPath[0]) ||
            windowsPath.Contains('\0'))
        {
            return false;
        }

        var drive = char.ToUpperInvariant(windowsPath[0]);
        var rest = windowsPath[2..].Replace('\\', '/').TrimStart('/');
        path = string.IsNullOrEmpty(rest)
            ? $"/{drive}:/"
            : $"/{drive}:/{rest}";
        return path.Length <= 4096;
    }

    private void OnBellReceived()
    {
        var session = _session;
        if (session == null)
            return;

        var mode = string.IsNullOrWhiteSpace(session.AdvancedBellMode)
            ? "Default"
            : session.AdvancedBellMode;
        if (string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < _bellMutedUntil)
            return;

        var ignoreSeconds = Math.Clamp(session.AdvancedBellIgnoreRepeatedSeconds <= 0
            ? 3
            : session.AdvancedBellIgnoreRepeatedSeconds, 1, 3600);
        var reactivateSeconds = Math.Clamp(session.AdvancedBellReactivateAfterSeconds <= 0
            ? 3
            : session.AdvancedBellReactivateAfterSeconds, 1, 3600);

        if (_lastBellAt != DateTimeOffset.MinValue &&
            now - _lastBellAt <= TimeSpan.FromSeconds(ignoreSeconds))
        {
            _bellMutedUntil = now.AddSeconds(reactivateSeconds);
            return;
        }

        _lastBellAt = now;
        BellRequested?.Invoke();
        PlayBell(mode, session.AdvancedBellSoundPath);
    }

    private static void PlayBell(string mode, string? soundPath)
    {
        try
        {
            if (string.Equals(mode, "Sound", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(soundPath) &&
                File.Exists(soundPath))
            {
                PlaySoundFile(soundPath);
                return;
            }

            if (string.Equals(mode, "Builtin", StringComparison.OrdinalIgnoreCase))
            {
                Console.Beep();
                return;
            }

            PlayDefaultSystemBell();
        }
        catch
        {
            // Bell playback is best-effort; terminal output must continue uninterrupted.
        }
    }

    private static void PlayDefaultSystemBell()
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBeep(0xffffffff);
            return;
        }

        Console.Beep();
    }

    private static void PlaySoundFile(string soundPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Beep();
            return;
        }

        PlaySound(soundPath, IntPtr.Zero, 0x00020000 | 0x0001);
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private void HandleConnectionClosed(int generation, string reason)
    {
        if (generation != _connectionGeneration || _manualDisconnect)
            return;

        IsConnected = false;
        HostInfo = string.Empty;
        RemoteTitle = string.Empty;
        StopKeepAliveLoop();
        StopSessionRecording();
        AppendStatusMessage($"[Connection closed: {reason}]", "31");

        if (_session?.AutoReconnect != true)
        {
            AppendStatusMessage("[Auto reconnect disabled]", "33");
            return;
        }

        var cancellationToken = _connectionCts?.Token ?? CancellationToken.None;
        StartReconnectLoop(cancellationToken);
    }

    private void StartReconnectLoop(CancellationToken cancellationToken)
    {
        var currentAttemptId = Volatile.Read(ref _reconnectAttemptId);
        if (_reconnectTask is { IsCompleted: false } &&
            Volatile.Read(ref _reconnectTaskAttemptId) == currentAttemptId)
        {
            return;
        }

        var attemptId = Interlocked.Increment(ref _reconnectAttemptId);
        Volatile.Write(ref _reconnectTaskAttemptId, attemptId);
        _reconnectTask = ReconnectLoopAsync(attemptId, cancellationToken);
    }

    private bool IsReconnectAttemptCurrent(long attemptId, CancellationToken cancellationToken)
    {
        return attemptId == Volatile.Read(ref _reconnectAttemptId) &&
               !_manualDisconnect &&
               !cancellationToken.IsCancellationRequested;
    }

    private async Task ReconnectLoopAsync(long attemptId, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        while (IsReconnectAttemptCurrent(attemptId, cancellationToken))
        {
            try
            {
                var reconnectDelay = TimeSpan.FromSeconds(Math.Max(1, _session?.ReconnectIntervalSeconds ?? 30));
                var limitMinutes = Math.Max(0, _session?.ReconnectLimitMinutes ?? 0);
                if (limitMinutes > 0 && DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(limitMinutes))
                {
                    await AppendReconnectStatusAsync(
                        attemptId,
                        cancellationToken,
                        $"[Auto reconnect stopped after {limitMinutes} minute(s)]",
                        "33");
                    return;
                }

                await Task.Delay(reconnectDelay, cancellationToken);
                if (!IsReconnectAttemptCurrent(attemptId, cancellationToken))
                    return;

                await AppendReconnectStatusAsync(
                    attemptId,
                    cancellationToken,
                    $"[Auto reconnecting; retry interval: {reconnectDelay.TotalSeconds:0}s...]",
                    "33");

                await ConnectCoreAsync(cancellationToken, isReconnect: true);
                if (!IsReconnectAttemptCurrent(attemptId, cancellationToken))
                    return;

                if (IsConnected && _connection?.IsConnected == true)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (IsReconnectAttemptCurrent(attemptId, cancellationToken))
                            AppendStatusMessage("[Auto reconnect succeeded]", "32");
                    });
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsReconnectAttemptCurrent(attemptId, cancellationToken))
                    return;

                await AppendReconnectStatusAsync(
                    attemptId,
                    cancellationToken,
                    $"[Auto reconnect failed: {ex.Message}]",
                    "31");
            }
        }
    }

    private void InvalidateReconnectLoop()
    {
        Interlocked.Increment(ref _reconnectAttemptId);
        _reconnectTask = null;
    }

    private async Task AppendReconnectStatusAsync(
        long attemptId,
        CancellationToken cancellationToken,
        string message,
        string colorCode)
    {
        if (!IsReconnectAttemptCurrent(attemptId, cancellationToken))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsReconnectAttemptCurrent(attemptId, cancellationToken))
                AppendStatusMessage(message, colorCode);
        });
    }

    private void AppendStatusMessage(string message, string colorCode)
    {
        LogTerminalData($"\r\n{message}\r\n");
        Parser.Process($"\r\n\x1B[{colorCode}m{message}\x1B[0m\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private void AppendPlainStatusMessage(string message)
    {
        LogTerminalData($"\r\n{message}\r\n");
        Parser.Process($"\r\n{message}\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private async Task StartSessionLogIfNeededAsync(SessionInfo session)
    {
        StopSessionLog();
        if (!session.AdvancedLogStartOnConnect)
            return;

        string? chosenPath = null;
        if (session.AdvancedLogPromptFileOnStart)
        {
            if (PickSessionLogFileAsync == null)
                return;

            chosenPath = await PickSessionLogFileAsync();
            if (string.IsNullOrWhiteSpace(chosenPath))
                return;
        }

        try
        {
            _sessionLogWriter = SessionLogWriter.Start(session, chosenPath);
        }
        catch (Exception ex)
        {
            AppendStatusMessage($"[Session log failed: {ex.Message}]", "31");
        }
    }

    private void LogTerminalData(string data)
    {
        try
        {
            _sessionLogWriter?.Write(data);
        }
        catch (Exception ex)
        {
            StopSessionLog();
            AppendStatusMessage($"[Session log stopped: {ex.Message}]", "31");
        }
    }

    private void StopSessionLog()
    {
        try
        {
            _sessionLogWriter?.Dispose();
        }
        catch
        {
            // Ignore log close failures during disconnect.
        }
        finally
        {
            _sessionLogWriter = null;
        }
    }

    private void PrepareLoginScript(SessionInfo session)
    {
        _loginScriptProbeBuffer.Clear();
        _terminalTriggerProbeBuffer.Clear();
        _terminalTriggerLastFiredAt.Clear();

        var rules = session.EnableLoginScriptRules
            ? session.LoginScriptRules
                .OrderBy(rule => rule.SortOrder)
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Expect))
                .Select(SessionEditViewModel.CloneLoginScriptRule)
                .ToList()
            : new List<LoginScriptRule>();

        _pendingLoginScriptRules = session.EnableLoginScriptRules
            ? rules.Where(rule => !rule.KeepWatching).ToList()
            : [];
        _activeTerminalTriggers = session.EnableLoginScriptRules
            ? rules.Where(rule => rule.KeepWatching).ToList()
            : [];
    }

    private void HandleLoginScriptData(
        int generation,
        ITerminalConnectionService connection,
        string data)
    {
        if (generation != _connectionGeneration ||
            _manualDisconnect ||
            _pendingLoginScriptRules.Count == 0 ||
            string.IsNullOrEmpty(data))
        {
            return;
        }

        _loginScriptProbeBuffer.Append(data);
        if (_loginScriptProbeBuffer.Length > 8192)
            _loginScriptProbeBuffer.Remove(0, _loginScriptProbeBuffer.Length - 8192);

        while (_pendingLoginScriptRules.Count > 0)
        {
            var rule = _pendingLoginScriptRules[0];
            if (!TerminalTriggerMatcher.IsMatch(rule, _loginScriptProbeBuffer.ToString()))
                return;

            _pendingLoginScriptRules.RemoveAt(0);
            _loginScriptProbeBuffer.Clear();
            if (!string.IsNullOrEmpty(rule.Send))
                TrySendData(connection, NormalizeScriptSendText(rule.Send));
        }
    }

    public void RefreshRecordingOptions()
    {
        if (_session == null || !IsConnected || !SessionRecordingService.Shared.IsEnabled)
        {
            StopSessionRecording();
            return;
        }

        _sessionRecorder ??= SessionRecordingService.Shared.Start(_session, Columns, Rows);
    }

    private void StopSessionRecording()
    {
        try
        {
            _sessionRecorder?.Dispose();
        }
        catch
        {
            // Recording is best-effort and must never block terminal teardown.
        }
        finally
        {
            _sessionRecorder = null;
        }
    }

    private void HandleTerminalTriggerData(
        int generation,
        ITerminalConnectionService connection,
        string data)
    {
        if (generation != _connectionGeneration ||
            _manualDisconnect ||
            _activeTerminalTriggers.Count == 0 ||
            string.IsNullOrEmpty(data))
        {
            return;
        }

        _terminalTriggerProbeBuffer.Append(data);
        if (_terminalTriggerProbeBuffer.Length > 8192)
            _terminalTriggerProbeBuffer.Remove(0, _terminalTriggerProbeBuffer.Length - 8192);

        var output = _terminalTriggerProbeBuffer.ToString();
        var now = DateTimeOffset.UtcNow;
        var matched = false;
        foreach (var rule in _activeTerminalTriggers)
        {
            if (!TerminalTriggerMatcher.IsMatch(rule, output))
                continue;

            if (_terminalTriggerLastFiredAt.TryGetValue(rule.Id, out var lastFiredAt) &&
                now - lastFiredAt < TerminalTriggerCooldown)
            {
                continue;
            }

            _terminalTriggerLastFiredAt[rule.Id] = now;
            if (!string.IsNullOrEmpty(rule.Send))
                TrySendData(connection, NormalizeScriptSendText(rule.Send));
            matched = true;
        }

        if (matched)
            _terminalTriggerProbeBuffer.Clear();
    }

    private void StartLoginScriptFileAsync(
        int generation,
        SessionInfo session,
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        if (!session.RunLoginScriptFile || string.IsNullOrWhiteSpace(session.LoginScriptFilePath))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                if (generation != _connectionGeneration || _manualDisconnect || !connection.IsConnected)
                    return;

                var path = session.LoginScriptFilePath.Trim();
                if (!File.Exists(path))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        AppendStatusMessage($"[Login script file not found: {path}]", "31"));
                    return;
                }

                var scriptText = await File.ReadAllTextAsync(path, cancellationToken);
                if (string.IsNullOrEmpty(scriptText))
                    return;

                var expandedScript = ApplyLoginScriptParameters(scriptText, session.LoginScriptParameters);
                var payload = BuildLoginScriptPayload(session, connection, expandedScript);
                TrySendData(connection, payload);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage($"[Login script failed: {ex.Message}]", "31"));
            }
        }, cancellationToken);
    }

    private void SendPreinputString(ITerminalConnectionService connection, SessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(session.TerminalAdvancedPreinputString) || !connection.IsConnected)
            return;

        var text = NormalizeScriptSendText(session.TerminalAdvancedPreinputString);
        text = TerminalSessionOptions.NormalizeSendLineEndings(text, session);
        if (!string.IsNullOrEmpty(text))
            TrySendData(connection, text);
    }

    private void StartRemoteDirectoryTrackingAsync(
        int generation,
        SessionInfo session,
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        if (session.Protocol != SessionProtocol.SSH ||
            session.SshNoTerminal ||
            !session.SftpFollowTerminalDirectory ||
            !ConnectionSupportsPosixShellFeatures(connection))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
                if (generation != _connectionGeneration || _manualDisconnect || !connection.IsConnected)
                    return;

                await InitializeRemoteDirectoryTrackingAsync(generation, connection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private async Task InitializeRemoteDirectoryTrackingAsync(
        int generation,
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        if (connection is not SshConnectionService sshConnection)
            return;

        try
        {
            var output = await sshConnection
                .RunCommandAsync("printf '__CXSHELL_HOME__%s\\n' \"$HOME\"; printf '__CXSHELL_PWD__'; pwd -P", TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);

            var home = ExtractMarkedRemotePath(output, "__CXSHELL_HOME__");
            var current = ExtractMarkedRemotePath(output, "__CXSHELL_PWD__");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _connectionGeneration || _manualDisconnect || !connection.IsConnected)
                    return;

                if (!string.IsNullOrWhiteSpace(home))
                    _remoteHomeDirectory = home;

                if (!string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(RemoteCurrentDirectory))
                    SetRemoteCurrentDirectory(current);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Remote directory initialization failed: {ex.Message}");
        }
    }

    private void HandlePotentialDirectoryChangeCommand(string typedCommandLine, string? visibleCommandLine)
    {
        if (_session == null ||
            _session.Protocol != SessionProtocol.SSH ||
            !_session.SftpFollowTerminalDirectory ||
            _connection == null ||
            !_connection.IsConnected ||
            !ConnectionSupportsPosixShellFeatures(_connection))
        {
            return;
        }

        if (!TryExtractDirectoryChange(typedCommandLine, allowPromptPrefix: false, out var request) &&
            !TryExtractDirectoryChange(visibleCommandLine, allowPromptPrefix: true, out request))
        {
            return;
        }

        var generation = _connectionGeneration;
        var queryId = Interlocked.Increment(ref _remoteDirectoryQueryId);
        var cancellationToken = _connectionCts?.Token ?? CancellationToken.None;
        _ = ResolveRemoteDirectoryChangeAsync(generation, queryId, request, cancellationToken);
    }

    private async Task ResolveRemoteDirectoryChangeAsync(
        int generation,
        int queryId,
        DirectoryChangeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            if (generation != _connectionGeneration ||
                queryId != _remoteDirectoryQueryId ||
                _manualDisconnect ||
                _connection is not SshConnectionService sshConnection ||
                !sshConnection.IsConnected)
            {
                return;
            }

            var candidate = ResolveDirectoryChangeCandidate(request);
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var command = $"cd {QuotePosixShellArgument(candidate)} 2>/dev/null && pwd -P";
            var output = await sshConnection
                .RunCommandAsync(command, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            var resolvedPath = NormalizeRemoteDirectoryPath(output);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _connectionGeneration &&
                    queryId == _remoteDirectoryQueryId &&
                    !_manualDisconnect &&
                    sshConnection.IsConnected)
                {
                    SetRemoteCurrentDirectory(resolvedPath);
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Remote directory update failed: {ex.Message}");
        }
    }

    private string? ResolveDirectoryChangeCandidate(DirectoryChangeRequest request)
    {
        return request.Kind switch
        {
            DirectoryChangeKind.Home => _remoteHomeDirectory,
            DirectoryChangeKind.Previous => _previousRemoteCurrentDirectory,
            DirectoryChangeKind.Path => ResolveRemoteDirectoryPath(request.Path),
            _ => null
        };
    }

    private static string? ExtractMarkedRemotePath(string output, string marker)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
                return NormalizeRemoteDirectoryPath(line[marker.Length..]);
        }

        return null;
    }

    private string? ResolveRemoteDirectoryPath(string? path)
    {
        var value = path?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Replace('\\', '/');
        if (value == "~")
            return _remoteHomeDirectory;

        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(_remoteHomeDirectory)
                ? null
                : CollapseRemotePath(CombineRemotePath(_remoteHomeDirectory, value[2..]));
        }

        if (value.StartsWith("~", StringComparison.Ordinal))
            return null;

        if (value.StartsWith("/", StringComparison.Ordinal))
            return CollapseRemotePath(value);

        var current = RemoteCurrentDirectory ?? _remoteHomeDirectory;
        return string.IsNullOrWhiteSpace(current)
            ? null
            : CollapseRemotePath(CombineRemotePath(current, value));
    }

    private static string? NormalizeRemoteDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var value = path
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Replace('\\', '/');
        if (value.Length > 4096 ||
            value.Contains('\0') ||
            !value.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        return CollapseRemotePath(value);
    }

    private static bool TryExtractDirectoryChange(
        string? commandLine,
        bool allowPromptPrefix,
        out DirectoryChangeRequest request)
    {
        request = default;
        var command = ExtractDirectoryCommandText(commandLine, allowPromptPrefix);
        if (string.IsNullOrWhiteSpace(command) || !StartsWithCdCommand(command))
            return false;

        var remainder = command.Length > 2 ? command[2..] : string.Empty;
        var tokens = ReadShellPrefixTokens(remainder);
        var index = 0;
        while (index < tokens.Count && IsCdOption(tokens[index]))
            index++;

        if (index >= tokens.Count)
        {
            request = new DirectoryChangeRequest(DirectoryChangeKind.Home, null);
            return true;
        }

        if (tokens.Count > index + 1)
            return false;

        var target = tokens[index];
        request = string.Equals(target, "-", StringComparison.Ordinal)
            ? new DirectoryChangeRequest(DirectoryChangeKind.Previous, null)
            : new DirectoryChangeRequest(DirectoryChangeKind.Path, target);
        return true;
    }

    private static string? ExtractDirectoryCommandText(string? commandLine, bool allowPromptPrefix)
    {
        var text = commandLine?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!allowPromptPrefix)
            return text;

        var promptIndex = LastPromptMarkerIndex(text);
        if (promptIndex >= 0)
            return text[(promptIndex + 2)..].TrimStart();

        var commandIndex = FindCommandToken(text, "cd");
        return commandIndex >= 0 ? text[commandIndex..].TrimStart() : text;
    }

    private static int LastPromptMarkerIndex(string text)
    {
        var best = -1;
        foreach (var marker in new[] { "$ ", "# ", "> ", "% " })
        {
            var index = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (index > best)
                best = index;
        }

        return best;
    }

    private static bool StartsWithCdCommand(string command)
    {
        return command.StartsWith("cd", StringComparison.Ordinal) &&
               (command.Length == 2 ||
                char.IsWhiteSpace(command[2]) ||
                IsShellCommandSeparatorStart(command, 2));
    }

    private static bool IsCdOption(string token)
    {
        return token is "--" or "-L" or "-P" or "-e";
    }

    private static List<string> ReadShellPrefixTokens(string text)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            if (index >= text.Length || IsShellCommandSeparatorStart(text, index))
                break;

            var token = new StringBuilder();
            while (index < text.Length)
            {
                var ch = text[index];
                if (char.IsWhiteSpace(ch) || IsShellCommandSeparatorStart(text, index))
                    break;

                if (ch is '\'' or '"')
                {
                    var quote = ch;
                    index++;
                    while (index < text.Length)
                    {
                        ch = text[index++];
                        if (ch == quote)
                            break;

                        if (ch == '\\' && quote == '"' && index < text.Length)
                            ch = text[index++];

                        token.Append(ch);
                    }
                    continue;
                }

                if (ch == '\\' && index + 1 < text.Length)
                {
                    token.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                token.Append(ch);
                index++;
            }

            if (token.Length > 0)
                tokens.Add(token.ToString());

            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            if (index < text.Length && IsShellCommandSeparatorStart(text, index))
                break;
        }

        return tokens;
    }

    private static bool IsShellCommandSeparatorStart(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return false;

        return text[index] is ';' or '|' or '&';
    }

    private static string CombineRemotePath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || parent == "/")
            return "/" + child.TrimStart('/');

        return parent.TrimEnd('/') + "/" + child.TrimStart('/');
    }

    private static string CollapseRemotePath(string path)
    {
        var parts = new Stack<string>();
        foreach (var rawPart in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart == ".")
                continue;

            if (rawPart == "..")
            {
                if (parts.Count > 0)
                    parts.Pop();
                continue;
            }

            parts.Push(rawPart);
        }

        return parts.Count == 0 ? "/" : "/" + string.Join("/", parts.Reverse());
    }

    private static string QuotePosixShellArgument(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static bool ConnectionSupportsPosixShellFeatures(ITerminalConnectionService connection)
    {
        return connection is not SshConnectionService { SupportsPosixShellFeatures: false };
    }

    private static string NormalizeScriptSendText(string text)
    {
        return text
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r", StringComparison.Ordinal);
    }

    private static string ApplyLoginScriptParameters(string scriptText, string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return scriptText;

        var args = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < args.Length; i++)
            scriptText = scriptText.Replace($"{{{i}}}", args[i], StringComparison.Ordinal);
        return scriptText;
    }

    private static string BuildLoginScriptPayload(
        SessionInfo session,
        ITerminalConnectionService connection,
        string scriptText)
    {
        var isPosix = ConnectionSupportsPosixShellFeatures(connection);
        var mode = SessionEditViewModel.NormalizeLoginScriptExecutionMode(
            session.LoginScriptExecutionMode,
            session.LoginScriptFilePath);
        var interpreter = ResolveLoginScriptInterpreter(mode, session.LoginScriptInterpreter, isPosix);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptText));
        var extension = mode switch
        {
            LoginScriptExecutionMode.Python => "py",
            LoginScriptExecutionMode.Bash => "sh",
            LoginScriptExecutionMode.PowerShell => "ps1",
            _ => "txt"
        };
        var temporaryName = $"cxshell-login-{Guid.NewGuid():N}.{extension}";
        var parameters = session.LoginScriptParameters?.Trim() ?? string.Empty;

        return isPosix
            ? BuildPosixLoginScriptCommand(interpreter, temporaryName, base64, parameters)
            : BuildPowerShellLoginScriptCommand(mode, interpreter, temporaryName, base64, parameters);
    }

    private static string ResolveLoginScriptInterpreter(
        LoginScriptExecutionMode mode,
        string? configuredInterpreter,
        bool isPosix)
    {
        if (!string.IsNullOrWhiteSpace(configuredInterpreter))
            return configuredInterpreter.Trim();

        return mode switch
        {
            LoginScriptExecutionMode.Python => isPosix ? "python3" : "python",
            LoginScriptExecutionMode.Bash => "bash",
            LoginScriptExecutionMode.PowerShell => isPosix ? "pwsh" : "powershell",
            _ => string.Empty
        };
    }

    private static string BuildPosixLoginScriptCommand(
        string interpreter,
        string temporaryName,
        string base64,
        string parameters)
    {
        var path = $"/tmp/{temporaryName}";
        var command = new StringBuilder();
        command.Append("__cxshell_script=").Append(QuotePosixShellArgument(path)).Append("; ");
        command.Append("printf '%s' ").Append(QuotePosixShellArgument(base64));
        command.Append(" | base64 -d > \"$__cxshell_script\"; ");
        command.Append(interpreter).Append(" \"$__cxshell_script\"");
        if (!string.IsNullOrWhiteSpace(parameters))
            command.Append(' ').Append(parameters);
        command.Append("; __cxshell_status=$?; rm -f \"$__cxshell_script\"; unset __cxshell_script; ");
        command.Append("printf '[CxShell script exit: %s]' \"$__cxshell_status\"");
        return NormalizeScriptSendText(command.ToString()) + "\r";
    }

    private static string BuildPowerShellLoginScriptCommand(
        LoginScriptExecutionMode mode,
        string interpreter,
        string temporaryName,
        string base64,
        string parameters)
    {
        var command = new StringBuilder();
        command.Append("$__cxshell_script=Join-Path $env:TEMP '").Append(temporaryName).Append("'; ");
        command.Append("[IO.File]::WriteAllBytes($__cxshell_script,[Convert]::FromBase64String('");
        command.Append(base64).Append("')); ");
        command.Append("& ").Append(interpreter);
        if (mode == LoginScriptExecutionMode.PowerShell)
            command.Append(" -NoProfile -NonInteractive -File");
        command.Append(" $__cxshell_script");
        if (!string.IsNullOrWhiteSpace(parameters))
            command.Append(' ').Append(parameters);
        command.Append("; $__cxshell_status=$LASTEXITCODE; Remove-Item -LiteralPath $__cxshell_script -Force -ErrorAction SilentlyContinue; ");
        command.Append("Write-Output ('[CxShell script exit: ' + $__cxshell_status + ']')");
        return NormalizeScriptSendText(command.ToString()) + "\r";
    }

    private bool HandleBinaryData(int generation, byte[] bytes)
    {
        if (generation != _connectionGeneration)
            return false;

        if (TrySuppressLateZmodemOverAndOut(bytes))
            return true;

        if (TrySuppressXymodemResidual(bytes))
            return true;

        ZmodemTransfer? transfer = null;
        XymodemTransfer? xymodemTransfer = null;
        lock (_zmodemLock)
        {
            if (_zmodemTransfer != null)
            {
                transfer = _zmodemTransfer;
            }
            else if (_zmodemStarting)
            {
                _zmodemPendingBytes.Add(bytes);
                return true;
            }
        }

        if (transfer != null)
        {
            transfer.Feed(bytes);
            return true;
        }

        lock (_xymodemLock)
        {
            if (_xymodemTransfer != null)
            {
                xymodemTransfer = _xymodemTransfer;
            }
            else if (_xymodemStarting)
            {
                _xymodemPendingBytes.Add(bytes);
                return true;
            }
        }

        if (xymodemTransfer != null)
        {
            xymodemTransfer.Feed(bytes);
            return true;
        }

        var pendingDownloadAction = HandlePendingXymodemDownloadBytes(generation, bytes);
        if (pendingDownloadAction == PendingXymodemDownloadByteAction.Consume)
        {
            lock (_zmodemLock)
                _zmodemProbeBytes.Clear();
            return true;
        }

        if (pendingDownloadAction == PendingXymodemDownloadByteAction.DeferToTerminal)
        {
            lock (_zmodemLock)
                _zmodemProbeBytes.Clear();
            return false;
        }

        if (_session?.FileTransferZmodemAutoActivate == false)
            return false;

        var probePrefixLength = 0;
        byte[] scanBytes;
        lock (_zmodemLock)
        {
            probePrefixLength = _zmodemProbeBytes.Count;
            if (probePrefixLength > 0)
            {
                scanBytes = _zmodemProbeBytes.Concat(bytes).ToArray();
                _zmodemProbeBytes.Clear();
            }
            else
            {
                scanBytes = bytes;
            }
        }

        if (!ZmodemTransfer.TryFindStartupHeader(scanBytes, out var index, out var direction))
        {
            if (TryStartPendingXymodemUpload(generation, scanBytes))
                return true;

            var keep = GetZmodemStartupPrefixSuffixLength(scanBytes);
            if (keep == 0 && probePrefixLength == 0)
                return false;

            var terminalLength = scanBytes.Length - keep;
            if (terminalLength > 0)
                ProcessTerminalBytes(scanBytes[..terminalLength]);

            lock (_zmodemLock)
            {
                _zmodemProbeBytes.Clear();
                if (keep > 0)
                    _zmodemProbeBytes.AddRange(scanBytes[^keep..]);
            }

            return true;
        }

        if (index > 0 && !ShouldSuppressZmodemPreamble(direction, scanBytes[..index]))
            ProcessTerminalBytes(scanBytes[..index]);

        lock (_zmodemLock)
        {
            _zmodemStarting = true;
            _zmodemStartingDirection = direction;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
            _zmodemPendingBytes.Add(scanBytes[index..]);
        }

        _ = BeginZmodemTransferAsync(generation, direction);
        return true;
    }

    private PendingXymodemDownloadByteAction HandlePendingXymodemDownloadBytes(int generation, byte[] bytes)
    {
        lock (_xymodemLock)
        {
            if (_pendingXymodemDownloadProtocol == null)
                return PendingXymodemDownloadByteAction.None;

            if (generation != _pendingXymodemDownloadGeneration ||
                DateTimeOffset.UtcNow - _pendingXymodemDownloadAt > TimeSpan.FromMinutes(2))
            {
                ClearPendingXymodemDownload();
                return PendingXymodemDownloadByteAction.None;
            }

            if (ZmodemTransfer.TryFindStartupHeader(bytes, out _, out _))
            {
                ClearPendingXymodemDownload();
                TrySendBytes(_connection, new[] { (byte)24, (byte)24, (byte)24, (byte)24, (byte)24 });
                PostStatusMessage("[YMODEM download cancelled: remote started ZMODEM; use sz for ZMODEM download]", "33");
                return PendingXymodemDownloadByteAction.Consume;
            }

            return PendingXymodemDownloadByteAction.DeferToTerminal;
        }
    }

    private enum PendingXymodemDownloadByteAction
    {
        None,
        DeferToTerminal,
        Consume
    }

    private static int GetZmodemStartupPrefixSuffixLength(byte[] bytes)
    {
        ReadOnlySpan<byte> prefix = stackalloc byte[] { 0x2a, 0x2a, 0x18, 0x42, 0x30 };
        var max = Math.Min(prefix.Length, bytes.Length);
        for (var length = max; length > 0; length--)
        {
            var suffix = bytes.AsSpan(bytes.Length - length, length);
            if (suffix.SequenceEqual(prefix[..length]))
                return length;
        }

        return 0;
    }

    private bool ShouldSuppressZmodemPreamble(ZmodemTransferDirection direction, byte[] bytes)
    {
        if (direction != ZmodemTransferDirection.Download || bytes.Length == 0)
            return false;

        var text = Encoding.ASCII.GetString(bytes).Trim('\r', '\n');
        return MatchesConfiguredCommandText(text, _session?.FileTransferZmodemUploadCommand, "rz");
    }

    private bool TryStartPendingXymodemUpload(int generation, byte[] bytes)
    {
        XymodemProtocol? protocol;
        lock (_xymodemLock)
        {
            protocol = _pendingXymodemUploadProtocol;
            if (protocol == null || DateTimeOffset.UtcNow - _pendingXymodemUploadAt > TimeSpan.FromMinutes(2))
            {
                _pendingXymodemUploadProtocol = null;
                return false;
            }
        }

        if (!XymodemTransfer.TryFindReceiverRequest(bytes, out var index))
            return false;

        if (index > 0)
            ProcessTerminalBytes(bytes[..index]);

        lock (_xymodemLock)
        {
            if (generation != _connectionGeneration || _xymodemTransfer != null || _xymodemStarting)
                return true;

            _xymodemStarting = true;
            _pendingXymodemUploadProtocol = null;
            _xymodemPendingBytes.Clear();
            _xymodemPendingBytes.Add(bytes[index..]);
        }

        _ = BeginXymodemUploadAsync(generation, protocol.Value);
        return true;
    }

    private async Task BeginZmodemTransferAsync(int generation, ZmodemTransferDirection direction)
    {
        try
        {
            string? downloadFolder = null;
            IReadOnlyList<string>? uploadFiles = null;

            if (direction == ZmodemTransferDirection.Download)
            {
                downloadFolder = GetConfiguredZmodemDownloadFolder();
                if (string.IsNullOrWhiteSpace(downloadFolder))
                {
                    if (PickZmodemDownloadFolderAsync == null)
                        throw new InvalidOperationException("Download folder picker is not available.");

                    downloadFolder = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemDownloadFolderAsync());
                }

                if (string.IsNullOrWhiteSpace(downloadFolder))
                {
                    CancelStartingZmodem("[ZMODEM download cancelled]", generation);
                    return;
                }
            }
            else
            {
                if (PickZmodemUploadFilesAsync == null)
                    throw new InvalidOperationException("Upload file picker is not available.");

                uploadFiles = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemUploadFilesAsync());
                uploadFiles = uploadFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
                if (uploadFiles.Count == 0)
                {
                    CancelStartingZmodem("[ZMODEM upload cancelled]", generation);
                    return;
                }
            }

            List<byte[]> pending;
            ZmodemTransfer transfer;
            lock (_zmodemLock)
            {
                if (generation != _connectionGeneration || !_zmodemStarting || direction != _zmodemStartingDirection)
                    return;

                transfer = new ZmodemTransfer(
                    direction,
                    SendZmodemBytes,
                    ProcessTerminalBytes,
                    PostStatusMessage,
                    ClearZmodemTransfer,
                    downloadFolder,
                    _session?.FileTransferDuplicateAction,
                    uploadFiles);

                _zmodemTransfer = transfer;
                _zmodemStarting = false;
                pending = _zmodemPendingBytes.ToList();
                _zmodemPendingBytes.Clear();
            }

            transfer.Start();
            foreach (var chunk in pending)
                transfer.Feed(chunk);
        }
        catch (Exception ex)
        {
            CancelStartingZmodem($"[ZMODEM failed: {ex.Message}]", generation);
        }
    }

    private async Task BeginXymodemUploadAsync(int generation, XymodemProtocol protocol)
    {
        try
        {
            if (PickZmodemUploadFilesAsync == null)
                throw new InvalidOperationException("Upload file picker is not available.");

            var uploadFiles = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemUploadFilesAsync());
            uploadFiles = uploadFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            if (uploadFiles.Count == 0)
            {
                CancelStartingXymodem($"[{GetXymodemName(protocol)} upload cancelled]", generation);
                return;
            }

            if (protocol == XymodemProtocol.Xmodem && uploadFiles.Count > 1)
                uploadFiles = uploadFiles.Take(1).ToList();

            List<byte[]> pending;
            XymodemTransfer transfer;
            lock (_xymodemLock)
            {
                if (generation != _connectionGeneration || !_xymodemStarting)
                    return;

                transfer = new XymodemTransfer(
                    protocol,
                    XymodemTransferDirection.Upload,
                    SendXymodemBytes,
                    ProcessTerminalBytes,
                    PostStatusMessage,
                    ClearXymodemTransfer,
                    uploadFiles: uploadFiles,
                    uploadBlockSize: _session?.FileTransferXymodemBlockSize ?? 128);

                _xymodemTransfer = transfer;
                _xymodemStarting = false;
                pending = _xymodemPendingBytes.ToList();
                _xymodemPendingBytes.Clear();
            }

            transfer.Start();
            foreach (var chunk in pending)
                transfer.Feed(chunk);
        }
        catch (Exception ex)
        {
            CancelStartingXymodem($"[{GetXymodemName(protocol)} failed: {ex.Message}]", generation);
        }
    }

    private async Task BeginXymodemDownloadAsync(int generation, XymodemProtocol protocol, string? suggestedFileName)
    {
        try
        {
            var downloadFolder = GetConfiguredZmodemDownloadFolder();
            if (string.IsNullOrWhiteSpace(downloadFolder))
            {
                if (PickZmodemDownloadFolderAsync == null)
                    throw new InvalidOperationException("Download folder picker is not available.");

                downloadFolder = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemDownloadFolderAsync());
            }

            if (string.IsNullOrWhiteSpace(downloadFolder))
            {
                PostStatusMessage($"[{GetXymodemName(protocol)} download cancelled]", "33");
                TrySendBytes(_connection, new[] { (byte)24, (byte)24, (byte)24 });
                return;
            }

            XymodemTransfer transfer;
            lock (_xymodemLock)
            {
                if (generation != _connectionGeneration || _xymodemTransfer != null || _xymodemStarting)
                    return;

                transfer = new XymodemTransfer(
                    protocol,
                    XymodemTransferDirection.Download,
                    SendXymodemBytes,
                    ProcessTerminalBytes,
                    PostStatusMessage,
                    ClearXymodemTransfer,
                    downloadFolder,
                    _session?.FileTransferDuplicateAction,
                    suggestedDownloadFileName: suggestedFileName);

                _xymodemTransfer = transfer;
            }

            transfer.Start();
        }
        catch (Exception ex)
        {
            PostStatusMessage($"[{GetXymodemName(protocol)} failed: {ex.Message}]", "31");
            TrySendBytes(_connection, new[] { (byte)24, (byte)24, (byte)24 });
        }
    }

    private string? GetConfiguredZmodemDownloadFolder()
    {
        if (_session == null || _session.FileTransferAlwaysAskDownloadFolder)
            return null;

        var path = _session.FileTransferDownloadDirectory;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Directory.Exists(path) ? path : null;
    }

    private void CancelStartingZmodem(string message, int generation)
    {
        if (generation != _connectionGeneration)
            return;

        lock (_zmodemLock)
        {
            _zmodemStarting = false;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
        }

        TrySendBytes(_connection, new byte[] { 24, 24, 24, 24, 24, 8, 8, 8, 8, 8 });
        PostStatusMessage(message, "33");
    }

    private void CancelStartingXymodem(string message, int generation)
    {
        if (generation != _connectionGeneration)
            return;

        lock (_xymodemLock)
        {
            _xymodemStarting = false;
            _xymodemPendingBytes.Clear();
            _pendingXymodemUploadProtocol = null;
        }

        TrySendBytes(_connection, new[] { (byte)24, (byte)24, (byte)24 });
        PostStatusMessage(message, "33");
    }

    private void ClearZmodemTransfer()
    {
        lock (_zmodemLock)
        {
            _zmodemTransfer?.Dispose();
            _zmodemTransfer = null;
            _zmodemStarting = false;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
            _suppressZmodemOverAndOutUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            _pendingZmodemOverAndOutO = false;
        }
    }

    private bool TrySuppressLateZmodemOverAndOut(byte[] bytes)
    {
        if (bytes.Length == 0 || DateTimeOffset.UtcNow > _suppressZmodemOverAndOutUntil)
        {
            _pendingZmodemOverAndOutO = false;
            return false;
        }

        var index = 0;
        while (index < bytes.Length && IsZmodemPaddingByte(bytes[index]))
            index++;

        if (_pendingZmodemOverAndOutO)
        {
            _pendingZmodemOverAndOutO = false;
            if (index < bytes.Length && bytes[index] == (byte)'O')
            {
                ProcessTerminalBytes(bytes[(index + 1)..]);
                return true;
            }

            ProcessTerminalBytes(new[] { (byte)'O' });
            return false;
        }

        if (index >= bytes.Length)
            return index > 0;

        if (bytes[index] != (byte)'O')
            return false;

        if (index + 1 >= bytes.Length)
        {
            _pendingZmodemOverAndOutO = true;
            return true;
        }

        if (bytes[index + 1] != (byte)'O')
            return false;

        ProcessTerminalBytes(bytes[(index + 2)..]);
        return true;
    }

    private static bool IsZmodemPaddingByte(byte value)
    {
        return value is 0x11 or 0x13 or 0x91 or 0x93 or 0x8a or 0x8d;
    }

    private void ClearXymodemTransfer()
    {
        lock (_xymodemLock)
        {
            _xymodemTransfer?.Dispose();
            _xymodemTransfer = null;
            _xymodemStarting = false;
            _xymodemPendingBytes.Clear();
            _pendingXymodemUploadProtocol = null;
            ClearPendingXymodemDownload();
            _suppressXymodemResidualUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    private bool TrySuppressXymodemResidual(byte[] bytes)
    {
        if (bytes.Length == 0 || DateTimeOffset.UtcNow > _suppressXymodemResidualUntil)
            return false;

        if (!LooksLikeXymodemResidual(bytes))
            return false;

        var terminalStart = FindLikelyTerminalTextStart(bytes);
        if (terminalStart >= 0)
            ProcessTerminalBytes(bytes[terminalStart..]);

        return true;
    }

    private static bool LooksLikeXymodemResidual(byte[] bytes)
    {
        var protocolControls = 0;
        var nonPrintable = 0;
        var repeatedRequests = 0;

        foreach (var value in bytes)
        {
            if (value is 0x01 or 0x02 or 0x04 or 0x06 or 0x15 or 0x18)
                protocolControls++;

            if (value is (byte)'C' or 0x15 or 0x18)
                repeatedRequests++;

            if ((value < 0x20 && value is not 0x08 and not 0x09 and not 0x0a and not 0x0d) || value >= 0x80)
                nonPrintable++;
        }

        return protocolControls > 0 ||
               (bytes.Length >= 3 && repeatedRequests == bytes.Length) ||
               nonPrintable * 3 >= bytes.Length;
    }

    private static int FindLikelyTerminalTextStart(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            if (value is (byte)'C' or 0x01 or 0x02 or 0x04 or 0x06 or 0x15 or 0x18)
                continue;

            if (value < 0x20 && value is not 0x08 and not 0x09 and not 0x0a and not 0x0d and not 0x1b)
                continue;

            if (value >= 0x80)
                continue;

            return i;
        }

        return -1;
    }

    private bool TrySendData(ITerminalConnectionService? connection, string data)
    {
        if (connection == null || string.IsNullOrEmpty(data) || !connection.IsConnected)
            return false;

        var queue = _sendQueue;
        if (queue == null)
            return false;

        return queue.TryEnqueue(_ =>
        {
            if (ReferenceEquals(_connection, connection) && connection.IsConnected)
                connection.SendData(data);

            return Task.CompletedTask;
        });
    }

    private bool TrySendBytes(ITerminalConnectionService? connection, byte[] bytes)
    {
        if (connection == null || bytes.Length == 0 || !connection.IsConnected)
            return false;

        var queue = _sendQueue;
        if (queue == null)
            return false;

        var payload = bytes.ToArray();
        return queue.TryEnqueue(_ =>
        {
            if (ReferenceEquals(_connection, connection) && connection.IsConnected)
                connection.SendBytes(payload);

            return Task.CompletedTask;
        });
    }

    private bool TrySendKeepAlive(ITerminalConnectionService connection)
    {
        if (!connection.IsConnected)
            return false;

        var queue = _sendQueue;
        if (queue == null)
            return false;

        return queue.TryEnqueue(_ =>
        {
            if (ReferenceEquals(_connection, connection) && connection.IsConnected)
                connection.SendKeepAlive();

            return Task.CompletedTask;
        });
    }

    private void SendZmodemBytes(byte[] bytes)
    {
        TrySendBytes(_connection, bytes);
    }

    private void SendXymodemBytes(byte[] bytes)
    {
        TrySendBytes(_connection, bytes);
    }

    private static string GetXymodemName(XymodemProtocol protocol)
    {
        return protocol == XymodemProtocol.Ymodem ? "YMODEM" : "XMODEM";
    }

    private void ProcessTerminalBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return;

        var charCount = _terminalByteDecoder.GetCharCount(bytes, 0, bytes.Length);
        if (charCount == 0)
            return;

        var chars = new char[charCount];
        var charsRead = _terminalByteDecoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        var text = new string(chars, 0, charsRead);
        Dispatcher.UIThread.Post(() =>
        {
            text = ProcessAnswerback(text, _connection);
            Parser.Process(text);
            Buffer.MarkAllDirty();
            BufferChanged?.Invoke();
        });
    }

    private string ProcessAnswerback(string text, ITerminalConnectionService? connection)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\x05') < 0)
            return text;

        var answerback = _session?.TerminalAdvancedAnswerback ?? "CxShell";
        if (!string.IsNullOrEmpty(answerback) && connection?.IsConnected == true)
            TrySendData(connection, answerback);

        return text.Replace("\x05", string.Empty, StringComparison.Ordinal);
    }

    private void PostStatusMessage(string message, string colorCode)
    {
        Dispatcher.UIThread.Post(() => AppendPlainStatusMessage(message));
    }

    public void SendInput(string data)
    {
        SendInputCore(data, observeCommandLine: true);
    }

    private void SendInputCore(string data, bool observeCommandLine)
    {
        _lastUserInputAt = DateTimeOffset.UtcNow;
        if (observeCommandLine)
            ObservePotentialXymodemCommand(data);
        if (_session?.TerminalVtEchoMode == true)
        {
            Parser.Process(data);
            Buffer.MarkAllDirty();
            BufferChanged?.Invoke();
        }

        var connection = _connection;
        var session = _session;
        if (connection == null || session == null)
            return;

        if (ShouldDelayInput(data))
        {
            var queue = _sendQueue;
            if (queue == null)
                return;

            queue.TryEnqueue(async cancellationToken =>
                await SendInputWithDelayAsync(data, session, connection, cancellationToken));
        }
        else
        {
            TrySendData(connection, data);
        }
    }

    private bool ReplaceCurrentCommandLine(string replacement)
    {
        var currentLine = _outgoingCommandLine.ToString();
        var eraseMode = _session?.TerminalAdvancedDestructiveBackspace == true
            ? _session.TerminalDeleteKeySequence
            : _session?.TerminalBackspaceKeySequence;
        var eraseSequence = ResolveBackspaceSequence(eraseMode);
        var payload = string.Concat(Enumerable.Repeat(eraseSequence, currentLine.Length)) + replacement;

        // Keep the history cursor active while replacing the visible line so
        // repeated Up/Down presses can continue through the history.
        SendInputCore(payload, observeCommandLine: false);

        _outgoingCommandLine.Clear();
        _outgoingCommandLine.Append(replacement);
        CommandLineChanged?.Invoke();
        return true;
    }

    private static string ResolveBackspaceSequence(string? mode)
    {
        return mode?.Trim().ToUpperInvariant() switch
        {
            "ASCII127" => "\x7F",
            "VT220" => "\x1B[3~",
            _ => "\x08"
        };
    }

    private void ObservePotentialXymodemCommand(string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        var changed = false;
        foreach (var ch in data)
        {
            if (ch is '\r' or '\n')
            {
                var typedCommandLine = _outgoingCommandLine.ToString();
                var visibleCommandLine = GetVisibleXymodemCommandLine();
                var commandLine = visibleCommandLine ?? typedCommandLine;
                _outgoingCommandLine.Clear();
                if (!_suppressNextCommandHistoryEntry)
                    _commandHistory.Add(typedCommandLine);

                _suppressNextCommandHistoryEntry = false;
                HandlePotentialXymodemCommand(commandLine);
                HandlePotentialDirectoryChangeCommand(typedCommandLine, visibleCommandLine);
                changed = true;
                continue;
            }

            if (ch == '\x1B')
            {
                _inputEscapeSequenceState = 1;
                continue;
            }

            if (ch == '\x9B')
            {
                _inputEscapeSequenceState = 2;
                continue;
            }

            if (_inputEscapeSequenceState == 1)
            {
                _inputEscapeSequenceState = ch switch
                {
                    '[' or 'O' => 2,
                    ']' => 3,
                    _ => 0
                };
                continue;
            }

            if (_inputEscapeSequenceState == 2)
            {
                // CSI/SS3 sequences end at their final byte. Ignore all
                // parameters and printable bytes until then.
                if (ch is >= '@' and <= '~')
                    _inputEscapeSequenceState = 0;
                continue;
            }

            if (_inputEscapeSequenceState == 3)
            {
                if (ch == '\x07')
                    _inputEscapeSequenceState = 0;
                continue;
            }

            if (ch == '\b' || ch == '\x7f')
            {
                if (_outgoingCommandLine.Length > 0)
                {
                    _outgoingCommandLine.Length--;
                    changed = true;
                }
                _commandHistory.ResetNavigation();
                continue;
            }

            if (ch == '\x15' || ch == '\x03')
            {
                changed = _outgoingCommandLine.Length > 0;
                _outgoingCommandLine.Clear();
                _commandHistory.ResetNavigation();
                continue;
            }

            if (ch == '\x17')
            {
                var beforeLength = _outgoingCommandLine.Length;
                while (_outgoingCommandLine.Length > 0 && char.IsWhiteSpace(_outgoingCommandLine[^1]))
                    _outgoingCommandLine.Length--;
                while (_outgoingCommandLine.Length > 0 && !char.IsWhiteSpace(_outgoingCommandLine[^1]))
                    _outgoingCommandLine.Length--;
                changed |= beforeLength != _outgoingCommandLine.Length;
                _commandHistory.ResetNavigation();
                continue;
            }

            if (!char.IsControl(ch))
            {
                if (_outgoingCommandLine.Length < 1024)
                {
                    _outgoingCommandLine.Append(ch);
                    changed = true;
                }
                _commandHistory.ResetNavigation();
            }
        }

        if (changed)
            CommandLineChanged?.Invoke();
    }

    private void HandlePotentialXymodemCommand(string commandLine)
    {
        var command = ExtractXymodemCommandLine(commandLine);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var parts = SplitCommandLine(command);
        if (parts.Count == 0)
            return;

        var executable = NormalizeCommandExecutable(parts[0]);
        if (IsConfiguredCommandExecutable(executable, _session?.FileTransferXmodemUploadCommand, "rx"))
        {
            MarkPendingXymodemUpload(XymodemProtocol.Xmodem);
            return;
        }

        if (IsConfiguredCommandExecutable(executable, _session?.FileTransferYmodemUploadCommand, "rb", "ry"))
        {
            MarkPendingXymodemUpload(XymodemProtocol.Ymodem);
            return;
        }

        switch (executable)
        {
            case "sx":
                StartXymodemDownloadFromCommand(XymodemProtocol.Xmodem, parts);
                break;
            case "sb":
                StartXymodemDownloadFromCommand(XymodemProtocol.Ymodem, parts);
                break;
        }
    }

    private string? GetVisibleXymodemCommandLine()
    {
        var buffer = Buffer;
        if (buffer.Rows <= 0 || buffer.Columns <= 0)
            return null;

        var row = Math.Clamp(buffer.CursorRow, 0, buffer.Rows - 1);
        var line = new StringBuilder(buffer.Columns);
        for (var col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (!cell.IsWideContinuation)
                line.Append(cell.Character);
        }

        var text = line.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private string ExtractXymodemCommandLine(string commandLine)
    {
        var command = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        var directParts = SplitCommandLine(command);
        if (directParts.Count > 0 && IsXymodemExecutable(directParts[0]))
            return command;

        foreach (var candidate in GetXymodemCommandCandidates())
        {
            var index = FindCommandToken(command, candidate);
            if (index >= 0)
                return command[index..].TrimStart();
        }

        return command;
    }

    private static int FindCommandToken(string text, string command)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(command, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return -1;

            var beforeOk = index == 0 || char.IsWhiteSpace(text[index - 1]) || IsShellSeparator(text[index - 1]);
            var after = index + command.Length;
            var afterOk = after >= text.Length || char.IsWhiteSpace(text[after]);
            if (beforeOk && afterOk)
                return index;

            index += command.Length;
        }

        return -1;
    }

    private static bool IsShellSeparator(char ch)
    {
        return ch is '$' or '#' or '>' or ';' or '|';
    }

    private bool IsXymodemExecutable(string? executable)
    {
        var normalized = NormalizeCommandExecutable(executable);
        return normalized is "sx" or "sb" ||
               IsConfiguredCommandExecutable(normalized, _session?.FileTransferXmodemUploadCommand, "rx") ||
               IsConfiguredCommandExecutable(normalized, _session?.FileTransferYmodemUploadCommand, "rb", "ry");
    }

    private IEnumerable<string> GetXymodemCommandCandidates()
    {
        var candidates = new[]
        {
            GetConfiguredCommandExecutable(_session?.FileTransferXmodemUploadCommand),
            GetConfiguredCommandExecutable(_session?.FileTransferYmodemUploadCommand),
            "rx",
            "rb",
            "ry",
            "sx",
            "sb"
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static bool IsConfiguredCommandExecutable(string? executable, string? configuredCommand, params string[] fallbacks)
    {
        var normalized = NormalizeCommandExecutable(executable);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var configuredExecutable = GetConfiguredCommandExecutable(configuredCommand);
        if (!string.IsNullOrWhiteSpace(configuredExecutable) &&
            string.Equals(normalized, configuredExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fallbacks.Any(fallback =>
            string.Equals(normalized, NormalizeCommandExecutable(fallback), StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesConfiguredCommandText(string text, string? configuredCommand, params string[] fallbacks)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (!string.IsNullOrWhiteSpace(configuredCommand) &&
            string.Equals(trimmed, configuredCommand.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = SplitCommandLine(trimmed);
        if (parts.Count == 0)
            return false;

        return IsConfiguredCommandExecutable(parts[0], configuredCommand, fallbacks);
    }

    private static string? GetConfiguredCommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var parts = SplitCommandLine(command.Trim());
        return parts.Count == 0 ? null : NormalizeCommandExecutable(parts[0]);
    }

    private static string NormalizeCommandExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return string.Empty;

        return Path.GetFileName(executable.Trim()).ToLowerInvariant();
    }

    private void MarkPendingXymodemUpload(XymodemProtocol protocol)
    {
        lock (_xymodemLock)
        {
            if (_xymodemTransfer != null || _xymodemStarting)
                return;

            _pendingXymodemUploadProtocol = protocol;
            _pendingXymodemUploadAt = DateTimeOffset.UtcNow;
        }
    }

    private void StartXymodemDownloadFromCommand(XymodemProtocol protocol, IReadOnlyList<string> parts)
    {
        var suggestedFileName = GetSuggestedXymodemDownloadName(parts);
        lock (_xymodemLock)
        {
            if (_xymodemTransfer != null || _xymodemStarting)
                return;

            _pendingXymodemDownloadProtocol = protocol;
            _pendingXymodemDownloadFileName = suggestedFileName;
            _pendingXymodemDownloadAt = DateTimeOffset.UtcNow;
            _pendingXymodemDownloadGeneration = _connectionGeneration;
        }
    }

    private void TryStartPendingXymodemDownloadFromOutput(int generation, string output)
    {
        if (string.IsNullOrEmpty(output))
            return;

        XymodemProtocol? protocol;
        string? suggestedFileName;
        lock (_xymodemLock)
        {
            protocol = _pendingXymodemDownloadProtocol;
            if (protocol == null)
                return;

            if (generation != _pendingXymodemDownloadGeneration ||
                DateTimeOffset.UtcNow - _pendingXymodemDownloadAt > TimeSpan.FromMinutes(2))
            {
                ClearPendingXymodemDownload();
                return;
            }

            if (output.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingXymodemDownload();
                return;
            }

            if (!OutputContainsReceivePrompt(output, protocol.Value))
                return;

            suggestedFileName = _pendingXymodemDownloadFileName;
            ClearPendingXymodemDownload();
        }

        _ = BeginXymodemDownloadAsync(generation, protocol.Value, suggestedFileName);
    }

    private void TryDetectPendingXymodemUploadFromOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output) ||
            !output.Contains("waiting to receive", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (output.Contains("rb", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ry", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("YMODEM", StringComparison.OrdinalIgnoreCase))
        {
            MarkPendingXymodemUpload(XymodemProtocol.Ymodem);
            return;
        }

        if (output.Contains("rx", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("XMODEM", StringComparison.OrdinalIgnoreCase))
        {
            MarkPendingXymodemUpload(XymodemProtocol.Xmodem);
            return;
        }

        var configuredProtocol = GetConfiguredXymodemUploadProtocol();
        if (configuredProtocol != null)
            MarkPendingXymodemUpload(configuredProtocol.Value);
    }

    private XymodemProtocol? GetConfiguredXymodemUploadProtocol()
    {
        return _session?.FileTransferUploadProtocol?.Trim().ToLowerInvariant() switch
        {
            "xmodem" => XymodemProtocol.Xmodem,
            "ymodem" => XymodemProtocol.Ymodem,
            _ => null
        };
    }

    private static bool OutputContainsReceivePrompt(string output, XymodemProtocol protocol)
    {
        var expected = protocol == XymodemProtocol.Xmodem ? "XMODEM" : "YMODEM";
        return output.Contains("receive command", StringComparison.OrdinalIgnoreCase) &&
               output.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearPendingXymodemDownload()
    {
        _pendingXymodemDownloadProtocol = null;
        _pendingXymodemDownloadFileName = null;
        _pendingXymodemDownloadAt = DateTimeOffset.MinValue;
        _pendingXymodemDownloadGeneration = 0;
    }

    private static string? GetSuggestedXymodemDownloadName(IReadOnlyList<string> parts)
    {
        for (var i = parts.Count - 1; i >= 1; i--)
        {
            var value = parts[i];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
                continue;

            return Path.GetFileName(value.Trim('"', '\''));
        }

        return null;
    }

    private static List<string> SplitCommandLine(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var ch in command)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else
                    current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private bool ShouldDelayInput(string data)
    {
        if (_session == null || string.IsNullOrEmpty(data))
            return false;

        return _session.AdvancedCharacterDelayMilliseconds > 0 ||
               (_session.AdvancedUseLineDelay && _session.AdvancedLineDelayMilliseconds > 0) ||
               (_session.AdvancedUsePromptDelay && !string.IsNullOrEmpty(_session.AdvancedPromptText));
    }

    private async Task SendInputWithDelayAsync(
        string data,
        SessionInfo session,
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
            return;

        var characterDelay = Math.Clamp(session.AdvancedCharacterDelayMilliseconds, 0, 60000);
        var lineDelay = session.AdvancedUseLineDelay
            ? Math.Clamp(session.AdvancedLineDelayMilliseconds, 0, 60000)
            : 0;

        if (session.AdvancedUsePromptDelay && !string.IsNullOrEmpty(session.AdvancedPromptText))
        {
            var segments = SplitInputLines(data).ToArray();
            for (var i = 0; i < segments.Length; i++)
            {
                if (!await SendSegmentWithCharacterDelayAsync(
                        segments[i],
                        connection,
                        characterDelay,
                        cancellationToken))
                {
                    return;
                }

                if (i + 1 < segments.Length)
                    await WaitForPromptAsync(
                        session.AdvancedPromptText,
                        session.AdvancedPromptMaxWaitMilliseconds,
                        cancellationToken);
            }
            return;
        }

        foreach (var segment in SplitInputLines(data))
        {
            if (!await SendSegmentWithCharacterDelayAsync(
                    segment,
                    connection,
                    characterDelay,
                    cancellationToken))
            {
                return;
            }

            if (lineDelay > 0 && EndsWithLineBreak(segment))
                await Task.Delay(lineDelay, cancellationToken);
        }
    }

    private static IEnumerable<string> SplitInputLines(string data)
    {
        if (string.IsNullOrEmpty(data))
            yield break;

        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != '\r' && data[i] != '\n')
                continue;

            if (data[i] == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                i++;

            yield return data[start..(i + 1)];
            start = i + 1;
        }

        if (start < data.Length)
            yield return data[start..];
    }

    private static bool EndsWithLineBreak(string value)
    {
        return value.EndsWith('\r') || value.EndsWith('\n');
    }

    private async Task<bool> SendSegmentWithCharacterDelayAsync(
        string segment,
        ITerminalConnectionService connection,
        int characterDelay,
        CancellationToken cancellationToken)
    {
        if (characterDelay <= 0)
        {
            if (!IsCurrentSendConnection(connection, cancellationToken))
                return false;

            connection.SendData(segment);
            return true;
        }

        foreach (var ch in segment)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSendConnection(connection, cancellationToken))
                return false;

            connection.SendData(ch.ToString());
            await Task.Delay(characterDelay, cancellationToken);
        }

        return true;
    }

    private bool IsCurrentSendConnection(
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
               ReferenceEquals(_connection, connection) &&
               connection.IsConnected;
    }

    private async Task WaitForPromptAsync(
        string prompt,
        int maxWaitMilliseconds,
        CancellationToken cancellationToken)
    {
        var timeout = Math.Clamp(maxWaitMilliseconds, 0, 600000);
        if (timeout == 0)
            return;

        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < TimeSpan.FromMilliseconds(timeout))
        {
            lock (_recentOutputLock)
            {
                if (_recentOutputBuffer.ToString().Contains(prompt, StringComparison.Ordinal))
                    return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private void AppendRecentOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_recentOutputLock)
        {
            _recentOutputBuffer.Append(text);
            if (_recentOutputBuffer.Length > 8192)
                _recentOutputBuffer.Remove(0, _recentOutputBuffer.Length - 8192);
        }
    }

    public void Resize(int columns, int rows, bool notifyRemote = true)
    {
        if (_session?.TerminalFixedSize == true)
            return;

        if (columns == Columns && rows == Rows)
        {
            if (notifyRemote)
                _connection?.ResizeTerminal(columns, rows);
            return;
        }

        Columns = columns;
        Rows = rows;
        Buffer.Resize(columns, rows);
        if (notifyRemote)
            _connection?.ResizeTerminal(columns, rows);
    }

    public void ApplyConfiguredTerminalSize()
    {
        if (_session?.TerminalFixedSize != true)
            return;

        var columns = Math.Clamp(_session.TerminalColumns, 20, 500);
        var rows = Math.Clamp(_session.TerminalRows, 5, 200);
        Columns = columns;
        Rows = rows;
        Buffer.Resize(columns, rows);
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private void StartKeepAliveLoop(
        int generation,
        SessionInfo session,
        ITerminalConnectionService connection,
        CancellationToken parentCancellationToken)
    {
        StopKeepAliveLoop();

        var sendSessionKeepAlive = session.SendSessionKeepAlive;
        var sendIdleString = session.SendIdleString && !string.IsNullOrEmpty(session.IdleString);
        if (!sendSessionKeepAlive && !sendIdleString)
            return;

        _lastUserInputAt = DateTimeOffset.UtcNow;
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
        var cancellationToken = _keepAliveCts.Token;
        _keepAliveTask = Task.Run(async () =>
        {
            var lastSessionKeepAliveAt = DateTimeOffset.UtcNow;
            var lastIdleStringAt = DateTimeOffset.UtcNow;
            var sessionInterval = TimeSpan.FromSeconds(Math.Max(1, session.SessionKeepAliveIntervalSeconds));
            var idleInterval = TimeSpan.FromSeconds(Math.Max(1, session.IdleStringIntervalSeconds));

            while (!cancellationToken.IsCancellationRequested &&
                   generation == _connectionGeneration &&
                   !_manualDisconnect)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (!connection.IsConnected)
                        continue;

                    var now = DateTimeOffset.UtcNow;
                    if (sendSessionKeepAlive && now - lastSessionKeepAliveAt >= sessionInterval)
                    {
                        TrySendKeepAlive(connection);
                        lastSessionKeepAliveAt = now;
                    }

                    if (sendIdleString &&
                        now - _lastUserInputAt >= idleInterval &&
                        now - lastIdleStringAt >= idleInterval)
                    {
                        TrySendData(connection, session.IdleString);
                        lastIdleStringAt = now;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (generation == _connectionGeneration && !_manualDisconnect)
                            AppendStatusMessage($"[Keepalive failed: {ex.Message}]", "31");
                    });
                }
            }
        }, cancellationToken);
    }

    private void StopKeepAliveLoop()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        _keepAliveTask = null;
    }

    public void Disconnect(string? statusMessage = null)
    {
        _manualDisconnect = true;
        InvalidateReconnectLoop();
        _session = null;
        OnPropertyChanged(nameof(IsTerminalSizeFixed));
        NotifyKeyboardOptionsChanged();
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _connectionGeneration++;
        ClearPendingTerminalOutput();

        var connection = _connection;
        _connection = null;
        StopKeepAliveLoop();
        var sendQueue = Interlocked.Exchange(ref _sendQueue, null);
        sendQueue?.Dispose();
        connection?.Dispose();
        ClearZmodemTransfer();
        ClearXymodemTransfer();
        _outgoingCommandLine.Clear();
        _commandHistory.Clear();
        _inputEscapeSequenceState = 0;
        _suppressNextCommandHistoryEntry = false;
        IsConnected = false;
        SupportsPosixShellFeatures = true;
        HostInfo = string.Empty;
        RemoteTitle = string.Empty;
        SshTunnelRuntimeChanged?.Invoke();

        if (!string.IsNullOrWhiteSpace(statusMessage))
            AppendStatusMessage(statusMessage, "33");

        StopSessionLog();
        StopSessionRecording();
    }

    public void CloseDetached()
    {
        _manualDisconnect = true;
        InvalidateReconnectLoop();
        _session = null;
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _connectionGeneration++;
        ClearPendingTerminalOutput();

        var connection = _connection;
        _connection = null;
        StopKeepAliveLoop();
        var sendQueue = Interlocked.Exchange(ref _sendQueue, null);
        sendQueue?.Dispose();
        ClearZmodemTransfer();
        ClearXymodemTransfer();
        _outgoingCommandLine.Clear();
        _commandHistory.Clear();
        _inputEscapeSequenceState = 0;
        _suppressNextCommandHistoryEntry = false;
        IsConnected = false;
        SupportsPosixShellFeatures = true;
        HostInfo = string.Empty;
        RemoteTitle = string.Empty;
        SshTunnelRuntimeChanged?.Invoke();
        StopSessionLog();
        StopSessionRecording();

        if (connection == null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Terminal close cleanup failed: {ex.Message}");
            }
        });
    }

    private static ITerminalConnectionService CreateConnectionService(SessionProtocol protocol)
    {
        return protocol switch
        {
            SessionProtocol.TELNET => new TelnetConnectionService(),
            SessionProtocol.RLOGIN => new RloginConnectionService(),
            SessionProtocol.SERIAL => new SerialConnectionService(),
            _ => new SshConnectionService()
        };
    }

    private void NotifyKeyboardOptionsChanged()
    {
        OnPropertyChanged(nameof(KeyboardFunctionKeyMode));
        OnPropertyChanged(nameof(KeyboardMappingFile));
        OnPropertyChanged(nameof(DeleteKeySequence));
        OnPropertyChanged(nameof(BackspaceKeySequence));
        OnPropertyChanged(nameof(LeftAltAsMeta));
        OnPropertyChanged(nameof(RightAltAsMeta));
        OnPropertyChanged(nameof(CtrlAltAsAltGr));
        OnPropertyChanged(nameof(NewLineMode));
        OnPropertyChanged(nameof(EchoMode));
        OnPropertyChanged(nameof(CursorKeyMode));
        OnPropertyChanged(nameof(NumericKeypadMode));
        OnPropertyChanged(nameof(UseApplicationCursorMode));
        OnPropertyChanged(nameof(ShiftLimitsApplicationCursorMode));
        OnPropertyChanged(nameof(ScrollToBottomOnInputOutput));
        OnPropertyChanged(nameof(SuspendScrollToBottomOnScrollLock));
        OnPropertyChanged(nameof(ScrollToBottomByKey));
        OnPropertyChanged(nameof(DestructiveBackspace));
        OnPropertyChanged(nameof(UseRxvtHomeEnd));
        OnPropertyChanged(nameof(AppearanceFontFamily));
        OnPropertyChanged(nameof(AppearanceFontStyle));
        OnPropertyChanged(nameof(AppearanceFontSize));
        OnPropertyChanged(nameof(AppearanceCjkFontFamily));
        OnPropertyChanged(nameof(AppearanceCjkFontStyle));
        OnPropertyChanged(nameof(AppearanceCjkFontSize));
        OnPropertyChanged(nameof(AppearanceUseVariablePitchFont));
        OnPropertyChanged(nameof(AppearanceFontQuality));
        OnPropertyChanged(nameof(AppearanceCursorColor));
        OnPropertyChanged(nameof(AppearanceCursorTextColor));
        OnPropertyChanged(nameof(AppearanceCursorShape));
        OnPropertyChanged(nameof(AppearanceUseBlinkingCursor));
        OnPropertyChanged(nameof(AppearanceCursorBlinkSpeedMilliseconds));
        OnPropertyChanged(nameof(AppearanceTerminalPadding));
        OnPropertyChanged(nameof(AppearanceLineSpacing));
        OnPropertyChanged(nameof(AppearanceCharacterSpacing));
        OnPropertyChanged(nameof(AppearanceBackgroundImagePath));
        OnPropertyChanged(nameof(AppearanceBackgroundImagePosition));
        OnPropertyChanged(nameof(AppearanceHighlightRules));
    }

    private static Color ParseColorOrDefault(string? value, string fallback)
    {
        return Color.TryParse(value, out var color) ? color : Color.Parse(fallback);
    }

    private static Color[] ParseAnsiColors(string? value)
    {
        var fallback = TerminalColors.Standard16.ToArray();
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var colors = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => Color.TryParse(text, out var color) ? color : (Color?)null)
            .Where(color => color.HasValue)
            .Select(color => color!.Value)
            .ToArray();

        return colors.Length >= 16 ? colors.Take(16).ToArray() : fallback;
    }

    private static string GetHostInfo(SessionInfo session)
    {
        return session.Protocol switch
        {
            SessionProtocol.SERIAL => session.SerialPortName,
            _ => $"{session.Username}@{session.Host}:{session.Port}"
        };
    }
}
