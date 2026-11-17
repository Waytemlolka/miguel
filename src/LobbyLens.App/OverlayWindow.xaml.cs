using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LobbyLens.App;

/// <summary>
/// Small click-through widget at the top center of the primary screen, above the game.
/// Works with Dota in "Borderless window" mode (exclusive fullscreen hides any overlay).
/// </summary>
public partial class OverlayWindow : Window
{
    readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => Reposition();
        _topmost.Tick += (_, _) => KeepOnTop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _topmost.Start(); else _topmost.Stop(); };
    }

    public void SetText(string line1, string line2, Color accent)
    {
        Line1.Text = line1;
        Line2.Text = line2;
        Line2.Visibility = string.IsNullOrEmpty(line2) ? Visibility.Collapsed : Visibility.Visible;
        Frame.BorderBrush = new SolidColorBrush(accent);
        Line1.Foreground = new SolidColorBrush(accent);
    }

    void Reposition()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 6;
    }

    void MakeClickThrough()
    {
        var h = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Reposition();
    }

    void KeepOnTop()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
