using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace OpenVocare.Views;

public partial class TrayMenuWindow : Window
{
    private readonly Action _showSettings;
    private readonly Action _toggleDictation;
    private readonly Action _quit;
    private bool _closeStarted;

    public TrayMenuWindow(Action showSettings, Action toggleDictation, Action quit)
    {
        _showSettings = showSettings;
        _toggleDictation = toggleDictation;
        _quit = quit;
        InitializeComponent();
    }

    public void ShowNearCursor()
    {
        Left = -10000;
        Top = -10000;
        Show();

        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget is null)
        {
            return;
        }

        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        System.Drawing.Point cursor = Forms.Cursor.Position;
        Forms.Screen screen = Forms.Screen.FromPoint(cursor);
        System.Windows.Point cursorDip = fromDevice.Transform(new System.Windows.Point(cursor.X, cursor.Y));
        System.Windows.Point workTopLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        System.Windows.Point workBottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

        Left = Math.Clamp(cursorDip.X - ActualWidth + 12, workTopLeft.X + 6, workBottomRight.X - ActualWidth - 6);
        Top = Math.Clamp(cursorDip.Y - ActualHeight - 8, workTopLeft.Y + 6, workBottomRight.Y - ActualHeight - 6);
        Activate();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_showSettings);
    private void ToggleDictation_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_toggleDictation);
    private void Quit_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_quit);

    private void InvokeAndClose(Action action)
    {
        Dismiss();
        action();
    }

    public void Dismiss()
    {
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        Deactivated -= Window_Deactivated;
        Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => Dismiss();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
        }
    }
}
