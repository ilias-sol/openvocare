using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenVocare.Views;

public partial class OverlayWindow : Window
{
    private bool _followingCursor;
    private IntPtr _windowHandle;
    private Thread? _followThread;
    private CancellationTokenSource? _followCancellation;
    private Storyboard? _activeAnimation;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OverlayWindow_SourceInitialized;
        Closed += (_, _) => StopFollowingCursor();
    }

    public void SetStatus(string text)
    {
        bool listening = string.Equals(text, "Listening", StringComparison.OrdinalIgnoreCase);
        RecordingWave.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        TranscribingDots.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;
        Storyboard nextAnimation = (Storyboard)FindResource(listening ? "RecordingAnimation" : "TranscribingAnimation");
        if (!ReferenceEquals(_activeAnimation, nextAnimation))
        {
            _activeAnimation?.Stop(this);
            nextAnimation.Begin(this, true);
            _activeAnimation = nextAnimation;
        }
        ToolTip = text;
        if (!IsVisible)
        {
            Show();
        }
        UpdateCursorPosition();
        StartFollowingCursor();
    }

    public void HideOverlay()
    {
        StopFollowingCursor();
        _activeAnimation?.Stop(this);
        _activeAnimation = null;
        Hide();
    }

    private void StartFollowingCursor()
    {
        if (_followingCursor)
        {
            return;
        }

        _followingCursor = true;
        CancellationTokenSource cancellation = new();
        _followCancellation = cancellation;
        _followThread = new Thread(() => CursorFollowerLoop(cancellation.Token))
        {
            IsBackground = true,
            Name = "OpenVocare cursor indicator"
        };
        _followThread.Start();
    }

    private void StopFollowingCursor()
    {
        if (!_followingCursor)
        {
            return;
        }

        _followingCursor = false;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _followCancellation, null);
        cancellation?.Cancel();
        bool stopped = _followThread?.Join(TimeSpan.FromMilliseconds(100)) ?? true;
        _followThread = null;
        if (stopped)
        {
            cancellation?.Dispose();
        }
    }

    private void CursorFollowerLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UpdateCursorPosition();
            if (Native.DwmFlush() != 0)
            {
                cancellationToken.WaitHandle.WaitOne(4);
            }
        }
    }

    private void UpdateCursorPosition()
    {
        if (!Native.GetCursorPos(out Native.Point point))
        {
            return;
        }

        double targetX = point.X + 16;
        double targetY = point.Y + 16;
        int x = (int)Math.Round(targetX);
        int y = (int)Math.Round(targetY);
        if (_windowHandle != IntPtr.Zero)
        {
            Native.SetWindowPos(_windowHandle, IntPtr.Zero, x, y, 0, 0,
                Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
            return;
        }

        Left = x;
        Top = y;
    }

    private void OverlayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        int extendedStyle = Native.GetWindowLong(_windowHandle, Native.GwlExStyle);
        Marshal.SetLastPInvokeError(0);
        int previousStyle = Native.SetWindowLong(
            _windowHandle,
            Native.GwlExStyle,
            extendedStyle | Native.WsExTransparent | Native.WsExNoActivate | Native.WsExToolWindow);
        if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            Debug.WriteLine("OpenVocare could not make the listening indicator click-through.");
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X; public int Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpNoOwnerZOrder = 0x0200;
        public const int GwlExStyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr window, int index, int newStyle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
    }
}
