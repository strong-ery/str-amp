using System.Runtime.InteropServices;

namespace Stramp.Integrations.Windows;

/// <summary>
/// Pins DPI-matched large and small icons directly to a Win32 window. This prevents the taskbar
/// from intermittently falling back to the generic window icon while Avalonia initializes.
/// </summary>
public sealed class WindowsWindowIcon : IDisposable
{
    private const uint WmSetIcon = 0x0080;
    private const uint WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;

    private IntPtr _largeIcon;
    private IntPtr _smallIcon;

    public bool TryApply(IntPtr hwnd, string iconPath)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero || !File.Exists(iconPath))
            return false;

        try
        {
            DisposeIcons();

            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
                dpi = 96;

            var largeWidth = GetSystemMetricsForDpi(SmCxIcon, dpi);
            var largeHeight = GetSystemMetricsForDpi(SmCyIcon, dpi);
            var smallWidth = GetSystemMetricsForDpi(SmCxSmallIcon, dpi);
            var smallHeight = GetSystemMetricsForDpi(SmCySmallIcon, dpi);

            _largeIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon,
                largeWidth, largeHeight, LoadFromFile);
            _smallIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon,
                smallWidth, smallHeight, LoadFromFile);

            if (_largeIcon == IntPtr.Zero || _smallIcon == IntPtr.Zero)
            {
                DisposeIcons();
                return false;
            }

            SendMessage(hwnd, WmSetIcon, (IntPtr)IconBig, _largeIcon);
            SendMessage(hwnd, WmSetIcon, (IntPtr)IconSmall, _smallIcon);
            SendMessage(hwnd, WmSetIcon, (IntPtr)IconSmall2, _smallIcon);
            return true;
        }
        catch
        {
            DisposeIcons();
            return false;
        }
    }

    /// <summary>Answers later WM_GETICON queries from Explorer using the retained icon handles.</summary>
    public bool TryHandleMessage(uint message, IntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message != WmGetIcon)
            return false;

        result = wParam.ToInt32() == IconBig ? _largeIcon : _smallIcon;
        return result != IntPtr.Zero;
    }

    public void Dispose()
    {
        DisposeIcons();
        GC.SuppressFinalize(this);
    }

    private void DisposeIcons()
    {
        if (_largeIcon != IntPtr.Zero)
        {
            DestroyIcon(_largeIcon);
            _largeIcon = IntPtr.Zero;
        }

        if (_smallIcon != IntPtr.Zero)
        {
            DestroyIcon(_smallIcon);
            _smallIcon = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance, string name, uint type, int desiredWidth, int desiredHeight, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
