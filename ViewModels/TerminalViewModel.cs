using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Media;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.Terminal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChiXueSsh.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
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
    private Decoder _terminalByteDecoder = Encoding.UTF8.GetDecoder();
    private ZmodemTransfer? _zmodemTransfer;
    private bool _zmodemStarting;
    private ZmodemTransferDirection _zmodemStartingDirection;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private DateTimeOffset _lastUserInputAt = DateTimeOffset.UtcNow;
    private readonly StringBuilder _loginScriptProbeBuffer = new();
    private List<LoginScriptRule> _pendingLoginScriptRules = new();
    private readonly object _recentOutputLock = new();
    private readonly StringBuilder _recentOutputBuffer = new();

    public Func<Task<IReadOnlyList<string>>>? PickZmodemUploadFilesAsync { get; set; }
    public Func<Task<string?>>? PickZmodemDownloadFolderAsync { get; set; }
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
    public bool UseRxvtHomeEnd => _session?.TerminalAdvancedUseRxvtHomeEnd == true;
    public string AppearanceFontFamily => _session?.AppearanceFontFamily ?? "DejaVu Sans Mono";
    public string AppearanceFontStyle => _session?.AppearanceFontStyle ?? "Normal";
    public double AppearanceFontSize => Math.Clamp(_session?.AppearanceFontSize ?? 14, 6, 96);
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
    }

    public event Action? BufferChanged;

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
                    AppendRecentOutput(terminalData);
                    HandleLoginScriptData(generation, connection, terminalData);
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
            StartKeepAliveLoop(generation, _session, connection, cancellationToken);
            StartLoginScriptFileAsync(generation, _session, connection, cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

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
        Parser.Process($"\r\n\x1B[{colorCode}m{message}\x1B[0m\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private void AppendPlainStatusMessage(string message)
    {
        Parser.Process($"\r\n{message}\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
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

        ZmodemTransfer? transfer = null;
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

    private async Task BeginZmodemTransferAsync(int generation, ZmodemTransferDirection direction)
    {
        try
        {
            string? downloadFolder = null;
            IReadOnlyList<string>? uploadFiles = null;

            if (direction == ZmodemTransferDirection.Download)
            {
                if (PickZmodemDownloadFolderAsync == null)
                    throw new InvalidOperationException("Download folder picker is not available.");

                downloadFolder = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemDownloadFolderAsync());
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

    private void ClearZmodemTransfer()
    {
        lock (_zmodemLock)
        {
            _zmodemTransfer?.Dispose();
            _zmodemTransfer = null;
            _zmodemStarting = false;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
        }
    }

    private void SendZmodemBytes(byte[] bytes)
    {
        _connection?.SendBytes(bytes);
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

    public void Resize(int columns, int rows)
    {
        if (_session?.TerminalFixedSize == true)
            return;

        if (columns == Columns && rows == Rows)
            return;

        Columns = columns;
        Rows = rows;
        Buffer.Resize(columns, rows);
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
        IsConnected = false;
        HostInfo = string.Empty;

        if (!string.IsNullOrWhiteSpace(statusMessage))
            AppendStatusMessage(statusMessage, "33");
    }

    private static ITerminalConnectionService CreateConnectionService(SessionProtocol protocol)
    {
        return protocol switch
        {
            SessionProtocol.TELNET => new TelnetConnectionService(),
            SessionProtocol.RLOGIN => new RloginConnectionService(),
            SessionProtocol.SERIAL => new SerialConnectionService(),
            SessionProtocol.LOCAL => new LocalTerminalConnectionService(),
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
        OnPropertyChanged(nameof(UseRxvtHomeEnd));
        OnPropertyChanged(nameof(AppearanceFontFamily));
        OnPropertyChanged(nameof(AppearanceFontStyle));
        OnPropertyChanged(nameof(AppearanceFontSize));
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
            SessionProtocol.LOCAL => "LOCAL",
            SessionProtocol.SERIAL => session.SerialPortName,
            _ => $"{session.Username}@{session.Host}:{session.Port}"
        };
    }
}
