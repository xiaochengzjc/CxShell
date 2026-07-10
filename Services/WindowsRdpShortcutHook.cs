using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CxShell.Services;

public sealed class WindowsRdpShortcutHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkLeftMenu = 0xA4;
    private const uint VkRightMenu = 0xA5;

    private readonly Action<uint, bool> _sendKey;
    private readonly HookProcedure _hookProcedure;
    private readonly Dictionary<uint, uint> _capturedKeys = new();
    private IntPtr _hookHandle;
    private bool _controlDown;
    private bool _altDown;
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _disposed;

    private WindowsRdpShortcutHook(Action<uint, bool> sendKey)
    {
        _sendKey = sendKey;
        _hookProcedure = HookCallback;
        _hookHandle = SetWindowsHookEx(
            WhKeyboardLl,
            _hookProcedure,
            GetModuleHandle(null),
            0);
    }

    public static WindowsRdpShortcutHook? TryCreate(Action<uint, bool> sendKey)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var hook = new WindowsRdpShortcutHook(sendKey);
        if (hook._hookHandle != IntPtr.Zero)
            return hook;

        hook.Dispose();
        return null;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _disposed)
            return CallNextHookEx(_hookHandle, code, message, data);

        var messageId = unchecked((int)message.ToInt64());
        var isDown = messageId is WmKeyDown or WmSysKeyDown;
        var isUp = messageId is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
            return CallNextHookEx(_hookHandle, code, message, data);

        var keyboardData = Marshal.PtrToStructure<KbdLlHookStruct>(data);
        if ((keyboardData.Flags & LlkhfInjected) != 0)
            return CallNextHookEx(_hookHandle, code, message, data);

        UpdateModifierState(keyboardData.VirtualKeyCode, isDown);
        var wasCaptured = _capturedKeys.TryGetValue(keyboardData.VirtualKeyCode, out var capturedScancode);
        var shouldCapture = wasCaptured || RdpSystemShortcutPolicy.ShouldCapture(
            keyboardData.VirtualKeyCode,
            _altDown,
            _controlDown,
            _leftWindowsDown || _rightWindowsDown);

        if (!shouldCapture)
            return CallNextHookEx(_hookHandle, code, message, data);

        var scancode = wasCaptured
            ? capturedScancode
            : ResolveRdpScancode(keyboardData);
        if (scancode == 0)
            return CallNextHookEx(_hookHandle, code, message, data);

        try
        {
            _sendKey(scancode, isDown);
            if (isDown)
                _capturedKeys[keyboardData.VirtualKeyCode] = scancode;
            else
                _capturedKeys.Remove(keyboardData.VirtualKeyCode);
            return new IntPtr(1);
        }
        catch
        {
            return CallNextHookEx(_hookHandle, code, message, data);
        }
    }

    private void UpdateModifierState(uint virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case VkControl:
            case VkLeftControl:
            case VkRightControl:
                _controlDown = isDown;
                break;
            case VkMenu:
            case VkLeftMenu:
            case VkRightMenu:
                _altDown = isDown;
                break;
            case RdpSystemShortcutPolicy.VirtualKeyLeftWindows:
                _leftWindowsDown = isDown;
                break;
            case RdpSystemShortcutPolicy.VirtualKeyRightWindows:
                _rightWindowsDown = isDown;
                break;
        }
    }

    private static uint ResolveRdpScancode(KbdLlHookStruct keyboardData)
    {
        var scancode = keyboardData.ScanCode;
        if (scancode == 0)
            scancode = MapVirtualKey(keyboardData.VirtualKeyCode, 0);

        if (scancode != 0 && (keyboardData.Flags & LlkhfExtended) != 0)
            scancode |= 0x0100;

        return scancode;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        foreach (var scancode in _capturedKeys.Values)
        {
            try
            {
                _sendKey(scancode, false);
            }
            catch
            {
            }
        }

        _capturedKeys.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKeyCode;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProcedure callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
