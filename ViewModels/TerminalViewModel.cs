using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _hostInfo = string.Empty;
    [ObservableProperty] private int _columns = 80;
    [ObservableProperty] private int _rows = 24;

    public TerminalBuffer Buffer { get; private set; }
    public AnsiParser Parser { get; private set; }

    private ITerminalConnectionService? _connection;
    private SessionInfo? _session;
    private string? _password;
    private CancellationTokenSource? _connectionCts;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _manualDisconnect = true;
    private int _connectionGeneration;
    private readonly object _zmodemLock = new();
    private readonly List<byte[]> _zmodemPendingBytes = new();
    private readonly List<byte> _zmodemProbeBytes = new();
    private readonly object _xymodemLock = new();
    private readonly List<byte[]> _xymodemPendingBytes = new();
    private readonly StringBuilder _outgoingCommandLine = new();
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
    private readonly object _recentOutputLock = new();
    private readonly StringBuilder _recentOutputBuffer = new();
    private readonly object _hiddenInputEchoLock = new();
    private DateTimeOffset _lastBellAt = DateTimeOffset.MinValue;
    private DateTimeOffset _bellMutedUntil = DateTimeOffset.MinValue;
    private string? _hiddenInputEcho;
    private int _hiddenInputEchoMatchIndex;
    private SessionLogWriter? _sessionLogWriter;

    public Func<Task<IReadOnlyList<string>>>? PickZmodemUploadFilesAsync { get; set; }
    public Func<Task<string?>>? PickZmodemDownloadFolderAsync { get; set; }
    public Func<Task<string?>>? PickSessionLogFileAsync { get; set; }
    public string ZmodemUploadStartDirectory => _session?.FileTransferUploadDirectory ?? string.Empty;
    public bool IsTerminalSizeFixed => _session?.TerminalFixedSize == true;
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
        }

        NotifyKeyboardOptionsChanged();
        BufferChanged?.Invoke();
    }

    public event Action? BufferChanged;
    public event Action? BellRequested;
    public event Action<string>? RemoteCurrentDirectoryChanged;
    public string? RemoteCurrentDirectory { get; private set; }

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();
        _session = session;
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
            session.TerminalAdvancedDisableTitleChange,
            session.TerminalAdvancedDisableTerminalPrint,
            session.TerminalAdvancedIgnoreResizeRequest,
            session.TerminalAdvancedUseBuiltinLineDrawing,
            session.TerminalAdvancedUseBuiltinPowerline,
            ParseColorOrDefault(session.AppearanceForegroundColor, "#CCCCCC"),
            ParseColorOrDefault(session.AppearanceBackgroundColor, "#000000"),
            ParseColorOrDefault(session.AppearanceBoldForegroundColor, "#33FF33"),
            ParseAnsiColors(session.AppearanceAnsiColors));
        Parser = new AnsiParser(Buffer);
        AttachParserHandlers(Parser);
        RemoteCurrentDirectory = null;
        _terminalByteDecoder.Reset();
        lock (_recentOutputLock)
            _recentOutputBuffer.Clear();
        OnPropertyChanged(nameof(Buffer));

        await ConnectCoreAsync(_connectionCts.Token);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_session == null)
            return;

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int generation = ++_connectionGeneration;
            var previous = _connection;
            _connection = null;
            previous?.Dispose();

            var connection = CreateConnectionService(_session.Protocol);
            _connection = connection;
            PrepareLoginScript(_session);

            connection.DataReceived += data =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _connectionGeneration)
                        return;

                    var terminalData = ProcessAnswerback(data, connection);
                    terminalData = TerminalSessionOptions.NormalizeReceiveLineEndings(terminalData, _session);
                    terminalData = FilterHiddenInputEcho(terminalData);
                    if (string.IsNullOrEmpty(terminalData))
                        return;

                    LogTerminalData(terminalData);
                    AppendRecentOutput(terminalData);
                    HandleLoginScriptData(generation, connection, terminalData);
                    TryDetectPendingXymodemUploadFromOutput(terminalData);
                    TryStartPendingXymodemDownloadFromOutput(generation, terminalData);
                    Parser.Process(terminalData);
                    Buffer.MarkAllDirty();
                    BufferChanged?.Invoke();
                });
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

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _connectionGeneration || _manualDisconnect)
                return;

            connection.ResizeTerminal(Columns, Rows);
            IsConnected = true;
            HostInfo = GetHostInfo(_session);
            await StartSessionLogIfNeededAsync(_session);
            StartKeepAliveLoop(generation, _session, connection, cancellationToken);
            StartLoginScriptFileAsync(generation, _session, connection, cancellationToken);
            SendPreinputString(connection, _session);
            StartRemoteDirectoryTrackingAsync(generation, _session, connection, cancellationToken);
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

    private void OnOperatingSystemCommandReceived(string command)
    {
        if (!TryParseOsc7CurrentDirectory(command, out var path))
            return;

        if (string.Equals(RemoteCurrentDirectory, path, StringComparison.Ordinal))
            return;

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
        StopKeepAliveLoop();
        AppendStatusMessage($"[Connection closed: {reason}]", "31");

        if (_session?.AutoReconnect != true)
        {
            AppendStatusMessage("[Auto reconnect disabled]", "33");
            return;
        }

        var cancellationToken = _connectionCts?.Token ?? CancellationToken.None;
        _ = ReconnectLoopAsync(generation, cancellationToken);
    }

    private async Task ReconnectLoopAsync(int disconnectedGeneration, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        while (!_manualDisconnect && disconnectedGeneration == _connectionGeneration)
        {
            try
            {
                var reconnectDelay = TimeSpan.FromSeconds(Math.Max(1, _session?.ReconnectIntervalSeconds ?? 30));
                var limitMinutes = Math.Max(0, _session?.ReconnectLimitMinutes ?? 0);
                if (limitMinutes > 0 && DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(limitMinutes))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        AppendStatusMessage($"[Auto reconnect stopped after {limitMinutes} minute(s)]", "33"));
                    return;
                }

                await Task.Delay(reconnectDelay, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage($"[Auto reconnecting; retry interval: {reconnectDelay.TotalSeconds:0}s...]", "33"));

                await ConnectCoreAsync(cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage("[Auto reconnect succeeded]", "32"));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage($"[Auto reconnect failed: {ex.Message}]", "31"));
                disconnectedGeneration = _connectionGeneration;
            }
        }
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
        _pendingLoginScriptRules = session.EnableLoginScriptRules
            ? session.LoginScriptRules
                .OrderBy(rule => rule.SortOrder)
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Expect))
                .Select(SessionEditViewModel.CloneLoginScriptRule)
                .ToList()
            : new List<LoginScriptRule>();
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
            if (!_loginScriptProbeBuffer.ToString().Contains(rule.Expect, StringComparison.Ordinal))
                return;

            _pendingLoginScriptRules.RemoveAt(0);
            _loginScriptProbeBuffer.Clear();
            if (!string.IsNullOrEmpty(rule.Send))
                connection.SendData(NormalizeScriptSendText(rule.Send));
        }
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

                connection.SendData(NormalizeScriptSendText(ApplyLoginScriptParameters(scriptText, session.LoginScriptParameters)));
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

    private static void SendPreinputString(ITerminalConnectionService connection, SessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(session.TerminalAdvancedPreinputString) || !connection.IsConnected)
            return;

        var text = NormalizeScriptSendText(session.TerminalAdvancedPreinputString);
        text = TerminalSessionOptions.NormalizeSendLineEndings(text, session);
        if (!string.IsNullOrEmpty(text))
            connection.SendData(text);
    }

    private void StartRemoteDirectoryTrackingAsync(
        int generation,
        SessionInfo session,
        ITerminalConnectionService connection,
        CancellationToken cancellationToken)
    {
        if (session.Protocol != SessionProtocol.SSH ||
            session.SshNoTerminal ||
            !session.SftpFollowTerminalDirectory)
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

                var bootstrap = BuildRemoteDirectoryTrackingBootstrap();
                RegisterHiddenInputEcho(bootstrap);
                var text = TerminalSessionOptions.NormalizeSendLineEndings(bootstrap + "\r", session);
                connection.SendData(text);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void RegisterHiddenInputEcho(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_hiddenInputEchoLock)
        {
            _hiddenInputEcho = text;
            _hiddenInputEchoMatchIndex = 0;
        }
    }

    private string FilterHiddenInputEcho(string data)
    {
        if (string.IsNullOrEmpty(data))
            return data;

        lock (_hiddenInputEchoLock)
        {
            if (string.IsNullOrEmpty(_hiddenInputEcho))
                return data;

            var output = new StringBuilder(data.Length);
            foreach (var ch in data)
            {
                var target = _hiddenInputEcho;
                if (string.IsNullOrEmpty(target))
                {
                    output.Append(ch);
                    continue;
                }

                if (ch == target[_hiddenInputEchoMatchIndex])
                {
                    _hiddenInputEchoMatchIndex++;
                    if (_hiddenInputEchoMatchIndex == target.Length)
                    {
                        _hiddenInputEcho = null;
                        _hiddenInputEchoMatchIndex = 0;
                    }

                    continue;
                }

                if (_hiddenInputEchoMatchIndex > 0)
                {
                    output.Append(target.AsSpan(0, _hiddenInputEchoMatchIndex));
                    _hiddenInputEchoMatchIndex = 0;

                    if (ch == target[0])
                    {
                        _hiddenInputEchoMatchIndex = 1;
                        continue;
                    }
                }

                output.Append(ch);
            }

            return output.ToString();
        }
    }

    private static string BuildRemoteDirectoryTrackingBootstrap()
    {
        return "__cxshell_osc7(){ __cxshell_h=$(hostname 2>/dev/null||printf localhost); printf '\\033]7;file://%s%s\\007' \"$__cxshell_h\" \"$PWD\"; }; " +
               "if [ -n \"${ZSH_VERSION-}\" ]; then case \" ${precmd_functions[*]-} \" in *\" __cxshell_osc7 \"*) ;; *) eval 'precmd_functions+=(__cxshell_osc7)' ;; esac; " +
               "elif [ -n \"${BASH_VERSION-}\" ]; then case \";${PROMPT_COMMAND-};\" in *\";__cxshell_osc7;\"*) ;; *) PROMPT_COMMAND=\"__cxshell_osc7${PROMPT_COMMAND:+;$PROMPT_COMMAND}\" ;; esac; fi; " +
               "if [ -n \"${BASH_VERSION-}${ZSH_VERSION-}\" ]; then __cxshell_osc7; fi";
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
                _connection?.SendBytes(new[] { (byte)24, (byte)24, (byte)24, (byte)24, (byte)24 });
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

    private static bool ShouldSuppressZmodemPreamble(ZmodemTransferDirection direction, byte[] bytes)
    {
        if (direction != ZmodemTransferDirection.Download || bytes.Length == 0)
            return false;

        var text = Encoding.ASCII.GetString(bytes).Trim('\r', '\n');
        return text == "rz";
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
                _connection?.SendBytes(new[] { (byte)24, (byte)24, (byte)24 });
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
            _connection?.SendBytes(new[] { (byte)24, (byte)24, (byte)24 });
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

        _connection?.SendBytes(new byte[] { 24, 24, 24, 24, 24, 8, 8, 8, 8, 8 });
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

        _connection?.SendBytes(new[] { (byte)24, (byte)24, (byte)24 });
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

    private void SendZmodemBytes(byte[] bytes)
    {
        _connection?.SendBytes(bytes);
    }

    private void SendXymodemBytes(byte[] bytes)
    {
        _connection?.SendBytes(bytes);
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
            connection.SendData(answerback);

        return text.Replace("\x05", string.Empty, StringComparison.Ordinal);
    }

    private void PostStatusMessage(string message, string colorCode)
    {
        Dispatcher.UIThread.Post(() => AppendPlainStatusMessage(message));
    }

    public void SendInput(string data)
    {
        _lastUserInputAt = DateTimeOffset.UtcNow;
        ObservePotentialXymodemCommand(data);
        if (_session?.TerminalVtEchoMode == true)
        {
            Parser.Process(data);
            Buffer.MarkAllDirty();
            BufferChanged?.Invoke();
        }

        if (ShouldDelayInput(data))
            _ = SendInputWithDelayAsync(data, _session!, _connection);
        else
            _connection?.SendData(data);
    }

    private void ObservePotentialXymodemCommand(string data)
    {
        if (string.IsNullOrEmpty(data))
            return;

        foreach (var ch in data)
        {
            if (ch is '\r' or '\n')
            {
                var commandLine = GetVisibleXymodemCommandLine() ?? _outgoingCommandLine.ToString();
                _outgoingCommandLine.Clear();
                HandlePotentialXymodemCommand(commandLine);
                continue;
            }

            if (ch == '\b' || ch == '\x7f')
            {
                if (_outgoingCommandLine.Length > 0)
                    _outgoingCommandLine.Length--;
                continue;
            }

            if (!char.IsControl(ch))
            {
                if (_outgoingCommandLine.Length < 1024)
                    _outgoingCommandLine.Append(ch);
            }
        }
    }

    private void HandlePotentialXymodemCommand(string commandLine)
    {
        var command = ExtractXymodemCommandLine(commandLine);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var parts = SplitCommandLine(command);
        if (parts.Count == 0)
            return;

        var executable = Path.GetFileName(parts[0]).ToLowerInvariant();
        switch (executable)
        {
            case "rx":
                MarkPendingXymodemUpload(XymodemProtocol.Xmodem);
                break;
            case "rb":
            case "ry":
                MarkPendingXymodemUpload(XymodemProtocol.Ymodem);
                break;
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

    private static string ExtractXymodemCommandLine(string commandLine)
    {
        var command = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        var directParts = SplitCommandLine(command);
        if (directParts.Count > 0 && IsXymodemExecutable(Path.GetFileName(directParts[0])))
            return command;

        var candidates = new[] { "rx", "rb", "ry", "sx", "sb" };
        foreach (var candidate in candidates)
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

    private static bool IsXymodemExecutable(string? executable)
    {
        return executable?.ToLowerInvariant() is "rx" or "rb" or "ry" or "sx" or "sb";
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
        }
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

    private async Task SendInputWithDelayAsync(string data, SessionInfo session, ITerminalConnectionService? connection)
    {
        if (connection?.IsConnected != true)
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
                await SendSegmentWithCharacterDelayAsync(segments[i], connection, characterDelay);
                if (i + 1 < segments.Length)
                    await WaitForPromptAsync(session.AdvancedPromptText, session.AdvancedPromptMaxWaitMilliseconds);
            }
            return;
        }

        foreach (var segment in SplitInputLines(data))
        {
            await SendSegmentWithCharacterDelayAsync(segment, connection, characterDelay);
            if (lineDelay > 0 && EndsWithLineBreak(segment))
                await Task.Delay(lineDelay);
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

    private static async Task SendSegmentWithCharacterDelayAsync(
        string segment,
        ITerminalConnectionService connection,
        int characterDelay)
    {
        if (characterDelay <= 0)
        {
            connection.SendData(segment);
            return;
        }

        foreach (var ch in segment)
        {
            connection.SendData(ch.ToString());
            await Task.Delay(characterDelay);
        }
    }

    private async Task WaitForPromptAsync(string prompt, int maxWaitMilliseconds)
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

            await Task.Delay(50);
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
                        connection.SendKeepAlive();
                        lastSessionKeepAliveAt = now;
                    }

                    if (sendIdleString &&
                        now - _lastUserInputAt >= idleInterval &&
                        now - lastIdleStringAt >= idleInterval)
                    {
                        connection.SendData(session.IdleString);
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
        _session = null;
        OnPropertyChanged(nameof(IsTerminalSizeFixed));
        NotifyKeyboardOptionsChanged();
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _connectionGeneration++;

        var connection = _connection;
        _connection = null;
        StopKeepAliveLoop();
        connection?.Dispose();
        ClearZmodemTransfer();
        ClearXymodemTransfer();
        _outgoingCommandLine.Clear();
        IsConnected = false;
        HostInfo = string.Empty;

        if (!string.IsNullOrWhiteSpace(statusMessage))
            AppendStatusMessage(statusMessage, "33");

        StopSessionLog();
    }

    public void CloseDetached()
    {
        _manualDisconnect = true;
        _session = null;
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _connectionGeneration++;

        var connection = _connection;
        _connection = null;
        StopKeepAliveLoop();
        ClearZmodemTransfer();
        ClearXymodemTransfer();
        _outgoingCommandLine.Clear();
        IsConnected = false;
        HostInfo = string.Empty;
        StopSessionLog();

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
