namespace CxShell.Terminal;

/// <summary>
/// 鼠标报告模式。终端应用通过 DECSET 打开这些模式后，控件把指针事件编码后发回远端。
/// </summary>
public enum TerminalMouseTracking
{
    None,
    X10,
    Normal,
    ButtonEvent,
    AnyEvent
}

/// <summary>终端应用请求的鼠标坐标编码。</summary>
public enum TerminalMouseEncoding
{
    Default,
    Sgr,
    Urxvt
}

public enum TerminalMouseEventType
{
    Press,
    Release,
    Move
}

public enum TerminalMouseButton
{
    Left = 0,
    Middle = 1,
    Right = 2,
    None = 3,
    WheelUp = 64,
    WheelDown = 65
}
