using System.Drawing;
using System.Runtime.InteropServices;

namespace Stramp.Integrations.Windows;

/// <summary>
/// Adds previous / play-pause / next buttons to the window's taskbar thumbnail preview (the popup
/// Windows shows when you hover the taskbar icon), via ITaskbarList3's thumb-bar API.
///
/// Everything here is best-effort: if the COM calls, icon creation, or the message hook fail we
/// simply end up without thumb buttons rather than taking the app down.
/// </summary>
public sealed class ThumbnailToolbar : IDisposable
{
    public const int ButtonIdPrevious = 1001;
    public const int ButtonIdPlayPause = 1002;
    public const int ButtonIdNext = 1003;

    private const int ThumbButtonCount = 3;

    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private readonly List<IntPtr> _icons = [];
    private IntPtr _playIcon;
    private IntPtr _pauseIcon;
    private bool _initialized;
    private bool _showingPause;

    /// <summary>Must be called after the window handle exists. Safe to call once.</summary>
    public bool TryInitialize(IntPtr hwnd)
    {
        if (_initialized || hwnd == IntPtr.Zero)
            return false;

        try
        {
            _hwnd = hwnd;
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();

            var prevIcon = CreateIcon(DrawPrevious);
            _playIcon = CreateIcon(DrawPlay);
            _pauseIcon = CreateIcon(DrawPause);
            var nextIcon = CreateIcon(DrawNext);
            _icons.AddRange([prevIcon, _playIcon, _pauseIcon, nextIcon]);

            var buttons = new ThumbButton[ThumbButtonCount];
            buttons[0] = MakeButton(ButtonIdPrevious, prevIcon, "Previous");
            buttons[1] = MakeButton(ButtonIdPlayPause, _playIcon, "Play/Pause");
            buttons[2] = MakeButton(ButtonIdNext, nextIcon, "Next");

            var hr = _taskbar.ThumbBarAddButtons(_hwnd, ThumbButtonCount, buttons);
            _initialized = hr == 0;
            return _initialized;
        }
        catch
        {
            _initialized = false;
            return false;
        }
    }

    /// <summary>Swaps the middle button between play and pause glyphs.</summary>
    public void SetPlaying(bool isPlaying)
    {
        if (!_initialized || _taskbar is null || isPlaying == _showingPause)
            return;

        try
        {
            var button = MakeButton(ButtonIdPlayPause, isPlaying ? _pauseIcon : _playIcon,
                isPlaying ? "Pause" : "Play");
            _taskbar.ThumbBarUpdateButtons(_hwnd, 1, [button]);
            _showingPause = isPlaying;
        }
        catch
        {
            // Leave the previous glyph in place.
        }
    }

    private static ThumbButton MakeButton(int id, IntPtr icon, string tip) => new()
    {
        Mask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.Flags,
        Id = (uint)id,
        Icon = icon,
        Tip = tip,
        Flags = ThumbButtonFlags.Enabled,
    };

    // ── Icon drawing ─────────────────────────────────────────────────────────

    private static IntPtr CreateIcon(Action<Graphics> draw)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            draw(g);
        }
        return bitmap.GetHicon();
    }

    private static void DrawPlay(Graphics g) =>
        g.FillPolygon(Brushes.White, [new PointF(10, 7), new PointF(25, 16), new PointF(10, 25)]);

    private static void DrawPause(Graphics g)
    {
        g.FillRectangle(Brushes.White, 10, 7, 4, 18);
        g.FillRectangle(Brushes.White, 18, 7, 4, 18);
    }

    private static void DrawPrevious(Graphics g)
    {
        g.FillRectangle(Brushes.White, 8, 7, 3, 18);
        g.FillPolygon(Brushes.White, [new PointF(24, 7), new PointF(24, 25), new PointF(12, 16)]);
    }

    private static void DrawNext(Graphics g)
    {
        g.FillPolygon(Brushes.White, [new PointF(8, 7), new PointF(20, 16), new PointF(8, 25)]);
        g.FillRectangle(Brushes.White, 21, 7, 3, 18);
    }

    public void Dispose()
    {
        foreach (var icon in _icons)
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
        }
        _icons.Clear();

        if (_taskbar is not null)
        {
            Marshal.ReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ── COM interop ──────────────────────────────────────────────────────────

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int tbpFlags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);

        [PreserveSig]
        int ThumbBarAddButtons(IntPtr hwnd, uint cButtons,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = ThumbButtonCount)] ThumbButton[] pButtons);

        [PreserveSig]
        int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons,
            [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] pButtons);

        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public ThumbButtonMask Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Tip;

        public ThumbButtonFlags Flags;
    }

    [Flags]
    private enum ThumbButtonMask
    {
        Bitmap = 0x1,
        Icon = 0x2,
        Tooltip = 0x4,
        Flags = 0x8,
    }

    [Flags]
    private enum ThumbButtonFlags
    {
        Enabled = 0x0,
        Disabled = 0x1,
        DismissOnClick = 0x2,
        NoBackground = 0x4,
        Hidden = 0x8,
        NonInteractive = 0x10,
    }
}
