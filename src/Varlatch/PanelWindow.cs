using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VarlatchKit;
using Windows.Graphics;

namespace Varlatch;

/// <summary>
/// The panel's window: borderless, above the tray icon, out of Alt+Tab and
/// the taskbar, closing when it loses focus, the way Windows' own flyouts
/// behave.
/// </summary>
internal sealed class PanelWindow : Window
{
    private readonly AppController app;
    private readonly AppWindow appWindow;
    private readonly IntPtr hwnd;
    private readonly PanelView view;
    private readonly Microsoft.UI.Xaml.Controls.ContentControl frame;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer tick;
    private PixelRect? anchor;
    private long hiddenAt;
    private bool closing;

    public PanelWindow(AppController app)
    {
        this.app = app;
        Title = "Varlatch";
        view = new PanelView(app);
        // Holds the focus when the panel opens, without a focus ring, so
        // none of the buttons looks chosen; Tab moves into them.
        frame = new Microsoft.UI.Xaml.Controls.ContentControl
        {
            Content = view.Root,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        frame.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
        Content = frame;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        appWindow = AppWindow;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Varlatch.ico"));
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        Activated += (_, e) =>
        {
            DebugHooks.Record("panel", e.WindowActivationState.ToString());
            if (e.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };
        // Alt+F4 closes it like a click elsewhere; only quitting ends it.
        appWindow.Closing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            Hide();
        };
        app.Changed += () =>
        {
            if (IsOpen) Render();
        };
        view.Root.SizeChanged += (_, _) =>
        {
            if (IsOpen) Fit();
        };
        // Countdowns move on their own between reads.
        tick = DispatcherQueue.CreateTimer();
        tick.Interval = TimeSpan.FromSeconds(10);
        tick.Tick += (_, _) => Render();
    }

    public bool IsOpen => appWindow.IsVisible;

    /// <summary>Hidden a moment ago, by the click on the tray icon that would otherwise open it again.</summary>
    public bool JustClosed => Environment.TickCount64 - hiddenAt < 500;

    public string PanelBounds => IsOpen ? $"{appWindow.Position.X},{appWindow.Position.Y} {appWindow.Size.Width}x{appWindow.Size.Height}" : "";

    public void ShowAt(PixelRect? iconBounds)
    {
        anchor = iconBounds;
        view.Render();
        Fit();
        appWindow.Show(true);
        Activate();
        Native.SetForegroundWindow(hwnd);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!view.FocusForm()) frame.Focus(FocusState.Programmatic);
        });
        tick.Start();
    }

    public void Hide()
    {
        if (!IsOpen) return;
        appWindow.Hide();
        hiddenAt = Environment.TickCount64;
        tick.Stop();
        view.Closed();
    }

    public new void Close()
    {
        closing = true;
        base.Close();
    }

    private void Render()
    {
        view.Render();
        Fit();
    }

    /// <summary>Sizes the window to its content and puts it by the tray icon, on the screen the icon is on.</summary>
    private void Fit()
    {
        var (monitor, workArea, dpi) = Screen();
        var scale = dpi / 96.0;
        view.Root.Measure(new Windows.Foundation.Size(PanelView.Width, double.PositiveInfinity));
        var width = (int)Math.Ceiling(PanelView.Width * scale);
        var height = (int)Math.Ceiling(view.Root.DesiredSize.Height * scale);
        var gap = (int)Math.Round(12 * scale);
        height = Math.Min(height, workArea.Height - 2 * gap);
        var frame = new SizeInt32(appWindow.Size.Width - appWindow.ClientSize.Width, appWindow.Size.Height - appWindow.ClientSize.Height);
        var placed = FlyoutPlacement.Place(anchor, monitor, workArea, width + frame.Width, height + frame.Height, gap);
        var target = new RectInt32(placed.Left, placed.Top, placed.Width, placed.Height);
        if (appWindow.Position.X != target.X || appWindow.Position.Y != target.Y
            || appWindow.Size.Width != target.Width || appWindow.Size.Height != target.Height)
        {
            appWindow.MoveAndResize(target);
        }
    }

    /// <summary>The monitor and work area of the tray icon (or the pointer), and that monitor's DPI.</summary>
    private (PixelRect Monitor, PixelRect WorkArea, uint Dpi) Screen()
    {
        IntPtr monitor;
        if (anchor is { } a)
        {
            var rect = new Native.RECT { Left = a.Left, Top = a.Top, Right = a.Right, Bottom = a.Bottom };
            monitor = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            Native.GetCursorPos(out var point);
            monitor = Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST);
        }
        var info = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var dpi = Native.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : Native.GetDpiForWindow(hwnd);
        return (new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                dpi == 0 ? 96 : dpi);
    }
}
