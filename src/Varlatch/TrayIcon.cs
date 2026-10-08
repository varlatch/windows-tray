using System.Runtime.InteropServices;
using Microsoft.Win32;
using VarlatchKit;

namespace Varlatch;

/// <summary>
/// The Varlatch icon in the notification area, owned by a hidden window that
/// gets its clicks. It comes back by itself when Explorer restarts, and
/// redraws itself when the taskbar turns light or dark.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = Native.WM_APP + 1;
    private const string WindowClass = "VarlatchTrayWindow";

    private readonly Native.WndProc wndProc;
    private readonly uint taskbarCreated;
    private IntPtr hwnd;
    private IntPtr icon;
    private bool added;
    private OverallState state = OverallState.Loading;
    private string toolTip = "Varlatch";

    /// <summary>A left click, or Enter on the icon. The point is where the icon is.</summary>
    public event Action? Clicked;

    /// <summary>A right click, or the menu key on the icon, at a point in screen pixels.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>Windows is ending the session.</summary>
    public event Action? SessionEnding;

    public TrayIcon()
    {
        wndProc = WindowProc;
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        var instance = Native.GetModuleHandle(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        Native.RegisterClassEx(ref wc);
        // A hidden top-level window rather than a message-only one: only
        // top-level windows hear that the taskbar was created again.
        hwnd = Native.CreateWindowEx(0, WindowClass, "Varlatch", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0,
                                     IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    public IntPtr Handle => hwnd;

    public bool IsShown => added;

    /// <summary>Shows the icon, or takes it out of the notification area.</summary>
    public void SetShown(bool shown)
    {
        if (shown && !added) Add();
        else if (!shown && added) Remove();
    }

    public void Update(OverallState newState, string newToolTip)
    {
        var redraw = newState != state;
        state = newState;
        toolTip = newToolTip;
        if (!added) return;
        if (redraw) Render();
        Notify(Native.NIM_MODIFY, Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP | Native.NIF_MESSAGE);
    }

    /// <summary>Where the icon is, in screen pixels; null while it is in the overflow menu or not shown.</summary>
    public PixelRect? Bounds
    {
        get
        {
            if (!added) return null;
            var id = new Native.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
                hWnd = hwnd,
                uID = IconId,
            };
            if (Native.Shell_NotifyIconGetRect(ref id, out var r) != 0) return null;
            var rect = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            return rect.IsEmpty ? null : rect;
        }
    }

    /// <summary>
    /// Shows a menu at <paramref name="x"/>, <paramref name="y"/> and returns
    /// the chosen item's index, or -1. Separators are null.
    /// </summary>
    public int ShowMenu(int x, int y, IReadOnlyList<(string Text, bool Enabled)?> items, int defaultIndex)
    {
        var menu = Native.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is { } item)
                    Native.AppendMenu(menu, Native.MF_STRING | (item.Enabled ? 0 : Native.MF_GRAYED), (nuint)(i + 1), item.Text);
                else
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
            }
            if (defaultIndex >= 0) Native.SetMenuDefaultItem(menu, (uint)(defaultIndex + 1), 0);
            // Without this, the menu would not close when clicking elsewhere.
            Native.SetForegroundWindow(hwnd);
            var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
                                                 x, y, hwnd, IntPtr.Zero);
            Native.PostMessage(hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return (int)chosen - 1;
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private void Add()
    {
        Render();
        added = Notify(Native.NIM_ADD, Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (!added) return;
        var data = Data(0);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private void Remove()
    {
        var data = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        added = false;
    }

    private bool Notify(uint message, uint flags)
    {
        var data = Data(flags);
        return Native.Shell_NotifyIcon(message, ref data);
    }

    private Native.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        // The notification area shows 127 characters at most.
        szTip = toolTip.Length > 127 ? toolTip[..126] + "…" : toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Draws the icon at the size and in the color the taskbar needs now.</summary>
    private void Render()
    {
        var dpi = Native.GetDpiForWindow(hwnd);
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi == 0 ? 96 : dpi);
        var next = TrayIconRenderer.Render(state, size, LightTaskbar());
        var old = icon;
        icon = next;
        if (old != IntPtr.Zero)
        {
            // After the notification area has the new one.
            var data = Data(Native.NIF_ICON);
            if (added) Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
            Native.DestroyIcon(old);
        }
    }

    /// <summary>The taskbar follows "Choose your default Windows mode", not the apps' mode.</summary>
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light == 1;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                var low = (int)(lParam.ToInt64() & 0xFFFF);
                switch (low)
                {
                    case Native.NIN_SELECT:
                    case Native.NIN_KEYSELECT:
                        Clicked?.Invoke();
                        break;
                    case Native.WM_CONTEXTMENU:
                        var x = (short)(wParam.ToInt64() & 0xFFFF);
                        var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        MenuRequested?.Invoke(x, y);
                        break;
                }
                return IntPtr.Zero;
            }
            if (message == taskbarCreated && taskbarCreated != 0)
            {
                // Explorer restarted: the icon is gone and must be added again.
                if (added)
                {
                    added = false;
                    Add();
                }
                return IntPtr.Zero;
            }
            switch (message)
            {
                case Native.WM_SETTINGCHANGE:
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_DPICHANGED:
                    if (added) Render();
                    break;
                case Native.WM_QUERYENDSESSION:
                    return 1;
                case Native.WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) SessionEnding?.Invoke();
                    return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            Log.Write("tray: " + e);
        }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (added) Remove();
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        icon = IntPtr.Zero;
        if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
        hwnd = IntPtr.Zero;
    }
}
