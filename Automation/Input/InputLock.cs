using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;

namespace BeastsV3.Automation.Input;

// Low-level Win32 hooks that suppress user input during automation. Per-key allowance
// windows let synthetic input through, and the cursor is clipped to the automation's
// last set position.
public sealed class InputLock : IDisposable
{
    private static readonly TimeSpan KeyboardAllowanceDuration = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan MouseAllowanceDuration = TimeSpan.FromMilliseconds(75);

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHWheel = 0x020E;
    private const int LlkhfInjected = 0x10;
    private const int LlmhfInjected = 0x01;

    private readonly RuntimeState _state;
    private readonly BeastsSettings _settings;
    private readonly object _hookSync = new();
    private readonly object _allowanceSync = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;

    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    // The hooks are owned by this thread, which pumps messages so the OS can deliver callbacks.
    private Thread _hookThread;
    private uint _hookThreadId;
    private volatile bool _hookInstallSucceeded;
    private readonly ManualResetEventSlim _hooksReady = new(false);
    private const int HookInstallTimeoutMs = 1000;
    private const uint WmQuit = 0x0012;

    private bool _disposed;
    private volatile bool _isActive;
    private volatile int _lockedCursorX;
    private volatile int _lockedCursorY;
    private volatile bool _hasLockedCursorPosition;
    private long _allowMouseUntilUtcTicks;
    private Keys[] _allowedKeys = [];
    private readonly Dictionary<Keys, long> _temporaryAllowedKeysUntilUtcTicks = new();

    public InputLock(RuntimeState state, BeastsSettings settings)
    {
        _state = state;
        _settings = settings;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public bool EnableForRun(IEnumerable<Keys> allowedKeys)
    {
        ThrowIfDisposed();
        if (!_settings.Timing.General.LockUserInputDuringAutomation.Value) { DisableForRun(); return false; }
        if (!EnsureHooksInstalled()) { DisableForRun(); return false; }

        _allowedKeys = allowedKeys?.Where(k => k != Keys.None).Distinct().ToArray() ?? [];

        if (TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            _lockedCursorX = cursorX;
            _lockedCursorY = cursorY;
            _hasLockedCursorPosition = true;
        }

        _isActive = true;
        _state.IsInputLockActive = true;
        ApplyCursorClip();
        return true;
    }

    public void DisableForRun()
    {
        _isActive = false;
        _state.IsInputLockActive = false;
        _allowedKeys = [];
        _hasLockedCursorPosition = false;
        lock (_allowanceSync) _temporaryAllowedKeysUntilUtcTicks.Clear();
        _allowMouseUntilUtcTicks = 0;
        ReleaseCursorClip();

        lock (_hookSync)
        {
            // Unhooking happens on the owning thread, so this signals it to exit.
            StopHookThread();
        }
    }

    public void AllowKeys(params Keys[] keys)
    {
        if (!_isActive || keys == null || keys.Length == 0) return;

        var allowUntil = DateTime.UtcNow.Add(KeyboardAllowanceDuration).Ticks;
        lock (_allowanceSync)
        {
            foreach (var key in keys)
            {
                if (key == Keys.None) continue;
                _temporaryAllowedKeysUntilUtcTicks[key] = allowUntil;
            }
        }
    }

    public void AllowMouse()
    {
        if (!_isActive) return;
        _allowMouseUntilUtcTicks = DateTime.UtcNow.Add(MouseAllowanceDuration).Ticks;
    }

    public void TrackCursor(float x, float y)
    {
        if (!_isActive) return;
        _lockedCursorX = (int)Math.Round(x);
        _lockedCursorY = (int)Math.Round(y);
        _hasLockedCursorPosition = true;
        ApplyCursorClip();
    }

    public void EnforceCursor()
    {
        if (!_isActive || !_hasLockedCursorPosition) return;
        if (!TryGetCursorPosition(out var curX, out var curY)) return;
        if (curX == _lockedCursorX && curY == _lockedCursorY) return;
        ApplyCursorClip();
        SetCursorPos(_lockedCursorX, _lockedCursorY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisableForRun();
        lock (_hookSync)
        {
            // DisableForRun already stopped the thread, which unhooks on its way out.
            StopHookThread();
            _disposed = true;
        }
        _hooksReady.Dispose();
    }

    // ---- private ---------------------------------------------------------

    // Installs the keyboard and mouse hooks on a dedicated message-pumping thread if they are
    // not already active, and blocks until they are up.
    private bool EnsureHooksInstalled()
    {
        lock (_hookSync)
        {
            if (_keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero) return true;
            if (_hookThread is { IsAlive: true }) return _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

            _hooksReady.Reset();
            _hookInstallSucceeded = false;

            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "BeastsV3 InputLock",
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            // Bounded so a failure to install can never hang a run.
            if (!_hooksReady.Wait(HookInstallTimeoutMs))
            {
                Log.Warn("Input lock hooks did not install within the timeout; continuing without the lock.");
                StopHookThread();
                return false;
            }

            return _hookInstallSucceeded;
        }
    }

    // Owns the hooks for their whole lifetime: installs, pumps, and unhooks on the way out.
    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();

        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleHandle = GetModuleHandle(module?.ModuleName);

            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, moduleHandle, 0);

            _hookInstallSucceeded = _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
            if (!_hookInstallSucceeded)
            {
                Unhook(ref _keyboardHook);
                Unhook(ref _mouseHook);
            }
        }
        catch (Exception ex)
        {
            _hookInstallSucceeded = false;
            Log.Error("Failed to install input lock hooks", ex);
        }
        finally
        {
            _hooksReady.Set();
        }

        if (!_hookInstallSucceeded) return;

        // The message loop. Callbacks arrive while this pumps; without it the OS times out on
        // every event. GetMessage returns 0 on the WM_QUIT that StopHookThread posts.
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Input lock message loop failed", ex);
        }
        finally
        {
            // Unhooked from the same thread that installed them, as Windows requires.
            Unhook(ref _keyboardHook);
            Unhook(ref _mouseHook);
        }
    }

    private void StopHookThread()
    {
        var thread = _hookThread;
        _hookThread = null;

        if (thread == null) return;

        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _hookThreadId = 0;

        // Bounded: a stuck hook thread must not hold up the end of a run.
        try { thread.Join(HookInstallTimeoutMs); } catch { }

        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isActive) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var message = unchecked((int)(long)wParam);
        if (message != WmKeyDown && message != WmKeyUp && message != WmSysKeyDown && message != WmSysKeyUp)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((kb.flags & LlkhfInjected) != 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var key = (Keys)kb.vkCode;
        if (Array.IndexOf(_allowedKeys, key) >= 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        if (IsKeyTemporarilyAllowed(key)) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        return (IntPtr)1;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isActive) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var message = unchecked((int)(long)wParam);
        if (!ShouldSuppressMouseMessage(message)) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var m = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if ((m.flags & LlmhfInjected) != 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        if (IsMouseTemporarilyAllowed()) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        return (IntPtr)1;
    }

    private bool IsKeyTemporarilyAllowed(Keys key)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        lock (_allowanceSync)
        {
            if (!_temporaryAllowedKeysUntilUtcTicks.TryGetValue(key, out var allowUntil)) return false;
            if (allowUntil >= nowTicks) return true;
            _temporaryAllowedKeysUntilUtcTicks.Remove(key);
            return false;
        }
    }

    private bool IsMouseTemporarilyAllowed() =>
        DateTime.UtcNow.Ticks <= _allowMouseUntilUtcTicks;

    private static bool ShouldSuppressMouseMessage(int message) =>
        message == WmMouseMove || message == WmLButtonDown || message == WmLButtonUp ||
        message == WmRButtonDown || message == WmRButtonUp || message == WmMButtonDown ||
        message == WmMButtonUp || message == WmMouseWheel || message == WmMouseHWheel ||
        message == WmXButtonDown || message == WmXButtonUp;

    private static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var p)) { x = p.X; y = p.Y; return true; }
        x = 0; y = 0; return false;
    }

    private void ApplyCursorClip()
    {
        if (!_isActive || !_hasLockedCursorPosition) return;
        var rect = new Rect { Left = _lockedCursorX, Top = _lockedCursorY, Right = _lockedCursorX + 1, Bottom = _lockedCursorY + 1 };
        ClipCursor(ref rect);
    }

    private static void ReleaseCursorClip() => ClipCursor(IntPtr.Zero);

    private static void Unhook(ref IntPtr hookHandle)
    {
        if (hookHandle == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(hookHandle); } catch { }
        finally { hookHandle = IntPtr.Zero; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InputLock));
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint vkCode; public uint scanCode; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct { public Point pt; public uint mouseData; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // ---- message pump for the hook thread -------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr lpRect);
}
