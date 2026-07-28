using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenVocare.Models;

namespace OpenVocare.Services;

public sealed class GlobalInputService : IDisposable
{
    private const int HotkeyId = 0x564449;
    private const int WmHotkey = 0x0312;
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmXButtonDoubleClick = 0x020D;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int VkEscape = 0x1B;
    private const uint ModNoRepeat = 0x4000;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _canCancel;
    private readonly Native.HookProc _mouseProc;
    private readonly Native.HookProc _keyboardProc;
    private HwndSource? _source;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private ShortcutSettings _settings = new();
    private uint _registeredModifiers;
    private uint _registeredVirtualKey;
    private bool _hotkeyRegistered;
    private bool _cancellationEnabled;
    private bool _keyboardHoldActive;
    private bool _mouseHoldActive;
    private bool _toggleActive;
    private bool _disposed;

    public GlobalInputService(Dispatcher dispatcher, Func<bool> canCancel)
    {
        _dispatcher = dispatcher;
        _canCancel = canCancel;
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public event EventHandler? HoldStarted;
    public event EventHandler? HoldReleased;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? RegistrationFailed;

    public void Start(Window owner, ShortcutSettings settings)
    {
        IntPtr handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("OpenVocare could not create its message source.");
        _source.AddHook(WindowProcedure);
        try
        {
            Reconfigure(settings);
        }
        catch
        {
            ReleaseNativeRegistrations();
            throw;
        }
    }

    public bool Reconfigure(ShortcutSettings settings)
    {
        if (_source is null)
        {
            _settings = CopySettings(settings);
            return true;
        }

        string? validationError = GetShortcutValidationError(settings);
        if (validationError is not null)
        {
            RegistrationFailed?.Invoke(this, validationError);
            return false;
        }

        uint virtualKey = 0;
        if (settings.KeyboardShortcutEnabled && !TryGetVirtualKey(settings.Key, out virtualKey))
        {
            RegistrationFailed?.Invoke(this, "The keyboard shortcut key is invalid.");
            return false;
        }

        IntPtr handle = _source.Handle;
        uint previousModifiers = _registeredModifiers;
        uint previousVirtualKey = _registeredVirtualKey;
        bool hadPreviousRegistration = _hotkeyRegistered;
        if (hadPreviousRegistration)
        {
            Native.UnregisterHotKey(handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        uint registrationModifiers = RegistrationModifiers(settings.Modifiers);
        if (settings.KeyboardShortcutEnabled && !Native.RegisterHotKey(handle, HotkeyId, registrationModifiers, virtualKey))
        {
            if (hadPreviousRegistration && Native.RegisterHotKey(handle, HotkeyId, previousModifiers, previousVirtualKey))
            {
                _registeredModifiers = previousModifiers;
                _registeredVirtualKey = previousVirtualKey;
                _hotkeyRegistered = true;
            }
            RegistrationFailed?.Invoke(this, "The keyboard shortcut is already in use by another application.");
            return false;
        }

        _settings = CopySettings(settings);
        ResetActivationState();
        _registeredModifiers = registrationModifiers;
        _registeredVirtualKey = virtualKey;
        _hotkeyRegistered = settings.KeyboardShortcutEnabled;
        UpdateMouseHook();
        UpdateKeyboardHook();
        return true;
    }

    internal static bool IsValidShortcutKey(string keyName) => TryGetVirtualKey(keyName, out _);

    internal static string? GetShortcutValidationError(ShortcutSettings settings)
    {
        if (!settings.KeyboardShortcutEnabled)
        {
            return settings.MouseButton == MouseShortcutButton.None
                ? "Enable the keyboard shortcut or choose a mouse shortcut."
                : null;
        }

        if (!TryGetVirtualKey(settings.Key, out _))
        {
            return "Enter a valid key, such as Space, F8, or D.";
        }

        if (settings.Modifiers == HotkeyModifiers.None
            && (!Enum.TryParse(settings.Key, true, out Key key) || key < Key.F1 || key > Key.F24))
        {
            return "Add at least one modifier, or use a function key from F1 through F24 by itself.";
        }

        return null;
    }

    internal static uint RegistrationModifiers(HotkeyModifiers modifiers) => (uint)modifiers | ModNoRepeat;

    public void SetCancellationEnabled(bool enabled)
    {
        _cancellationEnabled = enabled;
        UpdateKeyboardHook();
    }

    private void UpdateKeyboardHook()
    {
        bool needed = _cancellationEnabled || _settings.KeyboardShortcutEnabled;
        if (needed && _keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = InstallHook(WhKeyboardLl, _keyboardProc);
        }
        else if (!needed && _keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private void UpdateMouseHook()
    {
        if (_settings.MouseButton != MouseShortcutButton.None && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = InstallHook(WhMouseLl, _mouseProc);
        }
        else if (_settings.MouseButton == MouseShortcutButton.None && _mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_settings.KeyboardActivationMode == ShortcutActivationMode.Toggle)
            {
                ToggleActivation();
            }
            else if (!_keyboardHoldActive && !_toggleActive && !_mouseHoldActive)
            {
                _keyboardHoldActive = true;
                HoldStarted?.Invoke(this, EventArgs.Empty);
            }
        }

        return IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsXButtonMessage(wParam.ToInt32()) && _settings.MouseButton != MouseShortcutButton.None)
        {
            Native.MouseHookData data = Marshal.PtrToStructure<Native.MouseHookData>(lParam);
            MouseHookDecision decision = EvaluateMouseMessage(wParam.ToInt32(), data.MouseData, _settings);
            if (_settings.MouseActivationMode == ShortcutActivationMode.Toggle && decision.Press)
            {
                _dispatcher.BeginInvoke(ToggleActivation);
            }
            else if (decision.Press && !_mouseHoldActive && !_toggleActive && !_keyboardHoldActive)
            {
                _mouseHoldActive = true;
                _dispatcher.BeginInvoke(() => HoldStarted?.Invoke(this, EventArgs.Empty));
            }
            if (_settings.MouseActivationMode == ShortcutActivationMode.Hold
                && decision.Release && _mouseHoldActive)
            {
                _mouseHoldActive = false;
                _dispatcher.BeginInvoke(() => HoldReleased?.Invoke(this, EventArgs.Empty));
            }
            if (decision.Consume)
            {
                return new IntPtr(1);
            }
        }

        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void ToggleActivation()
    {
        _toggleActive = !_toggleActive;
        if (_toggleActive)
        {
            HoldStarted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            HoldReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetActivationState()
    {
        _keyboardHoldActive = false;
        _mouseHoldActive = false;
        _toggleActive = false;
    }

    internal readonly record struct MouseHookDecision(bool Press, bool Release, bool Consume);

    internal static MouseHookDecision EvaluateMouseMessage(int message, uint mouseData, ShortcutSettings settings)
    {
        if (!IsXButtonMessage(message) || settings.MouseButton == MouseShortcutButton.None)
        {
            return default;
        }

        int pressed = (int)((mouseData >> 16) & 0xFFFF);
        bool matches = (settings.MouseButton == MouseShortcutButton.XButton1 && pressed == 1)
            || (settings.MouseButton == MouseShortcutButton.XButton2 && pressed == 2);
        if (!matches)
        {
            return default;
        }

        // Browsers commonly navigate on button-up. Swallow the entire matched
        // sequence, while firing dictation only on the initial button-down.
        return new MouseHookDecision(
            message == WmXButtonDown,
            message == WmXButtonUp,
            settings.ConsumeMouseButton);
    }

    private static bool IsXButtonMessage(int message) =>
        message is WmXButtonDown or WmXButtonUp or WmXButtonDoubleClick;

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            Native.KeyboardHookData data = Marshal.PtrToStructure<Native.KeyboardHookData>(lParam);
            int message = wParam.ToInt32();
            if (_cancellationEnabled && _canCancel() && IsCancellationKey(message, data.VirtualKeyCode))
            {
                _dispatcher.BeginInvoke(() => CancelRequested?.Invoke(this, EventArgs.Empty));
                return new IntPtr(1);
            }
            if (_keyboardHoldActive
            && data.VirtualKeyCode == _registeredVirtualKey
                && _settings.KeyboardActivationMode == ShortcutActivationMode.Hold
                && message is WmKeyUp or WmSysKeyUp)
            {
                _keyboardHoldActive = false;
                _dispatcher.BeginInvoke(() => HoldReleased?.Invoke(this, EventArgs.Empty));
            }
        }

        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool TryGetVirtualKey(string keyName, out uint virtualKey)
    {
        if (Enum.TryParse(keyName, true, out Key key))
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return virtualKey != 0;
        }

        virtualKey = 0;
        return false;
    }

    internal static ShortcutSettings CopySettings(ShortcutSettings settings) => new()
    {
        KeyboardShortcutEnabled = settings.KeyboardShortcutEnabled,
        Modifiers = settings.Modifiers,
        Key = settings.Key,
        MouseButton = settings.MouseButton,
        ConsumeMouseButton = settings.ConsumeMouseButton,
        KeyboardActivationMode = settings.KeyboardActivationMode,
        MouseActivationMode = settings.MouseActivationMode
    };

    internal static bool IsCancellationKey(int message, uint virtualKeyCode) => message == WmKeyDown && virtualKeyCode == VkEscape;

    private static IntPtr InstallHook(int hookType, Native.HookProc proc)
    {
        using Process process = Process.GetCurrentProcess();
        string? moduleName = process.MainModule?.ModuleName;
        IntPtr module = Native.GetModuleHandle(moduleName);
        IntPtr hook = Native.SetWindowsHookEx(hookType, proc, module, 0);
        if (hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("OpenVocare could not register its global input hook.");
        }

        return hook;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseNativeRegistrations();
        _disposed = true;
    }

    private void ReleaseNativeRegistrations()
    {
        if (_source is not null)
        {
            if (_hotkeyRegistered)
            {
                Native.UnregisterHotKey(_source.Handle, HotkeyId);
            }
            _hotkeyRegistered = false;
            ResetActivationState();
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private static class Native
    {
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseHookData
        {
            public Point Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardHookData
        {
            public uint VirtualKeyCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
