using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.Models;
using CxShell.Terminal;

namespace CxShell.Controls;

public class TerminalControl : Control
{
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;
    private Typeface _cjkTypeface;
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
    private readonly Dictionary<(bool IsCjk, bool Bold, bool Italic), Typeface> _forcedTypefaceCache = new();
    private CellPosition? _selectionAnchor;
    private CellPosition? _selectionEnd;
    private bool _isSelecting;
    private int _scrollOffset;
    private TerminalBuffer? _observedBuffer;
    private bool _lastRemoteCursorStyleSet;
    private int _lastRemoteCursorStyle;
    private int _lastScrollbackCount;
    private bool _isDraggingScrollbar;
    private double _scrollbarDragOffsetY;
    private bool _isPointerOverScrollbar;
    private string? _pointerHyperlinkUri;
    private bool _scrollLockActive;
    private TerminalMouseButton? _mouseButtonDown;
    private (int Column, int Row) _lastMouseReportCell = (-1, -1);
    private string _searchQuery = string.Empty;
    private IReadOnlyList<TerminalTextMatch> _searchMatches = [];
    private readonly Dictionary<int, List<TerminalTextMatch>> _searchMatchesByRow = new();
    private int _searchMatchIndex = -1;
    private bool _searchRefreshScheduled;
    private readonly DispatcherTimer _cursorBlinkTimer;
    private bool _cursorBlinkVisible = true;
    private readonly TerminalTextInputMethodClient _textInputMethodClient;
    private Bitmap? _backgroundImage;
    private string? _loadedBackgroundImagePath;
    private IReadOnlyList<CompiledHighlightRule> _compiledHighlightRules = [];
    private string _ghostText = string.Empty;
    private TerminalBuffer? _renderCacheBuffer;
    private TerminalCell[][] _renderRows = [];
    private bool[] _renderRowsLoaded = [];
    private bool _renderCacheActive;
    private Key _pendingTextInputKey = Key.None;
    private KeyModifiers _pendingTextInputModifiers;
    private TerminalKeyEventType _pendingTextInputEventType = TerminalKeyEventType.Press;
    private const double ScrollbarWidth = 16;
    private const double ScrollbarMinThumbHeight = 28;

    public static readonly StyledProperty<TerminalBuffer?> TerminalBufferProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalBuffer?>(nameof(TerminalBuffer));

    public static readonly StyledProperty<bool> IsSizeFixedProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(IsSizeFixed));

    public static readonly StyledProperty<int> FixedColumnsProperty =
        AvaloniaProperty.Register<TerminalControl, int>(nameof(FixedColumns), 80);

    public static readonly StyledProperty<int> FixedRowsProperty =
        AvaloniaProperty.Register<TerminalControl, int>(nameof(FixedRows), 24);

    public static readonly StyledProperty<string> KeyboardFunctionKeyModeProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(KeyboardFunctionKeyMode), "Default");

    public static readonly StyledProperty<string> KeyboardMappingFileProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(KeyboardMappingFile), string.Empty);

    public static readonly StyledProperty<string> DeleteKeySequenceProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(DeleteKeySequence), "VT220");

    public static readonly StyledProperty<string> BackspaceKeySequenceProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(BackspaceKeySequence), "Backspace");

    public static readonly StyledProperty<bool> LeftAltAsMetaProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(LeftAltAsMeta));

    public static readonly StyledProperty<bool> RightAltAsMetaProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(RightAltAsMeta));

    public static readonly StyledProperty<bool> CtrlAltAsAltGrProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(CtrlAltAsAltGr), true);

    public static readonly StyledProperty<bool> NewLineModeProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(NewLineMode));

    public static readonly StyledProperty<string> CursorKeyModeProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(CursorKeyMode), "Normal");

    public static readonly StyledProperty<string> NumericKeypadModeProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(NumericKeypadMode), "Normal");

    public static readonly StyledProperty<bool> UseApplicationCursorModeProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(UseApplicationCursorMode), true);

    public static readonly StyledProperty<bool> ShiftLimitsApplicationCursorModeProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ShiftLimitsApplicationCursorMode), true);

    public static readonly StyledProperty<bool> ScrollToBottomOnInputOutputProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ScrollToBottomOnInputOutput), true);

    public static readonly StyledProperty<bool> ScrollToBottomByKeyProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ScrollToBottomByKey));

    public static readonly StyledProperty<bool> DestructiveBackspaceProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(DestructiveBackspace));

    public static readonly StyledProperty<bool> SuspendScrollToBottomOnScrollLockProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(SuspendScrollToBottomOnScrollLock));

    public static readonly StyledProperty<bool> UseRxvtHomeEndProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(UseRxvtHomeEnd));

    public static readonly StyledProperty<string> TerminalFontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(TerminalFontFamily), "DejaVu Sans Mono");

    public static readonly StyledProperty<string> TerminalFontStyleProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(TerminalFontStyle), "Normal");

    public static readonly StyledProperty<double> TerminalFontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(TerminalFontSize), 14);

    public static readonly StyledProperty<string> TerminalCjkFontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(TerminalCjkFontFamily), "DejaVu Sans Mono");

    public static readonly StyledProperty<string> TerminalCjkFontStyleProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(TerminalCjkFontStyle), "Normal");

    public static readonly StyledProperty<double> TerminalCjkFontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(TerminalCjkFontSize), 14);

    public static readonly StyledProperty<bool> UseVariablePitchFontProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(UseVariablePitchFont));

    public static readonly StyledProperty<string> TerminalFontQualityProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(TerminalFontQuality), "Default");

    public static readonly StyledProperty<Color> CursorColorProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(CursorColor), Color.Parse("#00FF00"));

    public static readonly StyledProperty<Color> CursorTextColorProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(CursorTextColor), Colors.Black);

    public static readonly StyledProperty<string> CursorShapeProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(CursorShape), "Block");

    public static readonly StyledProperty<bool> UseBlinkingCursorProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(UseBlinkingCursor));

    public static readonly StyledProperty<int> CursorBlinkSpeedMillisecondsProperty =
        AvaloniaProperty.Register<TerminalControl, int>(nameof(CursorBlinkSpeedMilliseconds), 500);

    public static readonly StyledProperty<Thickness> TerminalPaddingProperty =
        AvaloniaProperty.Register<TerminalControl, Thickness>(nameof(TerminalPadding), new Thickness(5));

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(LineSpacing));

    public static readonly StyledProperty<double> CharacterSpacingProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(CharacterSpacing));

    public static readonly StyledProperty<string> BackgroundImagePathProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(BackgroundImagePath), string.Empty);

    public static readonly StyledProperty<string> BackgroundImagePositionProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(BackgroundImagePosition), "Center");

    public static readonly StyledProperty<IReadOnlyList<HighlightRule>?> HighlightRulesProperty =
        AvaloniaProperty.Register<TerminalControl, IReadOnlyList<HighlightRule>?>(nameof(HighlightRules));

    public TerminalBuffer? TerminalBuffer
    {
        get => GetValue(TerminalBufferProperty);
        set => SetValue(TerminalBufferProperty, value);
    }

    public bool IsSizeFixed
    {
        get => GetValue(IsSizeFixedProperty);
        set => SetValue(IsSizeFixedProperty, value);
    }

    public int FixedColumns
    {
        get => GetValue(FixedColumnsProperty);
        set => SetValue(FixedColumnsProperty, value);
    }

    public int FixedRows
    {
        get => GetValue(FixedRowsProperty);
        set => SetValue(FixedRowsProperty, value);
    }

    public string KeyboardFunctionKeyMode
    {
        get => GetValue(KeyboardFunctionKeyModeProperty);
        set => SetValue(KeyboardFunctionKeyModeProperty, value);
    }

    public string KeyboardMappingFile
    {
        get => GetValue(KeyboardMappingFileProperty);
        set => SetValue(KeyboardMappingFileProperty, value);
    }

    public string DeleteKeySequence
    {
        get => GetValue(DeleteKeySequenceProperty);
        set => SetValue(DeleteKeySequenceProperty, value);
    }

    public string BackspaceKeySequence
    {
        get => GetValue(BackspaceKeySequenceProperty);
        set => SetValue(BackspaceKeySequenceProperty, value);
    }

    public bool LeftAltAsMeta
    {
        get => GetValue(LeftAltAsMetaProperty);
        set => SetValue(LeftAltAsMetaProperty, value);
    }

    public bool RightAltAsMeta
    {
        get => GetValue(RightAltAsMetaProperty);
        set => SetValue(RightAltAsMetaProperty, value);
    }

    public bool CtrlAltAsAltGr
    {
        get => GetValue(CtrlAltAsAltGrProperty);
        set => SetValue(CtrlAltAsAltGrProperty, value);
    }

    public bool NewLineMode
    {
        get => GetValue(NewLineModeProperty);
        set => SetValue(NewLineModeProperty, value);
    }

    public string CursorKeyMode
    {
        get => GetValue(CursorKeyModeProperty);
        set => SetValue(CursorKeyModeProperty, value);
    }

    public string NumericKeypadMode
    {
        get => GetValue(NumericKeypadModeProperty);
        set => SetValue(NumericKeypadModeProperty, value);
    }

    public bool UseApplicationCursorMode
    {
        get => GetValue(UseApplicationCursorModeProperty);
        set => SetValue(UseApplicationCursorModeProperty, value);
    }

    public bool ShiftLimitsApplicationCursorMode
    {
        get => GetValue(ShiftLimitsApplicationCursorModeProperty);
        set => SetValue(ShiftLimitsApplicationCursorModeProperty, value);
    }

    public bool ScrollToBottomOnInputOutput
    {
        get => GetValue(ScrollToBottomOnInputOutputProperty);
        set => SetValue(ScrollToBottomOnInputOutputProperty, value);
    }

    public bool ScrollToBottomByKey
    {
        get => GetValue(ScrollToBottomByKeyProperty);
        set => SetValue(ScrollToBottomByKeyProperty, value);
    }

    public bool DestructiveBackspace
    {
        get => GetValue(DestructiveBackspaceProperty);
        set => SetValue(DestructiveBackspaceProperty, value);
    }

    public bool SuspendScrollToBottomOnScrollLock
    {
        get => GetValue(SuspendScrollToBottomOnScrollLockProperty);
        set => SetValue(SuspendScrollToBottomOnScrollLockProperty, value);
    }

    public bool UseRxvtHomeEnd
    {
        get => GetValue(UseRxvtHomeEndProperty);
        set => SetValue(UseRxvtHomeEndProperty, value);
    }

    public string TerminalFontFamily
    {
        get => GetValue(TerminalFontFamilyProperty);
        set => SetValue(TerminalFontFamilyProperty, value);
    }

    public string TerminalFontStyle
    {
        get => GetValue(TerminalFontStyleProperty);
        set => SetValue(TerminalFontStyleProperty, value);
    }

    public double TerminalFontSize
    {
        get => GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    public string TerminalCjkFontFamily
    {
        get => GetValue(TerminalCjkFontFamilyProperty);
        set => SetValue(TerminalCjkFontFamilyProperty, value);
    }

    public string TerminalCjkFontStyle
    {
        get => GetValue(TerminalCjkFontStyleProperty);
        set => SetValue(TerminalCjkFontStyleProperty, value);
    }

    public double TerminalCjkFontSize
    {
        get => GetValue(TerminalCjkFontSizeProperty);
        set => SetValue(TerminalCjkFontSizeProperty, value);
    }

    public bool UseVariablePitchFont
    {
        get => GetValue(UseVariablePitchFontProperty);
        set => SetValue(UseVariablePitchFontProperty, value);
    }

    public string TerminalFontQuality
    {
        get => GetValue(TerminalFontQualityProperty);
        set => SetValue(TerminalFontQualityProperty, value);
    }

    public Color CursorColor
    {
        get => GetValue(CursorColorProperty);
        set => SetValue(CursorColorProperty, value);
    }

    public Color CursorTextColor
    {
        get => GetValue(CursorTextColorProperty);
        set => SetValue(CursorTextColorProperty, value);
    }

    public string CursorShape
    {
        get => GetValue(CursorShapeProperty);
        set => SetValue(CursorShapeProperty, value);
    }

    public bool UseBlinkingCursor
    {
        get => GetValue(UseBlinkingCursorProperty);
        set => SetValue(UseBlinkingCursorProperty, value);
    }

    public int CursorBlinkSpeedMilliseconds
    {
        get => GetValue(CursorBlinkSpeedMillisecondsProperty);
        set => SetValue(CursorBlinkSpeedMillisecondsProperty, value);
    }

    public Thickness TerminalPadding
    {
        get => GetValue(TerminalPaddingProperty);
        set => SetValue(TerminalPaddingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public double CharacterSpacing
    {
        get => GetValue(CharacterSpacingProperty);
        set => SetValue(CharacterSpacingProperty, value);
    }

    public string BackgroundImagePath
    {
        get => GetValue(BackgroundImagePathProperty);
        set => SetValue(BackgroundImagePathProperty, value);
    }

    public string BackgroundImagePosition
    {
        get => GetValue(BackgroundImagePositionProperty);
        set => SetValue(BackgroundImagePositionProperty, value);
    }

    public IReadOnlyList<HighlightRule>? HighlightRules
    {
        get => GetValue(HighlightRulesProperty);
        set => SetValue(HighlightRulesProperty, value);
    }

    public event Action<string>? InputReceived;
    public event Action<byte[]>? BinaryInputReceived;
    public event Action<int, int>? SizeChanged2;
    public event Action? SearchChanged;

    /// <summary>
    /// Gives the host a chance to handle a local key before it becomes a
    /// terminal escape sequence. Returning true marks the key as handled.
    /// </summary>
    public Func<Key, bool>? LocalKeyHandler { get; set; }

    public void SetGhostText(string? text)
    {
        var normalized = text ?? string.Empty;
        if (string.Equals(_ghostText, normalized, StringComparison.Ordinal))
            return;

        _ghostText = normalized;
        InvalidateVisual();
    }

    private int _columns = 80;
    private int _rows = 24;
    private string? _loadedKeyboardMappingFile;
    private System.Collections.Generic.Dictionary<string, string>? _customKeyboardMap;

    public int Columns => _columns;
    public int Rows => _rows;
    public bool HasSelection => _selectionAnchor.HasValue
        && _selectionEnd.HasValue
        && _selectionAnchor.Value != _selectionEnd.Value;

    public bool IsMouseReportingEnabled => TerminalBuffer?.MouseTracking != TerminalMouseTracking.None;

    public TerminalControl()
    {
        Focusable = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        TextInputOptions.SetContentType(this, TextInputContentType.Normal);
        TextInputOptions.SetMultiline(this, true);
        _textInputMethodClient = new TerminalTextInputMethodClient(this);
        TextInputMethodClientRequested += OnTextInputMethodClientRequested;
        _cursorBlinkTimer = new DispatcherTimer();
        _cursorBlinkTimer.Tick += OnCursorBlinkTimerTick;
        _typeface = CreateTypeface();
        _cjkTypeface = CreateCjkTypeface();
        ApplyTextQuality();
        CalculateCellSize();
        UpdateCursorBlinkTimer();
    }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        e.Client = _textInputMethodClient;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateCursorBlinkTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cursorBlinkTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCursorBlinkTimerTick(object? sender, EventArgs e)
    {
        _cursorBlinkVisible = !_cursorBlinkVisible;
        InvalidateVisual();
    }

    private void UpdateCursorBlinkTimer()
    {
        if (!IsCursorBlinking(TerminalBuffer) || !this.IsAttachedToVisualTree())
        {
            _cursorBlinkTimer.Stop();
            _cursorBlinkVisible = true;
            InvalidateVisual();
            return;
        }

        _cursorBlinkTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(CursorBlinkSpeedMilliseconds, 1, 5000));
        _cursorBlinkVisible = true;
        _cursorBlinkTimer.Start();
        InvalidateVisual();
    }

    private void CalculateCellSize()
    {
        var primaryText = new FormattedText(
            "M",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            TerminalFontSize,
            Brushes.White);
        var cjkText = new FormattedText(
            "中",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _cjkTypeface,
            TerminalCjkFontSize,
            Brushes.White);

        _cellWidth = Math.Max(1, primaryText.Width + CharacterSpacing);
        _cellHeight = Math.Max(1, Math.Max(primaryText.Height, cjkText.Height) + LineSpacing);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TerminalBufferProperty)
        {
            if (_observedBuffer != null)
                _observedBuffer.Changed -= OnTerminalBufferChanged;

            _observedBuffer = TerminalBuffer;
            _lastScrollbackCount = _observedBuffer?.ScrollbackCount ?? 0;
            _lastRemoteCursorStyleSet = _observedBuffer?.HasRemoteCursorStyle == true;
            _lastRemoteCursorStyle = _observedBuffer?.CursorStyle ?? 0;
            if (_observedBuffer != null)
                _observedBuffer.Changed += OnTerminalBufferChanged;

            _scrollOffset = 0;
            _selectionAnchor = null;
            _selectionEnd = null;
            RefreshSearchMatches();
            if (IsSizeFixed)
                ApplyFixedSizeToBuffer(notify: false);
            else
                TerminalBuffer?.Resize(_columns, _rows);
            InvalidateVisual();
        }
        else if (change.Property == IsSizeFixedProperty ||
                 change.Property == FixedColumnsProperty ||
                 change.Property == FixedRowsProperty)
        {
            ApplyFixedSizeToBuffer(notify: true);
            InvalidateVisual();
        }
        else if (change.Property == KeyboardMappingFileProperty ||
                 change.Property == KeyboardFunctionKeyModeProperty)
        {
            _loadedKeyboardMappingFile = null;
            _customKeyboardMap = null;
        }
        else if (change.Property == TerminalFontFamilyProperty ||
                  change.Property == TerminalFontStyleProperty ||
                  change.Property == TerminalFontSizeProperty ||
                  change.Property == TerminalCjkFontFamilyProperty ||
                  change.Property == TerminalCjkFontStyleProperty ||
                  change.Property == TerminalCjkFontSizeProperty ||
                  change.Property == UseVariablePitchFontProperty ||
                  change.Property == LineSpacingProperty ||
                  change.Property == CharacterSpacingProperty)
        {
            _typeface = CreateTypeface();
            _cjkTypeface = CreateCjkTypeface();
            _forcedTypefaceCache.Clear();
            CalculateCellSize();
            UpdateTerminalSize(Bounds.Size, notify: true);
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == TerminalFontQualityProperty)
        {
            ApplyTextQuality();
            InvalidateVisual();
        }
        else if (change.Property == TerminalPaddingProperty)
        {
            UpdateTerminalSize(Bounds.Size, notify: true);
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == BackgroundImagePathProperty)
        {
            LoadBackgroundImage();
            InvalidateVisual();
        }
        else if (change.Property == BackgroundImagePositionProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == HighlightRulesProperty)
        {
            CompileHighlightRules();
            InvalidateVisual();
        }
        else if (change.Property == UseBlinkingCursorProperty ||
                 change.Property == CursorBlinkSpeedMillisecondsProperty)
        {
            UpdateCursorBlinkTimer();
        }
        else if (change.Property == CursorColorProperty ||
                 change.Property == CursorTextColorProperty ||
                 change.Property == CursorShapeProperty)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? _columns * _cellWidth
            : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height)
            ? _rows * _cellHeight
            : availableSize.Height;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateTerminalSize(finalSize, notify: true);

        return finalSize;
    }

    public void SyncSizeToBounds()
    {
        UpdateTerminalSize(Bounds.Size, notify: true);
    }

    private void UpdateTerminalSize(Size size, bool notify)
    {
        if (IsSizeFixed)
        {
            ApplyFixedSizeToBuffer(notify);
            return;
        }

        if (_cellWidth <= 0
            || _cellHeight <= 0
            || double.IsNaN(size.Width)
            || double.IsNaN(size.Height)
            || double.IsInfinity(size.Width)
            || double.IsInfinity(size.Height)
            || size.Width <= 0
            || size.Height <= 0)
        {
            return;
        }

        var content = GetContentRect(size);
        var usableWidth = Math.Max(0, content.Width - ScrollbarWidth);
        var usableHeight = Math.Max(0, content.Height);
        int newCols = Math.Max(20, (int)Math.Floor(usableWidth / _cellWidth));
        int newRows = Math.Max(5, (int)Math.Floor(usableHeight / _cellHeight));

        var changed = newCols != _columns || newRows != _rows;
        _columns = newCols;
        _rows = newRows;
        TerminalBuffer?.Resize(_columns, _rows);
        ClampScrollOffset();

        if (notify && changed)
            SizeChanged2?.Invoke(_columns, _rows);
    }

    private void ApplyFixedSizeToBuffer(bool notify)
    {
        var newCols = Math.Clamp(FixedColumns, 20, 500);
        var newRows = Math.Clamp(FixedRows, 5, 200);
        var changed = newCols != _columns || newRows != _rows;
        _columns = newCols;
        _rows = newRows;
        TerminalBuffer?.Resize(_columns, _rows);
        ClampScrollOffset();

        if (notify && changed)
            SizeChanged2?.Invoke(_columns, _rows);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var buffer = TerminalBuffer;
        if (buffer == null)
        {
            context.FillRectangle(
                GetBrush(TerminalColors.DefaultBackground),
                new Rect(Bounds.Size));
            DrawBackgroundImage(context, new Rect(Bounds.Size));
            return;
        }

        // Draw background
        context.FillRectangle(
            GetBrush(buffer.DefaultBackgroundColor),
            new Rect(Bounds.Size));
        DrawBackgroundImage(context, GetContentRect(Bounds.Size));

        var contentRect = GetContentRect(Bounds.Size);
        BeginRenderCache(buffer);
        try
        {
            using (context.PushClip(contentRect))
            using (context.PushTransform(Matrix.CreateTranslation(contentRect.X, contentRect.Y)))
            {
                for (int row = 0; row < buffer.Rows; row++)
                {
                    double y = row * _cellHeight;
                    var highlightMap = BuildHighlightMap(buffer, row);

                    // Draw background segments
                    int segStart = 0;
                    var segColor = ResolveHighlightBackground(buffer, GetViewportCell(buffer, row, 0), highlightMap?[0]);

                    for (int col = 0; col <= buffer.Columns; col++)
                    {
                        var cellColor = col < buffer.Columns
                            ? ResolveHighlightBackground(buffer, GetViewportCell(buffer, row, col), highlightMap?[col])
                            : ResolveBackground(buffer, buffer.CreateDefaultCell());

                        if (cellColor != segColor || col == buffer.Columns)
                        {
                            if (segColor != buffer.DefaultBackgroundColor)
                            {
                                context.FillRectangle(
                                    GetBrush(segColor),
                                    new Rect(segStart * _cellWidth, y, (col - segStart) * _cellWidth, _cellHeight));
                            }
                            segStart = col;
                            segColor = cellColor;
                        }
                    }

                    DrawSearchMatches(context, row, y, buffer);
                    DrawSelection(context, row, y);

                    // Merge adjacent cells with the same style. Wide and combining
                    // characters remain individual runs so their grid positions stay exact.
                    DrawTextRuns(context, buffer, row, y, highlightMap);

                    // Draw underline for cells that have it
                    for (int col = 0; col < buffer.Columns; col++)
                    {
                        var cell = GetViewportCell(buffer, row, col);
                        var highlight = highlightMap?[col];
                        if (!cell.Invisible &&
                            (cell.HyperlinkUri != null || cell.Underline || cell.DoubleUnderline || cell.Strikethrough || highlight?.Underline == true || highlight?.Strikethrough == true) &&
                            cell.GetText() != " " && !cell.IsWideContinuation)
                        {
                            var width = col + 1 < buffer.Columns && GetViewportCell(buffer, row, col + 1).IsWideContinuation
                                ? 2
                                : 1;
                            var pen = new Pen(GetBrush(ResolveHighlightForeground(buffer, cell, highlight)), 1);
                            if (cell.HyperlinkUri != null || cell.Underline || highlight?.Underline == true)
                            {
                                context.DrawLine(pen,
                                    new Point(col * _cellWidth, y + _cellHeight - 1),
                                    new Point((col + width) * _cellWidth, y + _cellHeight - 1));
                            }

                            if (cell.DoubleUnderline)
                            {
                                context.DrawLine(pen,
                                    new Point(col * _cellWidth, y + Math.Max(1, _cellHeight - 3)),
                                    new Point((col + width) * _cellWidth, y + Math.Max(1, _cellHeight - 3)));
                                context.DrawLine(pen,
                                    new Point(col * _cellWidth, y + _cellHeight - 1),
                                    new Point((col + width) * _cellWidth, y + _cellHeight - 1));
                            }

                            if (cell.Strikethrough || highlight?.Strikethrough == true)
                            {
                                context.DrawLine(pen,
                                    new Point(col * _cellWidth, y + _cellHeight * 0.55),
                                    new Point((col + width) * _cellWidth, y + _cellHeight * 0.55));
                            }
                        }
                    }

                }

                // Draw cursor
                if (buffer.CursorVisible && _scrollOffset == 0 &&
                    (!IsCursorBlinking(buffer) || _cursorBlinkVisible))
                {
                    var cursorCol = buffer.CursorCol;
                    var cursorCell = buffer.GetCell(buffer.CursorRow, cursorCol);
                    var cursorWidth = !cursorCell.IsWideContinuation
                        && cursorCol + 1 < buffer.Columns
                        && buffer.GetCell(buffer.CursorRow, cursorCol + 1).IsWideContinuation
                        ? _cellWidth * 2
                        : _cellWidth;
                    double cursorX = cursorCol * _cellWidth;
                    double cursorY = buffer.CursorRow * _cellHeight;
                    DrawCursor(context, cursorX, cursorY, cursorWidth, GetCursorShape(buffer));
                    // Re-draw the character under cursor in black (inverted) so it remains visible
                    if (!cursorCell.Invisible && !cursorCell.IsWideContinuation && cursorCell.GetText() != " ")
                    {
                        DrawTextRun(context, cursorCell.GetText(),
                            cursorX, cursorY,
                            CursorTextColor, cursorCell.Bold, cursorCell.Italic);
                    }
                }

                if (!string.IsNullOrEmpty(_ghostText) &&
                    _scrollOffset == 0 &&
                    buffer.CursorRow >= 0 && buffer.CursorRow < buffer.Rows &&
                    buffer.CursorCol >= 0 && buffer.CursorCol < buffer.Columns)
                {
                    var ghostLength = Math.Min(_ghostText.Length, buffer.Columns - buffer.CursorCol);
                    if (ghostLength > 0)
                    {
                        DrawTextRun(
                            context,
                            _ghostText[..ghostLength],
                            buffer.CursorCol * _cellWidth,
                            buffer.CursorRow * _cellHeight,
                            Color.FromArgb(150, CursorColor.R, CursorColor.G, CursorColor.B),
                            bold: false);
                    }
                }
            }
        }
        finally
        {
            EndRenderCache();
        }

        DrawScrollbar(context, buffer);

        // Clear dirty rows
        buffer.DirtyRows.Clear();
    }

    private void DrawScrollbar(DrawingContext context, TerminalBuffer buffer)
    {
        if (!ShouldShowScrollbar(buffer))
            return;

        var track = GetScrollbarTrackRect();
        var thumb = GetScrollbarThumbRect(buffer, track);
        context.FillRectangle(
            GetBrush(Color.FromArgb(90, 80, 86, 96)),
            track);
        context.FillRectangle(
            GetBrush(_isDraggingScrollbar
                ? Color.FromArgb(230, 150, 170, 205)
                : Color.FromArgb(190, 120, 140, 175)),
            thumb);
    }

    private void LoadBackgroundImage()
    {
        var path = BackgroundImagePath?.Trim();
        if (string.Equals(path, _loadedBackgroundImagePath, StringComparison.Ordinal))
            return;

        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _loadedBackgroundImagePath = path;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            _backgroundImage = new Bitmap(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading terminal background image: {ex.Message}");
        }
    }

    private void DrawBackgroundImage(DrawingContext context, Rect target)
    {
        if (_backgroundImage == null)
            LoadBackgroundImage();

        var image = _backgroundImage;
        if (image == null || target.Width <= 0 || target.Height <= 0)
            return;

        var source = new Rect(image.Size);
        var mode = BackgroundImagePosition?.Trim();
        if (string.Equals(mode, "Stretch", StringComparison.OrdinalIgnoreCase))
        {
            context.DrawImage(image, source, target);
            return;
        }

        if (string.Equals(mode, "Tile", StringComparison.OrdinalIgnoreCase))
        {
            for (double y = target.Y; y < target.Bottom; y += image.Size.Height)
            {
                for (double x = target.X; x < target.Right; x += image.Size.Width)
                {
                    var width = Math.Min(image.Size.Width, target.Right - x);
                    var height = Math.Min(image.Size.Height, target.Bottom - y);
                    if (width <= 0 || height <= 0)
                        continue;

                    context.DrawImage(image, new Rect(0, 0, width, height), new Rect(x, y, width, height));
                }
            }
            return;
        }

        var xPos = target.X + (target.Width - image.Size.Width) / 2;
        var yPos = target.Y + (target.Height - image.Size.Height) / 2;
        if (string.Equals(mode, "TopLeft", StringComparison.OrdinalIgnoreCase))
        {
            xPos = target.X;
            yPos = target.Y;
        }
        else if (string.Equals(mode, "TopRight", StringComparison.OrdinalIgnoreCase))
        {
            xPos = target.Right - image.Size.Width;
            yPos = target.Y;
        }
        else if (string.Equals(mode, "BottomLeft", StringComparison.OrdinalIgnoreCase))
        {
            xPos = target.X;
            yPos = target.Bottom - image.Size.Height;
        }
        else if (string.Equals(mode, "BottomRight", StringComparison.OrdinalIgnoreCase))
        {
            xPos = target.Right - image.Size.Width;
            yPos = target.Bottom - image.Size.Height;
        }

        context.DrawImage(image, source, new Rect(xPos, yPos, image.Size.Width, image.Size.Height));
    }

    private void OnTerminalBufferChanged()
    {
        var buffer = TerminalBuffer;
        if (buffer != null &&
            (buffer.HasRemoteCursorStyle != _lastRemoteCursorStyleSet ||
             buffer.CursorStyle != _lastRemoteCursorStyle))
        {
            _lastRemoteCursorStyleSet = buffer.HasRemoteCursorStyle;
            _lastRemoteCursorStyle = buffer.CursorStyle;
            UpdateCursorBlinkTimer();
        }

        InvalidateVisual();

        var count = buffer?.ScrollbackCount ?? 0;
        var delta = count - _lastScrollbackCount;
        if (_scrollOffset > 0 && delta > 0)
        {
            if (ScrollToBottomOnInputOutput && !(SuspendScrollToBottomOnScrollLock && _scrollLockActive))
            {
                _scrollOffset = 0;
            }
            else
            {
                _scrollOffset += delta;
                ClampScrollOffset();
            }
        }

        _lastScrollbackCount = count;
        ScheduleSearchMatchesRefresh();
    }

    /// <summary>
    /// Lets the owner notify the control when output was parsed as a batched
    /// operation without raising a buffer event for every written character.
    /// </summary>
    public void NotifyBufferChanged() => OnTerminalBufferChanged();

    private void ScheduleSearchMatchesRefresh()
    {
        if (string.IsNullOrEmpty(_searchQuery) || _searchRefreshScheduled)
            return;

        _searchRefreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _searchRefreshScheduled = false;
            if (!string.IsNullOrEmpty(_searchQuery))
                RefreshSearchMatches();
        }, DispatcherPriority.Background);
    }

    private void DrawSearchMatches(DrawingContext context, int row, double y, TerminalBuffer buffer)
    {
        if (_searchMatchesByRow.Count == 0)
            return;

        var combinedRow = buffer.ScrollbackCount - _scrollOffset + row;
        if (!_searchMatchesByRow.TryGetValue(combinedRow, out var matches))
            return;

        foreach (var match in matches)
        {
            var start = Math.Clamp(match.Column, 0, buffer.Columns);
            var end = Math.Clamp(match.Column + match.Length, start, buffer.Columns);
            if (end <= start)
                continue;

            var isCurrent = _searchMatchIndex >= 0 &&
                            _searchMatchIndex < _searchMatches.Count &&
                            _searchMatches[_searchMatchIndex].Equals(match);
            var color = isCurrent ? Color.Parse("#B8860B") : Color.Parse("#8069A7FF");
            context.FillRectangle(
                GetBrush(color),
                new Rect(start * _cellWidth, y, (end - start) * _cellWidth, _cellHeight));
        }
    }

    private void DrawSelection(DrawingContext context, int row, double y)
    {
        if (!TryGetOrderedSelection(out var start, out var end))
            return;

        var buffer = TerminalBuffer;
        if (buffer == null || row < start.Row || row > end.Row)
            return;

        int startCol = row == start.Row ? start.Column : 0;
        int endColExclusive = row == end.Row ? end.Column : buffer.Columns;
        if (endColExclusive <= startCol)
            return;

        context.FillRectangle(
            GetBrush(Color.Parse("#8069A7FF")),
            new Rect(startCol * _cellWidth, y, (endColExclusive - startCol) * _cellWidth, _cellHeight));
    }

    private TerminalCell GetViewportCell(TerminalBuffer buffer, int row, int col)
    {
        if (_renderCacheActive && ReferenceEquals(_renderCacheBuffer, buffer) &&
            row >= 0 && row < _renderRows.Length && col >= 0 && col < buffer.Columns)
        {
            if (!_renderRowsLoaded[row])
            {
                buffer.CopyViewportRow(row, _scrollOffset, _renderRows[row]);
                _renderRowsLoaded[row] = true;
            }

            return _renderRows[row][col];
        }

        return buffer.GetViewportCell(row, col, _scrollOffset);
    }

    private void BeginRenderCache(TerminalBuffer buffer)
    {
        _renderCacheBuffer = buffer;
        _renderCacheActive = true;
        if (_renderRows.Length != buffer.Rows)
            _renderRows = new TerminalCell[buffer.Rows][];

        if (_renderRowsLoaded.Length != buffer.Rows)
            _renderRowsLoaded = new bool[buffer.Rows];
        else
            Array.Clear(_renderRowsLoaded, 0, _renderRowsLoaded.Length);

        for (var row = 0; row < buffer.Rows; row++)
        {
            if (_renderRows[row] == null || _renderRows[row].Length != buffer.Columns)
                _renderRows[row] = new TerminalCell[buffer.Columns];
        }
    }

    private void EndRenderCache()
    {
        _renderCacheActive = false;
        _renderCacheBuffer = null;
    }

    private static Color ResolveForeground(TerminalBuffer buffer, TerminalCell cell)
    {
        return buffer.ReverseVideoMode ^ cell.Reverse ? cell.Background : cell.Foreground;
    }

    private static Color ResolveBackground(TerminalBuffer buffer, TerminalCell cell)
    {
        return buffer.ReverseVideoMode ^ cell.Reverse ? cell.Foreground : cell.Background;
    }

    private static Color ResolveHighlightForeground(TerminalBuffer buffer, TerminalCell cell, CompiledHighlightRule? highlight)
    {
        var color = highlight == null || highlight.UseTerminalColor
            ? ResolveForeground(buffer, cell)
            : highlight.ForegroundColor;

        return cell.Dim ? DimColor(color) : color;
    }

    private static Color DimColor(Color color)
    {
        const double factor = 0.6;
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));
    }

    private static Color ResolveHighlightBackground(TerminalBuffer buffer, TerminalCell cell, CompiledHighlightRule? highlight)
    {
        if (highlight == null || highlight.UseTerminalColor)
            return ResolveBackground(buffer, cell);

        return highlight.BackgroundColor;
    }

    private void DrawTextRuns(
        DrawingContext context,
        TerminalBuffer buffer,
        int row,
        double y,
        CompiledHighlightRule?[]? highlightMap)
    {
        for (var col = 0; col < buffer.Columns;)
        {
            var cell = GetViewportCell(buffer, row, col);
            if (cell.IsWideContinuation)
            {
                col++;
                continue;
            }

            var highlight = highlightMap?[col];
            var color = ResolveHighlightForeground(buffer, cell, highlight);
            var bold = cell.Bold || highlight?.Bold == true;
            var italic = cell.Italic || highlight?.Italic == true;
            var cellWidth = col + 1 < buffer.Columns && GetViewportCell(buffer, row, col + 1).IsWideContinuation ? 2 : 1;
            var text = cell.GetText();

            if (cell.Invisible)
            {
                col += cellWidth;
                continue;
            }

            // Supplementary-plane/combining text and wide cells need their own
            // origin because their rendered width is not one terminal column.
            if (cellWidth != 1 || text.Length != 1)
            {
                DrawTextRun(context, text, col * _cellWidth, y, color, bold, italic);
                col += cellWidth;
                continue;
            }

            var start = col;
            var run = new StringBuilder();
            while (col < buffer.Columns)
            {
                var candidate = GetViewportCell(buffer, row, col);
                if (candidate.IsWideContinuation || candidate.Invisible)
                    break;

                var candidateHighlight = highlightMap?[col];
                var candidateText = candidate.GetText();
                var candidateWidth = col + 1 < buffer.Columns && GetViewportCell(buffer, row, col + 1).IsWideContinuation ? 2 : 1;
                var candidateColor = ResolveHighlightForeground(buffer, candidate, candidateHighlight);
                var candidateBold = candidate.Bold || candidateHighlight?.Bold == true;
                var candidateItalic = candidate.Italic || candidateHighlight?.Italic == true;
                if (candidateWidth != 1 || candidateText.Length != 1 ||
                    candidateColor != color || candidateBold != bold || candidateItalic != italic)
                    break;

                run.Append(candidateText);
                col++;
            }

            if (run.Length > 0)
                DrawTextRun(context, run.ToString(), start * _cellWidth, y, color, bold, italic);
            else
                col = Math.Min(buffer.Columns, start + 1);
        }
    }

    private CompiledHighlightRule?[]? BuildHighlightMap(TerminalBuffer buffer, int row)
    {
        if (_compiledHighlightRules.Count == 0)
            return null;

        var text = new StringBuilder(buffer.Columns);
        var columns = new List<int>();
        var columnEnds = new List<int>();
        for (var col = 0; col < buffer.Columns; col++)
        {
            var cell = GetViewportCell(buffer, row, col);
            if (cell.IsWideContinuation)
                continue;

            var cellText = cell.GetText();
            var width = col + 1 < buffer.Columns && GetViewportCell(buffer, row, col + 1).IsWideContinuation ? 2 : 1;
            foreach (var character in cellText)
            {
                text.Append(character);
                columns.Add(col);
                columnEnds.Add(col + width);
            }
        }

        CompiledHighlightRule?[]? map = null;
        foreach (var rule in _compiledHighlightRules)
        {
            try
            {
                foreach (Match match in rule.Regex.Matches(text.ToString()))
                {
                    if (!match.Success || match.Length <= 0)
                        continue;

                    map ??= new CompiledHighlightRule?[buffer.Columns];
                    if (match.Index >= columns.Count)
                        continue;

                    var start = Math.Clamp(columns[match.Index], 0, buffer.Columns);
                    var endOffset = Math.Min(match.Index + match.Length - 1, columnEnds.Count - 1);
                    var end = Math.Clamp(columnEnds[endOffset], start, buffer.Columns);
                    for (var col = start; col < end; col++)
                        map[col] = rule;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return map;
    }

    private void CompileHighlightRules()
    {
        var compiled = new List<CompiledHighlightRule>();
        foreach (var rule in HighlightRules ?? [])
        {
            if (!rule.IsEnabled || string.IsNullOrWhiteSpace(rule.Keyword))
                continue;

            var pattern = rule.IsRegex ? rule.Keyword : Regex.Escape(rule.Keyword);
            var options = RegexOptions.CultureInvariant;
            if (!rule.IsCaseSensitive)
                options |= RegexOptions.IgnoreCase;

            try
            {
                compiled.Add(new CompiledHighlightRule(
                    new Regex(pattern, options, TimeSpan.FromMilliseconds(50)),
                    ParseColorOrDefault(rule.ForegroundColor, Colors.Black),
                    ParseColorOrDefault(rule.BackgroundColor, Color.Parse("#FFFF40")),
                    rule.UseTerminalColor,
                    rule.Bold,
                    rule.Italic,
                    rule.Underline,
                    rule.Strikethrough));
            }
            catch (ArgumentException)
            {
            }
        }

        _compiledHighlightRules = compiled;
    }

    private static Color ParseColorOrDefault(string? value, Color fallback)
    {
        return Color.TryParse(value, out var color) ? color : fallback;
    }

    private void DrawTextRun(DrawingContext context, string text, double x, double y,
        Color color, bool bold, bool italic = false)
    {
        // Skip whitespace-only runs for performance
        bool allSpaces = true;
        foreach (var c in text)
        {
            if (c != ' ') { allSpaces = false; break; }
        }
        if (allSpaces) return;

        var useCjk = ContainsCjk(text);
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetTextTypeface(useCjk, bold, italic),
            useCjk ? TerminalCjkFontSize : TerminalFontSize,
            GetBrush(color));

        context.DrawText(ft, new Point(x, y));
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text != null)
        {
            MaybeScrollToBottomForInput();
            if (TerminalKeyboardEncoder.TryEncodeTextInput(
                    e.Text,
                    _pendingTextInputKey,
                    _pendingTextInputModifiers,
                    TerminalBuffer?.KittyKeyboardFlags ?? 0,
                    _pendingTextInputEventType,
                    out var kittyTextData))
            {
                InputReceived?.Invoke(kittyTextData);
            }
            else
            {
                InputReceived?.Invoke(e.Text);
            }

            _pendingTextInputKey = Key.None;
            _pendingTextInputModifiers = KeyModifiers.None;
            _pendingTextInputEventType = TerminalKeyEventType.Press;
            e.Handled = true;
        }
    }

    private Typeface CreateTypeface(bool forceBold = false, bool forceItalic = false)
    {
        var styleText = TerminalFontStyle ?? "Normal";
        var fontStyle = forceItalic || styleText.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            ? FontStyle.Italic
            : FontStyle.Normal;
        var fontWeight = forceBold || styleText.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            ? FontWeight.Bold
            : FontWeight.Normal;
        var family = string.IsNullOrWhiteSpace(TerminalFontFamily)
            ? "DejaVu Sans Mono"
            : TerminalFontFamily;
        var fallback = UseVariablePitchFont
            ? family
            : $"{family}, Cascadia Mono, Consolas, Courier New, monospace";
        return new Typeface(fallback, fontStyle, fontWeight);
    }

    private Typeface CreateCjkTypeface(bool forceBold = false, bool forceItalic = false)
    {
        var styleText = TerminalCjkFontStyle ?? "Normal";
        var fontStyle = forceItalic || styleText.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            ? FontStyle.Italic
            : FontStyle.Normal;
        var fontWeight = forceBold || styleText.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            ? FontWeight.Bold
            : FontWeight.Normal;
        var family = string.IsNullOrWhiteSpace(TerminalCjkFontFamily)
            ? TerminalFontFamily
            : TerminalCjkFontFamily;
        var fallback = UseVariablePitchFont
            ? family
            : $"{family}, Microsoft YaHei UI, SimSun, Noto Sans CJK SC, Cascadia Mono, Consolas, monospace";
        return new Typeface(fallback, fontStyle, fontWeight);
    }

    private Typeface GetTextTypeface(bool useCjk, bool bold, bool italic)
    {
        if (!bold && !italic)
            return useCjk ? _cjkTypeface : _typeface;

        var key = (useCjk, bold, italic);
        if (_forcedTypefaceCache.TryGetValue(key, out var typeface))
            return typeface;

        typeface = useCjk
            ? CreateCjkTypeface(forceBold: bold, forceItalic: italic)
            : CreateTypeface(forceBold: bold, forceItalic: italic);
        _forcedTypefaceCache[key] = typeface;
        return typeface;
    }

    private SolidColorBrush GetBrush(Color color)
    {
        if (_brushCache.TryGetValue(color, out var brush))
            return brush;

        brush = new SolidColorBrush(color);
        _brushCache[color] = brush;
        return brush;
    }

    private void ApplyTextQuality()
    {
        TextOptions.SetTextRenderingMode(this, GetTextRenderingMode(TerminalFontQuality));
        TextOptions.SetTextHintingMode(this, GetTextHintingMode(TerminalFontQuality));
        TextOptions.SetBaselinePixelAlignment(this, GetBaselinePixelAlignment(TerminalFontQuality));
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjkCharacter(rune.Value))
                return true;
        }

        return false;
    }

    private static bool IsCjkCharacter(int code)
    {
        return code >= 0x2E80 && code <= 0xA4CF
               || code >= 0xAC00 && code <= 0xD7A3
               || code >= 0xF900 && code <= 0xFAFF
               || code >= 0xFE10 && code <= 0xFE6F
               || code >= 0xFF00 && code <= 0xFFEF
               || code >= 0x1F300 && code <= 0x1FAFF;
    }

    private static TextRenderingMode GetTextRenderingMode(string? value)
    {
        return value switch
        {
            "NonAntiAliased" => TextRenderingMode.Alias,
            "AntiAliased" => TextRenderingMode.Antialias,
            "ClearType" => TextRenderingMode.SubpixelAntialias,
            "NaturalClearType" => TextRenderingMode.SubpixelAntialias,
            _ => TextRenderingMode.Unspecified
        };
    }

    private static TextHintingMode GetTextHintingMode(string? value)
    {
        return value switch
        {
            "Draft" => TextHintingMode.None,
            "Proof" => TextHintingMode.Strong,
            "NonAntiAliased" => TextHintingMode.None,
            "AntiAliased" => TextHintingMode.Light,
            "ClearType" => TextHintingMode.Strong,
            "NaturalClearType" => TextHintingMode.Strong,
            _ => TextHintingMode.Unspecified
        };
    }

    private static BaselinePixelAlignment GetBaselinePixelAlignment(string? value)
    {
        return value switch
        {
            "NonAntiAliased" => BaselinePixelAlignment.Aligned,
            "AntiAliased" => BaselinePixelAlignment.Aligned,
            "ClearType" => BaselinePixelAlignment.Aligned,
            "NaturalClearType" => BaselinePixelAlignment.Aligned,
            _ => BaselinePixelAlignment.Unspecified
        };
    }

    private sealed record CompiledHighlightRule(
        Regex Regex,
        Color ForegroundColor,
        Color BackgroundColor,
        bool UseTerminalColor,
        bool Bold,
        bool Italic,
        bool Underline,
        bool Strikethrough);

    private bool IsCursorBlinking(TerminalBuffer? buffer)
    {
        if (buffer == null)
            return UseBlinkingCursor;

        if (!buffer.HasRemoteCursorStyle)
            return UseBlinkingCursor;

        return buffer.CursorStyle is 0 or 1 or 3 or 5;
    }

    private string GetCursorShape(TerminalBuffer buffer)
    {
        if (!buffer.HasRemoteCursorStyle)
            return CursorShape;

        return buffer.CursorStyle switch
        {
            3 or 4 => "Underline",
            5 or 6 => "Vertical",
            _ => "Block"
        };
    }

    private void DrawCursor(DrawingContext context, double x, double y, double width, string shape)
    {
        var brush = GetBrush(CursorColor);
        shape = shape.Trim();
        if (string.Equals(shape, "Vertical", StringComparison.OrdinalIgnoreCase))
        {
            context.FillRectangle(brush, new Rect(x, y, Math.Max(2, width * 0.18), _cellHeight));
        }
        else if (string.Equals(shape, "Underline", StringComparison.OrdinalIgnoreCase))
        {
            context.FillRectangle(brush, new Rect(x, y + _cellHeight - 3, width, 3));
        }
        else
        {
            context.FillRectangle(brush, new Rect(x, y, width, _cellHeight));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var buffer = TerminalBuffer;
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (buffer != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsPointInScrollbar(point, buffer))
        {
            var track = GetScrollbarTrackRect();
            var thumb = GetScrollbarThumbRect(buffer, track);
            _isDraggingScrollbar = true;
            _scrollbarDragOffsetY = thumb.Contains(point) ? point.Y - thumb.Y : thumb.Height / 2;
            UpdateScrollOffsetFromScrollbar(point.Y, buffer);
            _selectionAnchor = null;
            _selectionEnd = null;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed &&
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) &&
            TryGetHyperlinkAtPoint(point) is { } hyperlinkUri &&
            TryOpenHyperlink(hyperlinkUri))
        {
            e.Handled = true;
            return;
        }

        // A terminal application that enabled mouse tracking owns the pointer
        // on the live screen. Shift keeps the local selection/context menu path.
        if (buffer != null
            && buffer.MouseTracking != TerminalMouseTracking.None
            && _scrollOffset == 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && TryGetPressedMouseButton(properties) is { } mouseButton
            && SendMouseReport(TerminalMouseEventType.Press, mouseButton, point, e.KeyModifiers))
        {
            _mouseButtonDown = mouseButton;
            _lastMouseReportCell = GetMouseCell(point);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
            return;

        var position = GetCellPosition(e.GetPosition(this), includeCell: true);
        _selectionAnchor = position;
        _selectionEnd = position;
        _isSelecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdatePointerCursor(e.GetPosition(this));

        var buffer = TerminalBuffer;
        if (buffer != null
            && _scrollOffset == 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && buffer.MouseTracking is TerminalMouseTracking.ButtonEvent or TerminalMouseTracking.AnyEvent)
        {
            var mouseCell = GetMouseCell(e.GetPosition(this));
            var hasButton = _mouseButtonDown.HasValue;
            if ((buffer.MouseTracking == TerminalMouseTracking.AnyEvent || hasButton)
                && mouseCell != _lastMouseReportCell)
            {
                var mouseButton = _mouseButtonDown ?? TerminalMouseButton.None;
                if (SendMouseReport(TerminalMouseEventType.Move, mouseButton, e.GetPosition(this), e.KeyModifiers))
                    _lastMouseReportCell = mouseCell;

                e.Handled = true;
                return;
            }

            if (hasButton)
            {
                e.Handled = true;
                return;
            }
        }

        if (_isDraggingScrollbar)
        {
            var scrollBuffer = TerminalBuffer;
            if (scrollBuffer != null)
                UpdateScrollOffsetFromScrollbar(e.GetPosition(this).Y, scrollBuffer);

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_isSelecting || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _selectionEnd = GetCellPosition(e.GetPosition(this), includeCell: true);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOverScrollbar = false;
        _pointerHyperlinkUri = null;
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mouseButtonDown is { } mouseButton)
        {
            SendMouseReport(TerminalMouseEventType.Release, mouseButton, e.GetPosition(this), e.KeyModifiers);
            _mouseButtonDown = null;
            _lastMouseReportCell = (-1, -1);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isDraggingScrollbar)
        {
            _isDraggingScrollbar = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_isSelecting || e.InitialPressMouseButton != MouseButton.Left)
            return;

        _selectionEnd = GetCellPosition(e.GetPosition(this), includeCell: true);
        _isSelecting = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var buffer = TerminalBuffer;
        if (buffer != null
            && buffer.MouseTracking != TerminalMouseTracking.None
            && _scrollOffset == 0
            && e.Delta.Y != 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && SendMouseReport(
                TerminalMouseEventType.Press,
                e.Delta.Y > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown,
                e.GetPosition(this),
                e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (buffer == null || GetMaxScrollOffset(buffer) == 0)
            return;

        var rows = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * 3));
        var oldOffset = _scrollOffset;
        if (e.Delta.Y > 0)
            _scrollOffset += rows;
        else if (e.Delta.Y < 0)
            _scrollOffset -= rows;

        ClampScrollOffset();
        if (_scrollOffset == oldOffset)
            return;

        _selectionAnchor = null;
        _selectionEnd = null;
        InvalidateVisual();
        e.Handled = true;
    }

    public async Task CopySelectionAsync()
    {
        try
        {
            var text = GetSelectedText();
            if (string.IsNullOrEmpty(text) || !this.IsAttachedToVisualTree())
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error copying terminal selection: {ex.Message}");
        }
    }

    public string GetTextForExport()
    {
        return HasSelection
            ? GetSelectedText()
            : TerminalBuffer?.ExportText() ?? string.Empty;
    }

    public int SearchMatchCount => _searchMatches.Count;

    public int SearchMatchIndex => _searchMatchIndex;

    public void SetSearchQuery(string? query)
    {
        var normalized = query ?? string.Empty;
        if (string.Equals(_searchQuery, normalized, StringComparison.Ordinal))
            return;

        _searchQuery = normalized;
        _searchMatchIndex = -1;
        RefreshSearchMatches();
    }

    public bool MoveToSearchMatch(bool backwards)
    {
        if (_searchMatches.Count == 0 || TerminalBuffer == null)
            return false;

        _searchMatchIndex = _searchMatchIndex < 0
            ? backwards ? _searchMatches.Count - 1 : 0
            : (_searchMatchIndex + (backwards ? -1 : 1) + _searchMatches.Count) % _searchMatches.Count;

        var match = _searchMatches[_searchMatchIndex];
        var buffer = TerminalBuffer;
        var targetRow = match.Row < buffer.ScrollbackCount
            ? Math.Max(0, match.Row - Math.Max(1, _rows / 2))
            : match.Row;

        _scrollOffset = targetRow < buffer.ScrollbackCount
            ? buffer.ScrollbackCount - targetRow
            : 0;
        ClampScrollOffset();
        _selectionAnchor = null;
        _selectionEnd = null;
        SearchChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    public void ClearSearch()
    {
        if (string.IsNullOrEmpty(_searchQuery) && _searchMatches.Count == 0)
            return;

        _searchQuery = string.Empty;
        _searchMatchIndex = -1;
        RefreshSearchMatches();
    }

    private void RefreshSearchMatches()
    {
        _searchMatches = TerminalBuffer?.FindTextMatches(_searchQuery) ?? [];
        _searchMatchesByRow.Clear();
        for (var index = 0; index < _searchMatches.Count; index++)
        {
            var match = _searchMatches[index];
            if (!_searchMatchesByRow.TryGetValue(match.Row, out var rowMatches))
            {
                rowMatches = new List<TerminalTextMatch>();
                _searchMatchesByRow[match.Row] = rowMatches;
            }

            rowMatches.Add(match);
        }

        if (_searchMatchIndex >= _searchMatches.Count)
            _searchMatchIndex = -1;

        SearchChanged?.Invoke();
        InvalidateVisual();
    }

    public async Task PasteAsync()
    {
        try
        {
            if (!this.IsAttachedToVisualTree())
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                ScrollToBottom();
                InputReceived?.Invoke(BuildPastePayload(text, TerminalBuffer?.BracketedPasteMode == true));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error pasting into terminal: {ex.Message}");
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateCursorBlinkTimer();
        if (TerminalBuffer?.FocusReportingMode == true)
            InputReceived?.Invoke("\x1b[I");
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdateCursorBlinkTimer();
        if (TerminalBuffer?.FocusReportingMode == true)
            InputReceived?.Invoke("\x1b[O");
    }

    internal static string BuildPastePayload(string text, bool bracketedPasteMode)
    {
        var normalizedText = text.Replace("\r\n", "\r").Replace("\n", "\r");
        return bracketedPasteMode
            ? "\x1b[200~" + normalizedText + "\x1b[201~"
            : normalizedText;
    }

    private string GetSelectedText()
    {
        var buffer = TerminalBuffer;
        if (buffer == null || !TryGetOrderedSelection(out var start, out var end))
            return string.Empty;

        var result = new StringBuilder();
        for (int row = start.Row; row <= end.Row; row++)
        {
            int startCol = row == start.Row ? start.Column : 0;
            int endColExclusive = row == end.Row ? end.Column : buffer.Columns;
            var line = new StringBuilder();

            for (int col = startCol; col < endColExclusive; col++)
            {
                var cell = GetViewportCell(buffer, row, col);
                if (!cell.IsWideContinuation)
                    line.Append(cell.GetText());
            }

            result.Append(line.ToString().TrimEnd());
            if (row < end.Row)
                result.AppendLine();
        }

        return result.ToString();
    }

    private CellPosition GetCellPosition(Point point, bool includeCell = false)
    {
        var buffer = TerminalBuffer;
        int columns = Math.Max(1, buffer?.Columns ?? _columns);
        int rows = Math.Max(1, buffer?.Rows ?? _rows);
        var contentPoint = point - GetContentRect(Bounds.Size).Position;
        int row = Math.Clamp((int)(contentPoint.Y / _cellHeight), 0, rows - 1);
        int column = Math.Clamp((int)(contentPoint.X / _cellWidth), 0, columns - 1);

        if (includeCell && contentPoint.X >= column * _cellWidth + _cellWidth / 2)
            column = Math.Min(columns, column + 1);

        return new CellPosition(row, column);
    }

    private static TerminalMouseButton? TryGetPressedMouseButton(PointerPointProperties properties)
    {
        if (properties.IsLeftButtonPressed)
            return TerminalMouseButton.Left;
        if (properties.IsRightButtonPressed)
            return TerminalMouseButton.Right;
        if (properties.IsMiddleButtonPressed)
            return TerminalMouseButton.Middle;
        return null;
    }

    private (int Column, int Row) GetMouseCell(Point point)
    {
        var contentPoint = point - GetContentRect(Bounds.Size).Position;
        var columns = Math.Max(1, TerminalBuffer?.Columns ?? _columns);
        var rows = Math.Max(1, TerminalBuffer?.Rows ?? _rows);
        return (
            Math.Clamp((int)(contentPoint.X / _cellWidth), 0, columns - 1),
            Math.Clamp((int)(contentPoint.Y / _cellHeight), 0, rows - 1));
    }

    private bool SendMouseReport(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        Point point,
        KeyModifiers modifiers)
    {
        var buffer = TerminalBuffer;
        if (buffer == null)
            return false;

        var cell = GetMouseCell(point);
        var bytes = TerminalMouseEncoder.Encode(
            type,
            button,
            cell.Column,
            cell.Row,
            modifiers.HasFlag(KeyModifiers.Shift),
            modifiers.HasFlag(KeyModifiers.Alt) || modifiers.HasFlag(KeyModifiers.Meta),
            modifiers.HasFlag(KeyModifiers.Control),
            buffer.MouseTracking,
            buffer.MouseEncoding);
        if (bytes is not { Length: > 0 })
            return false;

        ScrollToBottom();
        BinaryInputReceived?.Invoke(bytes);
        return true;
    }

    private bool TryGetOrderedSelection(out CellPosition start, out CellPosition end)
    {
        start = default;
        end = default;
        if (!HasSelection)
            return false;

        var anchor = _selectionAnchor!.Value;
        var selectionEnd = _selectionEnd!.Value;
        if (anchor.CompareTo(selectionEnd) <= 0)
        {
            start = anchor;
            end = selectionEnd;
        }
        else
        {
            start = selectionEnd;
            end = anchor;
        }

        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (LocalKeyHandler?.Invoke(e.Key) == true)
        {
            e.Handled = true;
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool useAltAsMeta = alt && (LeftAltAsMeta || RightAltAsMeta) && !(ctrl && CtrlAltAsAltGr);
        _pendingTextInputKey = e.Key;
        _pendingTextInputModifiers = e.KeyModifiers;
        // Avalonia 12 does not expose an IsRepeat flag on KeyEventArgs. A
        // repeated key-down therefore uses the protocol's press form until
        // the platform provides a distinct repeat event.
        _pendingTextInputEventType = TerminalKeyEventType.Press;

        if (e.Key == Key.Scroll)
        {
            _scrollLockActive = !_scrollLockActive;
            e.Handled = true;
            return;
        }

        // Ctrl+key combinations
        if (TerminalKeyboardEncoder.TryEncode(
                e.Key,
                e.KeyModifiers,
                TerminalBuffer?.ModifyOtherKeysMode ?? 0,
                TerminalBuffer?.KittyKeyboardFlags ?? 0,
                _pendingTextInputEventType,
                out var protocolData))
        {
            MaybeScrollToBottomForInput();
            InputReceived?.Invoke(protocolData);
            e.Handled = true;
            return;
        }

        if (ctrl && !alt)
        {
            string? ctrlData = GetControlCharacter(e.Key);

            if (ctrlData != null)
            {
                MaybeScrollToBottomForInput();
                InputReceived?.Invoke(ctrlData);
                e.Handled = true;
                return;
            }
        }

        var standardModifiers = useAltAsMeta
            ? e.KeyModifiers
            : e.KeyModifiers & ~KeyModifiers.Alt;
        string? data = TryGetCustomKeySequence(e.Key)
            ?? GetStandardKeySequence(e.Key, standardModifiers);

        if (data != null && useAltAsMeta && !IsModifiedCsiSequence(data))
            data = "\x1B" + data;

        if (data != null)
        {
            MaybeScrollToBottomForInput();
            InputReceived?.Invoke(data);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (TerminalKeyboardEncoder.TryEncode(
                e.Key,
                e.KeyModifiers,
                TerminalBuffer?.ModifyOtherKeysMode ?? 0,
                TerminalBuffer?.KittyKeyboardFlags ?? 0,
                TerminalKeyEventType.Release,
                out var protocolData))
        {
            MaybeScrollToBottomForInput();
            InputReceived?.Invoke(protocolData);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Converts the conventional Ctrl+letter combinations to the control
    /// characters used by POSIX terminals. Full-screen programs such as nano
    /// rely on the complete A-Z range, not just shell editing shortcuts.
    /// </summary>
    internal static string? GetControlCharacter(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return ((char)((int)key - (int)Key.A + 1)).ToString();

        return key switch
        {
            // ASCII control aliases commonly used by nano and other full-screen editors.
            Key.Space => "\0",              // Ctrl+Space / Ctrl+@
            Key.D6 => "\x1E",                // Ctrl+6 / Ctrl+^
            Key.OemOpenBrackets => "\x1B",   // Ctrl+[
            Key.OemBackslash => "\x1C",      // Ctrl+\\
            Key.OemCloseBrackets => "\x1D",  // Ctrl+]
            Key.OemMinus => "\x1F",           // Ctrl+_
            _ => null
        };
    }

    private string? GetStandardKeySequence(Key key, KeyModifiers modifiers)
    {
        return key switch
        {
            Key.Enter => NewLineMode ? "\r\n" : "\r",
            Key.Back => DestructiveBackspace ? ResolveEraseSequence(DeleteKeySequence) : ResolveEraseSequence(BackspaceKeySequence),
            Key.Tab => "\t",
            Key.Escape => "\x1B",
            Key.Up => GetCursorKeySequence('A', modifiers),
            Key.Down => GetCursorKeySequence('B', modifiers),
            Key.Right => GetCursorKeySequence('C', modifiers),
            Key.Left => GetCursorKeySequence('D', modifiers),
            Key.Home => GetHomeEndSequence(home: true, modifiers),
            Key.End => GetHomeEndSequence(home: false, modifiers),
            Key.Insert => GetTildeSequence("2", "\x1B[2~", modifiers),
            Key.Delete => GetTildeSequence("3", ResolveEraseSequence(DeleteKeySequence), modifiers),
            Key.PageUp => GetTildeSequence("5", "\x1B[5~", modifiers),
            Key.PageDown => GetTildeSequence("6", "\x1B[6~", modifiers),
            Key.F1 => GetFunctionKeySequence(1, modifiers),
            Key.F2 => GetFunctionKeySequence(2, modifiers),
            Key.F3 => GetFunctionKeySequence(3, modifiers),
            Key.F4 => GetFunctionKeySequence(4, modifiers),
            Key.F5 => GetFunctionKeySequence(5, modifiers),
            Key.F6 => GetFunctionKeySequence(6, modifiers),
            Key.F7 => GetFunctionKeySequence(7, modifiers),
            Key.F8 => GetFunctionKeySequence(8, modifiers),
            Key.F9 => GetFunctionKeySequence(9, modifiers),
            Key.F10 => GetFunctionKeySequence(10, modifiers),
            Key.F11 => GetFunctionKeySequence(11, modifiers),
            Key.F12 => GetFunctionKeySequence(12, modifiers),
            Key.NumPad0 => GetNumericKeypadSequence("0", "Op"),
            Key.NumPad1 => GetNumericKeypadSequence("1", "Oq"),
            Key.NumPad2 => GetNumericKeypadSequence("2", "Or"),
            Key.NumPad3 => GetNumericKeypadSequence("3", "Os"),
            Key.NumPad4 => GetNumericKeypadSequence("4", "Ot"),
            Key.NumPad5 => GetNumericKeypadSequence("5", "Ou"),
            Key.NumPad6 => GetNumericKeypadSequence("6", "Ov"),
            Key.NumPad7 => GetNumericKeypadSequence("7", "Ow"),
            Key.NumPad8 => GetNumericKeypadSequence("8", "Ox"),
            Key.NumPad9 => GetNumericKeypadSequence("9", "Oy"),
            _ => null
        };
    }

    private string GetCursorKeySequence(char suffix, KeyModifiers modifiers)
    {
        if (HasKeyModifier(modifiers))
            return $"\x1B[1;{TerminalKeyboardEncoder.GetModifierCode(modifiers)}{suffix}";

        var shiftLimitsApplication = ShiftLimitsApplicationCursorMode && modifiers.HasFlag(KeyModifiers.Shift);
        var applicationMode = UseApplicationCursorMode &&
                              !shiftLimitsApplication &&
                              (TerminalBuffer?.CursorKeyApplicationMode == true ||
                               string.Equals(CursorKeyMode, "Application", StringComparison.OrdinalIgnoreCase));
        return applicationMode ? $"\x1BO{suffix}" : $"\x1B[{suffix}";
    }

    private void MaybeScrollToBottomForInput()
    {
        if (ScrollToBottomOnInputOutput || ScrollToBottomByKey)
            ScrollToBottom();
    }

    private string GetNumericKeypadSequence(string normal, string applicationSuffix)
    {
        var forceNormal = string.Equals(NumericKeypadMode, "ForceNormal", StringComparison.OrdinalIgnoreCase);
        var applicationMode = !forceNormal &&
                              (TerminalBuffer?.NumericKeypadApplicationMode == true ||
                               string.Equals(NumericKeypadMode, "Application", StringComparison.OrdinalIgnoreCase));

        return applicationMode ? "\x1B" + applicationSuffix : normal;
    }

    private static string ResolveEraseSequence(string? mode)
    {
        return mode?.Trim().ToUpperInvariant() switch
        {
            "ASCII127" => "\x7F",
            "BACKSPACE" => "\x08",
            _ => "\x1B[3~"
        };
    }

    private string GetHomeEndSequence(bool home, KeyModifiers modifiers)
    {
        var normal = home
            ? UseRxvtHomeEnd ? "\x1B[7~" : "\x1B[H"
            : UseRxvtHomeEnd ? "\x1B[8~" : "\x1B[F";
        return GetTildeSequence(home ? "1" : "4", normal, modifiers, home ? 'H' : 'F');
    }

    private static string GetTildeSequence(
        string parameter,
        string normal,
        KeyModifiers modifiers,
        char? nonTildeFinal = null)
    {
        if (!HasKeyModifier(modifiers))
            return normal;

        var modifier = TerminalKeyboardEncoder.GetModifierCode(modifiers);
        return nonTildeFinal is { } final
            ? $"\x1B[1;{modifier}{final}"
            : $"\x1B[{parameter};{modifier}~";
    }

    private string? GetFunctionKeySequence(int index, KeyModifiers modifiers)
    {
        var mode = KeyboardFunctionKeyMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase))
            mode = "XtermR6";

        var sequence = mode.ToUpperInvariant() switch
        {
            "ESCN" => index is >= 1 and <= 12 ? $"\x1B[{index + 10}~" : null,
            "LINUX" => GetLinuxFunctionKeySequence(index),
            "VT400" => GetXtermFunctionKeySequence(index),
            "VT100PLUS" => GetVt100FunctionKeySequence(index),
            "SCO" => index is >= 1 and <= 12 ? "\x1B[" + (char)('M' + index - 1) : null,
            _ => GetXtermFunctionKeySequence(index)
        };

        if (sequence == null || !HasKeyModifier(modifiers))
            return sequence;

        var modifier = TerminalKeyboardEncoder.GetModifierCode(modifiers);
        return index switch
        {
            1 => $"\x1B[1;{modifier}P",
            2 => $"\x1B[1;{modifier}Q",
            3 => $"\x1B[1;{modifier}R",
            4 => $"\x1B[1;{modifier}S",
            _ => GetModifiedFunctionTilde(index, modifier)
        };
    }

    private static string GetModifiedFunctionTilde(int index, int modifier)
    {
        var parameter = index switch
        {
            5 => 15,
            6 => 17,
            7 => 18,
            8 => 19,
            9 => 20,
            10 => 21,
            11 => 23,
            12 => 24,
            _ => 0
        };
        return parameter == 0 ? string.Empty : $"\x1B[{parameter};{modifier}~";
    }

    private static bool HasKeyModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) ||
        modifiers.HasFlag(KeyModifiers.Alt) ||
        modifiers.HasFlag(KeyModifiers.Control) ||
        modifiers.HasFlag(KeyModifiers.Meta);

    private static bool IsModifiedCsiSequence(string value) =>
        value.StartsWith("\x1B[1;", StringComparison.Ordinal) ||
        value.Contains(";2~", StringComparison.Ordinal) ||
        value.Contains(";3~", StringComparison.Ordinal) ||
        value.Contains(";4~", StringComparison.Ordinal) ||
        value.Contains(";5~", StringComparison.Ordinal) ||
        value.Contains(";6~", StringComparison.Ordinal) ||
        value.Contains(";7~", StringComparison.Ordinal) ||
        value.Contains(";8~", StringComparison.Ordinal) ||
        value.Contains(";9~", StringComparison.Ordinal) ||
        value.Contains(";10~", StringComparison.Ordinal) ||
        value.Contains(";11~", StringComparison.Ordinal) ||
        value.Contains(";12~", StringComparison.Ordinal);

    private static string? GetVt100FunctionKeySequence(int index)
    {
        return index switch
        {
            1 => "\x1BOP",
            2 => "\x1BOQ",
            3 => "\x1BOR",
            4 => "\x1BOS",
            _ => GetXtermFunctionKeySequence(index)
        };
    }

    private static string? GetLinuxFunctionKeySequence(int index)
    {
        return index switch
        {
            1 => "\x1B[[A",
            2 => "\x1B[[B",
            3 => "\x1B[[C",
            4 => "\x1B[[D",
            5 => "\x1B[[E",
            _ => GetXtermFunctionKeySequence(index)
        };
    }

    private static string? GetXtermFunctionKeySequence(int index)
    {
        return index switch
        {
            1 => "\x1BOP",
            2 => "\x1BOQ",
            3 => "\x1BOR",
            4 => "\x1BOS",
            5 => "\x1B[15~",
            6 => "\x1B[17~",
            7 => "\x1B[18~",
            8 => "\x1B[19~",
            9 => "\x1B[20~",
            10 => "\x1B[21~",
            11 => "\x1B[23~",
            12 => "\x1B[24~",
            _ => null
        };
    }

    private string? TryGetCustomKeySequence(Key key)
    {
        if (!string.Equals(KeyboardFunctionKeyMode, "UserCustom", StringComparison.OrdinalIgnoreCase))
            return null;

        var map = GetCustomKeyboardMap();
        if (map == null)
            return null;

        return map.TryGetValue(key.ToString(), out var sequence) ? sequence : null;
    }

    private System.Collections.Generic.Dictionary<string, string>? GetCustomKeyboardMap()
    {
        var path = KeyboardMappingFile?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_customKeyboardMap != null && string.Equals(_loadedKeyboardMappingFile, path, StringComparison.OrdinalIgnoreCase))
            return _customKeyboardMap;

        _loadedKeyboardMappingFile = path;
        _customKeyboardMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(path))
                return _customKeyboardMap;

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var keyName = line[..separatorIndex].Trim();
                var sequence = DecodeKeySequence(line[(separatorIndex + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(keyName))
                    _customKeyboardMap[keyName] = sequence;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading keyboard mapping file: {ex.Message}");
        }

        return _customKeyboardMap;
    }

    private static string DecodeKeySequence(string text)
    {
        return text
            .Replace("\\e", "\x1B", StringComparison.OrdinalIgnoreCase)
            .Replace("\\r", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\t", "\t", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x1b", "\x1B", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x7f", "\x7F", StringComparison.OrdinalIgnoreCase)
            .Replace("\\x08", "\x08", StringComparison.OrdinalIgnoreCase);
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset == 0)
            return;

        _scrollOffset = 0;
        _selectionAnchor = null;
        _selectionEnd = null;
        InvalidateVisual();
    }

    private void ClampScrollOffset()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, TerminalBuffer == null ? 0 : GetMaxScrollOffset(TerminalBuffer));
    }

    private bool ShouldShowScrollbar(TerminalBuffer buffer)
    {
        return GetMaxScrollOffset(buffer) > 0 && GetContentRect(Bounds.Size).Height > ScrollbarMinThumbHeight;
    }

    private Rect GetContentRect(Size size)
    {
        var padding = TerminalPadding;
        var left = Math.Clamp(padding.Left, 0, Math.Max(0, size.Width));
        var top = Math.Clamp(padding.Top, 0, Math.Max(0, size.Height));
        var right = Math.Clamp(padding.Right, 0, Math.Max(0, size.Width - left));
        var bottom = Math.Clamp(padding.Bottom, 0, Math.Max(0, size.Height - top));
        return new Rect(
            left,
            top,
            Math.Max(0, size.Width - left - right),
            Math.Max(0, size.Height - top - bottom));
    }

    private Rect GetScrollbarTrackRect()
    {
        var content = GetContentRect(Bounds.Size);
        return new Rect(Math.Max(content.X, content.Right - ScrollbarWidth), content.Y, ScrollbarWidth, content.Height);
    }

    private Rect GetScrollbarThumbRect(TerminalBuffer buffer, Rect track)
    {
        var maxOffset = GetMaxScrollOffset(buffer);
        var totalRows = maxOffset + buffer.Rows;
        var thumbHeight = Math.Clamp(
            track.Height * buffer.Rows / Math.Max(buffer.Rows, totalRows),
            ScrollbarMinThumbHeight,
            track.Height);
        var travel = Math.Max(0, track.Height - thumbHeight);
        maxOffset = Math.Max(1, maxOffset);
        var topRatio = (maxOffset - _scrollOffset) / (double)maxOffset;
        var thumbY = track.Y + travel * topRatio;
        return new Rect(track.X + 2, thumbY, Math.Max(4, track.Width - 4), thumbHeight);
    }

    private bool IsPointInScrollbar(Point point, TerminalBuffer buffer)
    {
        return ShouldShowScrollbar(buffer) && GetScrollbarTrackRect().Contains(point);
    }

    private void UpdatePointerCursor(Point point)
    {
        var buffer = TerminalBuffer;
        var overScrollbar = buffer != null && IsPointInScrollbar(point, buffer);
        var hyperlinkUri = overScrollbar ? null : TryGetHyperlinkAtPoint(point);
        if (overScrollbar == _isPointerOverScrollbar &&
            string.Equals(hyperlinkUri, _pointerHyperlinkUri, StringComparison.Ordinal))
            return;

        _isPointerOverScrollbar = overScrollbar;
        _pointerHyperlinkUri = hyperlinkUri;
        Cursor = new Cursor(overScrollbar
            ? StandardCursorType.Arrow
            : hyperlinkUri != null ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    private string? TryGetHyperlinkAtPoint(Point point)
    {
        var buffer = TerminalBuffer;
        if (buffer == null || _cellWidth <= 0 || _cellHeight <= 0)
            return null;

        var position = GetCellPosition(point);
        var cell = GetViewportCell(buffer, position.Row, position.Column);
        if (cell.HyperlinkUri == null && position.Column > 0)
            cell = GetViewportCell(buffer, position.Row, position.Column - 1);

        return IsOpenableHyperlink(cell.HyperlinkUri) ? cell.HyperlinkUri : null;
    }

    private static bool IsOpenableHyperlink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenHyperlink(string value)
    {
        if (!IsOpenableHyperlink(value))
            return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = value,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening terminal hyperlink: {ex.Message}");
            return false;
        }
    }

    private void UpdateScrollOffsetFromScrollbar(double pointerY, TerminalBuffer buffer)
    {
        var track = GetScrollbarTrackRect();
        var thumb = GetScrollbarThumbRect(buffer, track);
        var travel = Math.Max(1, track.Height - thumb.Height);
        var thumbY = Math.Clamp(pointerY - _scrollbarDragOffsetY, track.Y, track.Bottom - thumb.Height);
        var topRatio = (thumbY - track.Y) / travel;
        _scrollOffset = (int)Math.Round(GetMaxScrollOffset(buffer) * (1 - topRatio));
        ClampScrollOffset();
    }

    private static int GetMaxScrollOffset(TerminalBuffer buffer)
    {
        return Math.Min(buffer.ScrollbackCount, buffer.MaxMeaningfulScrollOffset);
    }

    private Rect GetCursorRectangle()
    {
        var buffer = TerminalBuffer;
        var col = buffer?.CursorCol ?? _columns;
        var row = buffer?.CursorRow ?? _rows;
        var content = GetContentRect(Bounds.Size);
        return new Rect(content.X + col * _cellWidth, content.Y + row * _cellHeight, _cellWidth, _cellHeight);
    }

    private sealed class TerminalTextInputMethodClient : TextInputMethodClient
    {
        private readonly TerminalControl _owner;

        public TerminalTextInputMethodClient(TerminalControl owner)
        {
            _owner = owner;
        }

        public override Visual TextViewVisual => _owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => _owner.GetCursorRectangle();
        public override TextSelection Selection
        {
            get => new(0, 0);
            set { }
        }

        public override void SetPreeditText(string? preeditText)
        {
        }

        public override void SetPreeditText(string? preeditText, int? cursorOffset)
        {
        }

        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
        }
    }

    private readonly record struct CellPosition(int Row, int Column) : IComparable<CellPosition>
    {
        public int CompareTo(CellPosition other)
        {
            int rowComparison = Row.CompareTo(other.Row);
            return rowComparison != 0 ? rowComparison : Column.CompareTo(other.Column);
        }
    }
}
