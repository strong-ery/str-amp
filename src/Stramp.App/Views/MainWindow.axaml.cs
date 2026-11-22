using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stramp.App.Services;
using Stramp.App.ViewModels;
using Stramp.Core.Dsp;
#if STRAMP_WINDOWS
using Stramp.Integrations.Windows;
#endif

namespace Stramp.App.Views;

public partial class MainWindow : Window
{
    private const int SpectrumBins = 160;

    // Idle motion: a slow sway that keeps the cover alive between beats. Deliberately small so
    // beat hits stay the dominant motion rather than competing with a constant drift.
    private const double DriftPixels = 4.5;
    private const double MaxRotationDegrees = 0.9;

    // Beat response. Hits snap to full size in one frame then ease out over ~0.2s.
    private const double KickStretch = 0.17;
    private const double HihatStretch = 0.13;
    private const double KickSquash = 0.07;
    private const double KickShiftPixels = 7;
    private const double PulseDecayPerTick = 0.86;

    /// <summary>Seconds the auto-scroll keeps out of the way after a scroll by hand (~4s).</summary>
    private const double LyricsManualScrollDurationSeconds = 4.0;

    /// <summary>Share of the panel left blank top and bottom, so the end lines can still centre.</summary>
    private const double LyricsEdgePaddingFraction = 0.42;

    /// <summary>Type size as a share of the panel's width, clamped to stay readable either way.</summary>
    private const double LyricsFontScale = 0.085;
    private const double MinLyricsFontSize = 16;
    private const double MaxLyricsFontSize = 30;

    /// <summary>Distance from the right edge that reveals the otherwise hidden lyric scrollbar.</summary>
    private const double LyricsScrollbarRevealDistance = 44;

    /// <summary>Volume change per mouse wheel notch when scrolling over the volume slider.</summary>
    private const double VolumeScrollStep = 5;

    private readonly BeatDetector _kickDetector = new(sensitivity: 1.9, refractorySeconds: 7.0 / 60.0);
    private readonly BeatDetector _hihatDetector = new(sensitivity: 2.2, refractorySeconds: 3.0 / 60.0);

    private readonly ScaleTransform _artScale = new();
    private readonly RotateTransform _artRotate = new();
    private readonly TranslateTransform _artTranslate = new();

    private double _driftPhase;
    private float _kickPulse;
    private float _hihatPulse;

    private double _lyricsEdgePadding;

    private bool _isDraggingSplitter;
    private readonly ColumnResizeAnimator _columnAnimator = new();
    private readonly ColumnResizeAnimator _windowGrowAnimator = new();
    private readonly LyricsScrollAnimator _lyricsAnimator = new();
    private readonly SmoothScrollAnimator _lyricsManualScroll = new();

    private bool _isFrameLoopRunning;
    private TimeSpan? _lastFrameTimestamp;

#if STRAMP_WINDOWS
    private ThumbnailToolbar? _thumbnailToolbar;
    private WindowsWindowIcon? _windowIcon;
#else
    private LinuxNowPlayingHost? _nowPlaying;
#endif
    private readonly MediaKeyController _mediaKeys;
    private MainWindowViewModel? _observedViewModel;
    private ThemeSettingsViewModel? _observedTheme;
    private double _lastStageShortSide;

    public MainWindow()
    {
        InitializeComponent();

        Opened += (_, _) => DisableNativeFullscreenPopover();

        // Transform the whole framed artwork, not the image inside the clipping frame:
        // transforming the inner image just slides the picture around behind a fixed window.
        ArtFrame.RenderTransform = new TransformGroup
        {
            Children = { _artScale, _artRotate, _artTranslate },
        };

        // The Slider handles pointer events internally and marks them handled, so a normal
        // XAML event hookup never sees the press/release that bracket a scrub.
        ProgressSlider.AddHandler(PointerPressedEvent, OnProgressPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnProgressPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, OnProgressCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        AddHandler(KeyDownEvent, OnEditorTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LyricsScroller.AddHandler(PointerWheelChangedEvent, OnLyricsWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        VolumeSlider.AddHandler(PointerWheelChangedEvent, OnVolumeWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        CompactVolumeSlider.AddHandler(PointerWheelChangedEvent, OnVolumeWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        AttachSmoothScrollHandlers();

        WaveformBar.Scrubbing += fraction => ViewModel?.ScrubTo(fraction);
        WaveformBar.ScrubCompleted += fraction => ViewModel?.CompleteScrub(fraction);

        _mediaKeys = new MediaKeyController(
            this,
            () => ViewModel?.PlayPauseCommand.Execute(null),
            () => ViewModel?.NextCommand.Execute(null),
            () => ViewModel?.PreviousCommand.Execute(null),
            () => ViewModel?.Volume ?? 0,
            volume =>
            {
                if (ViewModel is { } vm)
                    vm.Volume = volume;
            },
            // The lyrics editor uses Space and the arrow keys itself.
            () => ViewModel is { IsLyricsEditorOpen: true, IsEditorSyncStep: true });

        LeftSplitter.DragStarted += (_, _) => _isDraggingSplitter = true;
        LeftSplitter.DragDelta += OnLeftSplitterDragDelta;
        LeftSplitter.DragCompleted += (sender, e) =>
        {
            _isDraggingSplitter = false;
            OnLeftSplitterDragCompleted(sender, e);
        };
        RightSplitter.DragStarted += (_, _) => _isDraggingSplitter = true;
        RightSplitter.DragDelta += OnRightSplitterDragDelta;
        RightSplitter.DragCompleted += (sender, e) =>
        {
            _isDraggingSplitter = false;
            OnRightSplitterDragCompleted(sender, e);
        };

        AddHandler(DragDrop.DropEvent, OnFileDrop);

        Opened += (_, _) =>
        {
            StartFrameLoop();
            SetUpPlatformIntegration();
            UpdateColumnWidths(animate: false);
            UpdateLyricsSplit(animate: false);
        };
        SizeChanged += (_, _) => UpdateColumnWidths(animate: false);
        Closed += (_, _) =>
        {
            StopFrameLoop();
#if STRAMP_WINDOWS
            _thumbnailToolbar?.Dispose();
            _windowIcon?.Dispose();
#else
            _nowPlaying?.Dispose();
#endif
            _mediaKeys.Dispose();
            ViewModel?.Dispose();
        };
        PropertyChanged += OnWindowPropertyChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            var paths = new List<string>();
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path != null && File.Exists(path))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count > 0 && ViewModel is { } vm)
            {
                vm.PlayDroppedFiles(paths);
            }
        }
    }

    private void StartFrameLoop()
    {
        if (_isFrameLoopRunning)
            return;

        _isFrameLoopRunning = true;
        _lastFrameTimestamp = null;
        RequestAnimationFrame(OnAnimationFrame);
    }

    private void StopFrameLoop()
    {
        _isFrameLoopRunning = false;
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (!_isFrameLoopRunning)
            return;

        var dt = _lastFrameTimestamp is { } last
            ? (timestamp - last).TotalSeconds
            : 1.0 / 60.0;
        _lastFrameTimestamp = timestamp;

        // Guard against spikes from suspension, modal dialogs, or window dragging
        dt = Math.Clamp(dt, 0.0001, 0.1);

        OnFrameTick(dt);

        if (_isFrameLoopRunning)
            RequestAnimationFrame(OnAnimationFrame);
    }

    // ── Taskbar thumbnail buttons (Windows only) ─────────────────────────────

#if !STRAMP_WINDOWS
    private void SetUpPlatformIntegration()
    {
        _nowPlaying ??= new LinuxNowPlayingHost(this);
        _nowPlaying.Attach(ViewModel);
    }
#else
    private void SetUpPlatformIntegration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        _windowIcon = new WindowsWindowIcon();
        _windowIcon.TryApply(handle.Value,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Stramp.ico"));

        _thumbnailToolbar = new ThumbnailToolbar();
        _thumbnailToolbar.TryInitialize(handle.Value);
        Win32Properties.AddWndProcHookCallback(this, OnWndProc);

        _mediaKeys.AttachWindowsMediaKeys(handle.Value, ViewModel?.IsPlaying ?? false);
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint wmCommand = 0x0111;
        const int thbnClicked = 0x1800;

        if (_windowIcon?.TryHandleMessage(msg, wParam, out var icon) == true)
        {
            handled = true;
            return icon;
        }

        if (msg == wmCommand && (wParam.ToInt64() >> 16 & 0xFFFF) == thbnClicked)
        {
            var buttonId = (int)(wParam.ToInt64() & 0xFFFF);
            var vm = ViewModel;

            switch (buttonId)
            {
                case ThumbnailToolbar.ButtonIdPrevious:
                    vm?.PreviousCommand.Execute(null);
                    break;
                case ThumbnailToolbar.ButtonIdPlayPause:
                    vm?.PlayPauseCommand.Execute(null);
                    break;
                case ThumbnailToolbar.ButtonIdNext:
                    vm?.NextCommand.Execute(null);
                    break;
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

#endif

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.LyricLines.CollectionChanged -= OnLyricLinesChanged;
        }

        if (_observedTheme is not null)
        {
            _observedTheme.PropertyChanged -= OnThemePropertyChanged;
        }

        _observedViewModel = ViewModel;
        if (_observedViewModel is null)
            return;

        _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _observedViewModel.VisualizerFeed.WaveformReady += OnWaveformReady;
        _observedViewModel.LyricLines.CollectionChanged += OnLyricLinesChanged;

        _observedTheme = _observedViewModel.Theme;
        if (_observedTheme is not null)
            _observedTheme.PropertyChanged += OnThemePropertyChanged;

        if (_isImmersiveFullScreen)
        {
            _savedIsLibraryOpen = _observedViewModel.IsLibraryOpen;
            _savedIsUpNextOpen = _observedViewModel.IsUpNextOpen;
            _observedViewModel.IsLibraryOpen = false;
            _observedViewModel.IsUpNextOpen = false;
        }

        UpdateColumnWidths(animate: false);
        UpdateLyricsSplit(animate: false);
        UpdateStageArtworkAndVisualizer();
    }

    private void OnThemePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeSettingsViewModel.CircularAlbumArt))
        {
            UpdateStageArtworkAndVisualizer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPlaying))
        {
#if STRAMP_WINDOWS
            _thumbnailToolbar?.SetPlaying(ViewModel?.IsPlaying ?? false);
#endif
            _mediaKeys.SetPlaybackState(ViewModel?.IsPlaying ?? false);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLibraryOpen))
        {
            if (ViewModel is { IsLibraryOpen: true } vm)
                GrowWindowIfPanelWouldSquishPlayer(growLeftward: true, vm);
            UpdateColumnWidths(animate: true);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsUpNextOpen))
        {
            if (ViewModel is { IsUpNextOpen: true } vm)
                GrowWindowIfPanelWouldSquishPlayer(growLeftward: false, vm);
            UpdateColumnWidths(animate: true);
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.LeftPanelWidth) or
                 nameof(MainWindowViewModel.RightPanelWidth))
        {
            UpdateColumnWidths(animate: true);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsOpen))
        {
            UpdateLyricsSplit(animate: true);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.EditorPlayingLine))
        {
            // Follow the song while reviewing, but never yank the list away from a line still waiting for its time.
            var line = ViewModel?.EditorPlayingLine;
            if (ViewModel?.EditorSelectedLine is not { IsStamped: false })
                Dispatcher.UIThread.Post(() => ScrollEditorLineIntoView(line), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.EditorSelectedLine))
        {
            var selected = ViewModel?.EditorSelectedLine;
            Dispatcher.UIThread.Post(() => ScrollEditorLineIntoView(selected), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsSynced))
        {
            Dispatcher.UIThread.Post(UpdateLyricsPadding, DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ActiveLyricLine))
        {
            // Posted rather than called: the container for a line that just scrolled into the
            // list may not have been measured yet, and its bounds are what we scroll by.
            Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsRestartingForUpdate) && ViewModel?.IsRestartingForUpdate == true)
        {
            // The update helper is waiting for this process to exit before it can replace these
            // files and relaunch the app itself.
            Close();
        }
    }

    // ── Lyrics ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives the lyrics column half the card, which is what pushes the cover and visualizer into
    /// the left half. Closed, the column collapses to nothing and the stage fills the card again.
    /// </summary>
    private void UpdateLyricsSplit(bool animate = true)
    {
        if (ViewModel is not { } vm || NowPlayingSplit.ColumnDefinitions.Count < 2)
            return;

        var lyricsCol = NowPlayingSplit.ColumnDefinitions[1];
        bool shouldBeOpen = vm.IsLyricsOpen;
        bool disableAnimations = vm.Theme.DisableAnimations || !animate;

        if (disableAnimations)
        {
            StopLyricsAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            return;
        }

        double totalWidth = NowPlayingSplit.Bounds.Width;
        if (totalWidth <= 0)
            totalWidth = NowPlayingCard.Bounds.Width;
        if (totalWidth <= 0)
            totalWidth = 600;

        double targetPixels = shouldBeOpen ? totalWidth * 0.5 : 0;
        double startPixels;

        if (lyricsCol.Width.IsStar)
        {
            startPixels = lyricsCol.Width.Value * (totalWidth * 0.5);
        }
        else if (lyricsCol.Width.IsAbsolute)
        {
            startPixels = lyricsCol.Width.Value;
        }
        else
        {
            startPixels = 0;
        }

        if (Math.Abs(startPixels - targetPixels) < 1)
        {
            StopLyricsAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            return;
        }

        StopLyricsAnimation();
        _lyricsAnimator.StartPanelAnimation(startPixels, targetPixels, shouldBeOpen);
    }

    private bool IsPanelAnimating => _columnAnimator.IsAnimating || _lyricsAnimator.IsPanelAnimating;

    private void StopLyricsAnimation()
    {
        if (_lyricsAnimator.IsPanelAnimating)
        {
            _lyricsAnimator.StopPanelAnimation();
            RefreshLyricsLayoutAfterAnimation();
        }
    }

    private void StepLyricsPanelAnimation()
    {
        if (!_lyricsAnimator.IsPanelAnimating || NowPlayingSplit.ColumnDefinitions.Count < 2)
            return;

        var lyricsCol = NowPlayingSplit.ColumnDefinitions[1];
        var currPixels = _lyricsAnimator.StepPanelAnimation(out var finished, out var shouldBeOpen);

        if (currPixels > 1)
            lyricsCol.Width = new GridLength(currPixels, GridUnitType.Pixel);
        else
            lyricsCol.Width = new GridLength(0);

        if (finished)
        {
            RefreshLyricsLayoutAfterAnimation();
            lyricsCol.Width = shouldBeOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private void RefreshLyricsLayoutAfterAnimation()
    {
        if (LyricsScroller is null || LyricsItems is null)
            return;

        var width = LyricsScroller.Bounds.Width;
        if (width > 0)
        {
            LyricsItems.FontSize = Math.Clamp(
                width * LyricsFontScale, MinLyricsFontSize, MaxLyricsFontSize);
        }
        UpdateLyricsPadding();
        Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
    }

    /// <summary>A new track clears the collection, so the panel starts reading from the top again.</summary>
    private void OnLyricLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
            return;

        LyricsScroller.Offset = default;
        _lyricsAnimator.ClearScrollTarget();
        _lyricsAnimator.ClearManualScrollPause();
    }

    /// <summary>
    /// Type size follows the panel, because the lyrics only ever get half the card and big text in
    /// a narrow column would wrap every couple of words.
    /// </summary>
    private void OnLyricsViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsPanelAnimating)
            return;

        LyricsItems.FontSize = Math.Clamp(
            e.NewSize.Width * LyricsFontScale, MinLyricsFontSize, MaxLyricsFontSize);
        UpdateLyricsPadding();
    }

    /// <summary>
    /// Blank space above the first line and below the last, so they can reach the middle of the
    /// panel like every other line. Only a synced list scrolls itself, so only it gets the padding;
    /// an unsynced sheet would just open on a screenful of nothing.
    /// </summary>
    private void UpdateLyricsPadding()
    {
        var padding = ViewModel?.IsLyricsSynced == true
            ? LyricsScroller.Bounds.Height * LyricsEdgePaddingFraction
            : 0;

        if (Math.Abs(padding - _lyricsEdgePadding) > 0.5)
        {
            _lyricsEdgePadding = padding;
            LyricsItems.Margin = new Thickness(0, padding, 0, padding);
        }

        // Posted: the new padding changes the scrollable extent, which the target is clamped to.
        Dispatcher.UIThread.Post(ScrollActiveLyricIntoView, DispatcherPriority.Background);
    }

    /// <summary>
    /// Aims the panel at the active line. The move itself is eased over the following frames by
    /// StepLyricsScroll rather than applied here, so the list glides instead of snapping.
    /// </summary>
    private void ScrollActiveLyricIntoView()
    {
        if (_lyricsAnimator.IsManualScrollPauseActive || IsPanelAnimating)
            return;

        if (ViewModel is not { IsLyricsSynced: true } vm || vm.ActiveLyricLine is not { } active)
            return;

        var index = vm.LyricLines.IndexOf(active);
        if (index < 0 || LyricsItems.ContainerFromIndex(index) is not { } container)
            return;

        var viewport = LyricsScroller.Viewport.Height;
        if (viewport <= 0)
            return;

        // Bounds are relative to the items panel, which the padding offsets and scrolling never
        // moves, so this target stays correct even while an earlier glide is still running.
        var lineCentre = _lyricsEdgePadding + container.Bounds.Top + container.Bounds.Height / 2;
        var maxOffset = Math.Max(0, LyricsScroller.Extent.Height - viewport);
        _lyricsAnimator.SetScrollTarget(Math.Clamp(lineCentre - viewport / 2, 0, maxOffset));
    }

    /// <summary>Moves the panel a fraction of the way to its target, once per animation frame.</summary>
    private void StepLyricsScroll(double dt)
    {
        if (IsPanelAnimating)
            return;

        // A wheel glide in flight owns the offset this frame; the auto-scroll only gets a turn
        // once it has settled, same as it already waits out the manual-scroll pause below.
        var manualOffset = _lyricsManualScroll.Step(dt, LyricsScroller.Offset.Y);
        if (manualOffset is { } manual)
        {
            LyricsScroller.Offset = LyricsScroller.Offset.WithY(manual);
            return;
        }

        // Scrolling by hand takes over; the panel only takes itself back once the user has stopped.
        if (_lyricsAnimator.TickManualPause(dt))
            ScrollActiveLyricIntoView();

        var newOffset = _lyricsAnimator.EaseTowardTarget(dt, LyricsScroller.Offset.Y);
        if (newOffset is { } offset)
            LyricsScroller.Offset = LyricsScroller.Offset.WithY(offset);
    }

    /// <summary>
    /// A scroll by hand parks the auto-scroll, so the two never fight over the panel, and glides
    /// instead of jumping per notch, same as every other scroll surface in the app.
    /// </summary>
    private void OnLyricsWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        _lyricsAnimator.BeginManualScrollPause(LyricsManualScrollDurationSeconds);
        _lyricsAnimator.ClearScrollTarget();

        var maxOffset = LyricsScroller.Extent.Height - LyricsScroller.Viewport.Height;
        _lyricsManualScroll.HandleWheel(e.Delta.Y, LyricsScroller.Offset.Y, maxOffset);
    }

    private void OnVolumeWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var delta = e.Delta.Y * VolumeScrollStep;
        vm.Volume = Math.Clamp(vm.Volume + delta, 0, 100);
        e.Handled = true;
    }

    private void OnLyricsPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(LyricsScroller);
        var nearScrollbar = point.Position.X >=
            Math.Max(0, LyricsScroller.Bounds.Width - LyricsScrollbarRevealDistance);
        var keepVisibleWhileDragging =
            LyricsScroller.Classes.Contains("scrollbar-near") && point.Properties.IsLeftButtonPressed;
        SetLyricsScrollbarVisible(nearScrollbar || keepVisibleWhileDragging);
    }

    private void OnLyricsPointerExited(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(LyricsScroller).Properties.IsLeftButtonPressed)
            SetLyricsScrollbarVisible(false);
    }

    private void OnLyricsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var position = e.GetPosition(LyricsScroller);
        SetLyricsScrollbarVisible(position.X >=
            Math.Max(0, LyricsScroller.Bounds.Width - LyricsScrollbarRevealDistance));
    }

    private void OnLyricsPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!LyricsScroller.IsPointerOver)
            SetLyricsScrollbarVisible(false);
    }

    private void SetLyricsScrollbarVisible(bool visible) =>
        LyricsScroller.Classes.Set("scrollbar-near", visible);

    /// <summary>Clicking a timed line jumps playback to it; unsynced lines carry no time to jump to.</summary>
    private void OnEditorLineTapped(object? sender, TappedEventArgs e)
    {
        // Clicks inside an open edit box are for the box, not for selecting the line.
        if (e.Source is Visual source && (source is TextBox || source.FindAncestorOfType<TextBox>() is not null))
            return;

        if ((e.Source as StyledElement)?.DataContext is LyricEditorLine line)
            ViewModel?.SelectEditorLine(line);
    }

    /// <summary>Double-click the time to retime a line, anywhere else on it to rewrite its words.</summary>
    private void OnEditorLineDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source is TextBox || source.FindAncestorOfType<TextBox>() is not null)
            return;
        if ((source as StyledElement)?.DataContext is not LyricEditorLine line)
            return;

        var onTime = source is TextBlock { Classes: var classes } && classes.Contains("editor-time");
        ViewModel?.BeginEditingLine(line, onTime);

        e.Handled = true;
        FocusEditorEntry(line);
    }

    /// <summary>Puts the cursor in a line's open edit box once it has been laid out.</summary>
    private void FocusEditorEntry(LyricEditorLine line) =>
        Dispatcher.UIThread.Post(() =>
        {
            var box = LyricsEditorItems.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(candidate => candidate.IsVisible && ReferenceEquals(candidate.DataContext, line));
            if (box is null)
                return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Background);

    private void OnAddEditorLineClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            FocusEditorEntry(vm.AddEditorLine());
    }

    /// <summary>The small − / + on a hovered line: Tag carries the step in seconds.</summary>
    private void OnLineNudgeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LyricEditorLine line, Tag: string step }
            && double.TryParse(step, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            ViewModel?.NudgeEditorLine(line, seconds);
    }

    private void OnLineBreakClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LyricEditorLine line } && ViewModel is { } vm)
        {
            vm.EditorSelectedLine = line;
            vm.AddEditorGap();
        }
    }

    private void OnLineDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LyricEditorLine line })
            ViewModel?.DeleteEditorLine(line);
    }

    private void OnEditorEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: LyricEditorLine line })
            return;

        if (e.Key == Key.Enter)
        {
            line.CommitEdit();
            ViewModel?.EditorLineEdited(line);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            line.CancelEdit();
            ViewModel?.EditorLineEdited(line);
            e.Handled = true;
        }
    }

    private void OnEditorEntryLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: LyricEditorLine line } && (line.IsEditingText || line.IsEditingTime))
        {
            line.CommitEdit();
            ViewModel?.EditorLineEdited(line);
        }
    }

    private void OnLyricTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is LyricLineRow line)
            ViewModel?.SeekToLyricCommand.Execute(line);
    }

    /// <summary>Matches the player column's own MinWidth in MainWindow.axaml.</summary>
    private const double PlayerColumnMinWidth = 300.0;

    private ColumnDefinition? LeftColumn =>
        MainContentGrid?.ColumnDefinitions.Count > 0 ? MainContentGrid.ColumnDefinitions[0] : null;

    private ColumnDefinition? RightColumn =>
        MainContentGrid?.ColumnDefinitions.Count > 4 ? MainContentGrid.ColumnDefinitions[4] : null;

    /// <summary>
    /// Opening a library/up-next panel can otherwise leave the player column squeezed below its
    /// usable minimum - its controls overlapping, its volume slider reduced to nothing - if the
    /// window itself isn't wide enough to fit everything that's now open. Rather than let that
    /// happen, the window grows by whatever's short: from its left edge for the library panel,
    /// its right edge for up next, so whatever was already on screen doesn't shift to make room.
    /// </summary>
    private void GrowWindowIfPanelWouldSquishPlayer(bool growLeftward, MainWindowViewModel vm)
    {
        if (WindowState != WindowState.Normal)
            return;

        const double splitterWidth = 8;

        double leftRequested = vm.IsLibraryOpen ? Math.Max(SidePanelMinWidth, vm.LeftPanelWidth) : 0;
        double rightRequested = vm.IsUpNextOpen ? Math.Max(SidePanelMinWidth, vm.RightPanelWidth) : 0;
        double splittersTotal = (vm.IsLibraryOpen ? splitterWidth : 0) + (vm.IsUpNextOpen ? splitterWidth : 0);

        double availableWidth = Bounds.Width > 0 ? Bounds.Width - 32 : Width - 32;
        double needed = leftRequested + rightRequested + splittersTotal + PlayerColumnMinWidth;
        double shortfall = needed - availableWidth;
        if (shortfall <= 0)
            return;

        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return;

        double maxWidth = screen.WorkingArea.Width / screen.Scaling;
        double newWidth = Math.Min(Width + shortfall, maxWidth);
        double actualGrowth = newWidth - Width;
        if (actualGrowth <= 0)
            return;

        // Both tracked in DIPs (matching Width) so the two glide in step - Position is only
        // converted to device pixels when a stepped frame is actually applied below.
        double startX = Position.X / screen.Scaling;
        double targetX = growLeftward ? startX - actualGrowth : startX;
        _windowGrowAnimator.Start(Width, newWidth, startX, targetX);
    }

    private void StepWindowGrowAnimation()
    {
        if (!_windowGrowAnimator.IsAnimating)
            return;

        var (width, xDip) = _windowGrowAnimator.Step(out _);
        var scaling = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.Scaling ?? 1.0;
        Position = new PixelPoint((int)Math.Round(xDip * scaling), Position.Y);
        Width = width;
    }

    private void UpdateColumnWidths(bool animate = true)
    {
        if (ViewModel is not { } vm || LeftColumn is not { } leftCol || RightColumn is not { } rightCol)
            return;

        var availableWidth = Bounds.Width > 0 ? Bounds.Width - 32 : Width - 32;
        const double minCenterWidth = PlayerColumnMinWidth;
        const double splitterWidth = 8;

        double leftRequested = vm.IsLibraryOpen ? Math.Max(SidePanelMinWidth, vm.LeftPanelWidth) : 0;
        double rightRequested = vm.IsUpNextOpen ? Math.Max(SidePanelMinWidth, vm.RightPanelWidth) : 0;

        double splittersTotal = (vm.IsLibraryOpen ? splitterWidth : 0) + (vm.IsUpNextOpen ? splitterWidth : 0);
        double availableForSides = Math.Max(0, availableWidth - minCenterWidth - splittersTotal);
        double totalRequestedSides = leftRequested + rightRequested;

        double leftTarget = leftRequested;
        double rightTarget = rightRequested;

        if (totalRequestedSides > availableForSides && totalRequestedSides > 0)
        {
            double scale = availableForSides / totalRequestedSides;
            leftTarget *= scale;
            rightTarget *= scale;
        }

        bool disableAnimations = vm.Theme.DisableAnimations || _isDraggingSplitter || !animate;

        if (disableAnimations)
        {
            StopColumnAnimation();
            ApplyLeftColumnWidth(leftTarget);
            ApplyRightColumnWidth(rightTarget);
            return;
        }

        double startLeft = leftCol.Width.IsAbsolute ? leftCol.Width.Value : 0;
        double startRight = rightCol.Width.IsAbsolute ? rightCol.Width.Value : 0;

        if (Math.Abs(startLeft - leftTarget) < 1 && Math.Abs(startRight - rightTarget) < 1)
        {
            StopColumnAnimation();
            ApplyLeftColumnWidth(leftTarget);
            ApplyRightColumnWidth(rightTarget);
            return;
        }

        StopColumnAnimation();
        _columnAnimator.Start(startLeft, leftTarget, startRight, rightTarget);
    }

    private void StopColumnAnimation()
    {
        if (_columnAnimator.IsAnimating)
        {
            _columnAnimator.Stop();
            RefreshLyricsLayoutAfterAnimation();
        }
    }

    private void StepColumnAnimation()
    {
        if (!_columnAnimator.IsAnimating)
            return;

        var (left, right) = _columnAnimator.Step(out var finished);
        ApplyLeftColumnWidth(left);
        ApplyRightColumnWidth(right);

        if (finished)
            RefreshLyricsLayoutAfterAnimation();
    }

    private void ApplyLeftColumnWidth(double width)
    {
        if (LeftColumn is not { } leftCol) return;
        if (width > 5)
        {
            leftCol.Width = new GridLength(width);
            LeftSplitter.IsVisible = true;
        }
        else
        {
            leftCol.Width = new GridLength(0);
            LeftSplitter.IsVisible = false;
        }
    }

    private void ApplyRightColumnWidth(double width)
    {
        if (RightColumn is not { } rightCol) return;
        if (width > 5)
        {
            rightCol.Width = new GridLength(width);
            RightSplitter.IsVisible = true;
        }
        else
        {
            rightCol.Width = new GridLength(0);
            RightSplitter.IsVisible = false;
        }
    }

    /// <summary>
    /// The usable floor for an open library/up-next panel - also used as the Grid's own MinWidth
    /// can't be (their fully-closed state is also Width="0" on that same column, so GridSplitter
    /// has nothing else stopping it from dragging one down to nothing), which is why this is
    /// applied by hand in the splitters' own drag handlers below instead. Matching against the
    /// width's own current value there (rather than just checking IsAbsolute) would have let a
    /// drag that lands exactly on 0 skip the clamp outright - the one case it exists to catch.
    /// </summary>
    private const double SidePanelMinWidth = 275.0;

    private void OnLeftSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is not { } vm || LeftColumn is not { Width: { IsAbsolute: true } w } col)
            return;

        var clamped = Math.Max(SidePanelMinWidth, w.Value);
        if (Math.Abs(clamped - w.Value) > 0.1)
            col.Width = new GridLength(clamped);

        vm.LeftPanelWidth = clamped;
    }

    private void OnLeftSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel is not { } vm || LeftColumn is not { Width: { IsAbsolute: true } w } col)
            return;

        var clamped = Math.Max(SidePanelMinWidth, w.Value);
        col.Width = new GridLength(clamped);
        vm.LeftPanelWidth = clamped;
    }

    private void OnRightSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is not { } vm || RightColumn is not { Width: { IsAbsolute: true } w } col)
            return;

        var clamped = Math.Max(SidePanelMinWidth, w.Value);
        if (Math.Abs(clamped - w.Value) > 0.1)
            col.Width = new GridLength(clamped);

        vm.RightPanelWidth = clamped;
    }

    private void OnRightSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel is not { } vm || RightColumn is not { Width: { IsAbsolute: true } w } col)
            return;

        var clamped = Math.Max(SidePanelMinWidth, w.Value);
        col.Width = new GridLength(clamped);
        vm.RightPanelWidth = clamped;
    }

    private void OnWaveformReady()
    {
        WaveformBar.SetWaveform(ViewModel?.VisualizerFeed.Waveform);
    }

    /// <summary>Keeps a line of the editor's list in view as it becomes the one to watch.</summary>
    private void ScrollEditorLineIntoView(LyricEditorLine? line)
    {
        if (line is null || ViewModel is null)
            return;

        var index = ViewModel.EditorLines.IndexOf(line);
        if (index >= 0 && LyricsEditorItems.ContainerFromIndex(index) is Control container)
            container.BringIntoView();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ── Window chrome ────────────────────────────────────────────────────────

    private WindowState _previousWindowState = WindowState.Normal;
    private bool _savedIsLibraryOpen;
    private bool _savedIsUpNextOpen;
    private bool _isImmersiveFullScreen;

    /// <summary>
    /// Avalonia's own window chrome (active because of ExtendClientAreaToDecorationsHint) draws
    /// a built-in "fullscreen popover" on hover at the top edge - its own little titlebar with
    /// its own close/restore buttons, meant as an escape hatch for apps with no other way out of
    /// fullscreen. Stramp already has one (F11/Escape/the stramp menu). Neither the popover nor
    /// the chrome object holding it (Window.TopLevelHost.Decorations) is public API, so this
    /// reaches it the only way available - two non-public property reads - then clears it
    /// through its own public, writable FullscreenPopover property. Pinned to Avalonia 12.1.2's
    /// internal layout; worth re-checking on an Avalonia upgrade.
    /// </summary>
    private void DisableNativeFullscreenPopover()
    {
        const BindingFlags nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        var topLevelHost = typeof(Window).GetProperty("TopLevelHost", nonPublicInstance)?.GetValue(this);
        if (topLevelHost is null)
            return;

        if (topLevelHost.GetType().GetProperty("Decorations", nonPublicInstance)?.GetValue(topLevelHost)
            is not WindowDrawnDecorations decorations)
        {
            return;
        }

        if (decorations.Content is { } content)
            content.FullscreenPopover = null;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximized();

    private void OnMinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximized();

    private void OnToggleFullScreenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleFullScreen();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ToggleMaximized()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = WindowState.Maximized;
        else
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;
        }
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    /// <summary>
    /// The editor's keys, taken on the way down so a focused button or list can't swallow them.
    /// Typing in a text box is left alone.
    /// </summary>
    private void OnEditorTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { IsLyricsEditorOpen: true, IsEditorSyncStep: true } vm
            || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return;

        if (ctrl)
        {
            if (e.Key == Key.Z && !shift)
                vm.UndoEditor();
            else if (e.Key == Key.Y || (e.Key == Key.Z && shift))
                vm.RedoEditor();
            else
                return;

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space: vm.PlayPauseCommand.Execute(null); break;
            case Key.Up: vm.MoveEditorSelection(-1); break;
            case Key.Down: vm.MoveEditorSelection(1); break;
            case Key.Left: vm.NudgeSelectedBy(-1, shift); break;
            case Key.Right: vm.NudgeSelectedBy(1, shift); break;
            case Key.Back or Key.Delete: vm.DeleteEditorLine(); break;
            case Key.Enter: FocusEditorEntry(vm.AddEditorLine()); break;
            case Key.B: vm.AddEditorGap(); break;
            case Key.T: vm.StampSelectedLine(); break;
            default: return;
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F11 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HandleEscapeKeyDown(e);
        }
        else if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (ViewModel?.HasCurrentSong == true)
            {
                ViewModel.OpenAddToPlaylistForCurrentSongCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ViewModel?.OpenNewPlaylistDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key == Key.Up && ViewModel?.IsViewingPlaylist == true)
            {
                if (ViewModel.SelectedLibraryRow is not null)
                {
                    ViewModel.MoveSongInPlaylistUp(ViewModel.SelectedLibraryRow);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down && ViewModel?.IsViewingPlaylist == true)
            {
                if (ViewModel.SelectedLibraryRow is not null)
                {
                    ViewModel.MoveSongInPlaylistDown(ViewModel.SelectedLibraryRow);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.Delete && ViewModel?.IsViewingPlaylist == true)
        {
            if (ViewModel.SelectedLibraryRow is not null)
            {
                ViewModel.RemoveSongFromCurrentPlaylist(ViewModel.SelectedLibraryRow);
                e.Handled = true;
            }
        }
    }

    /// <summary>Escape closes whichever dialog/overlay is on top, or exits full screen if none is open.</summary>
    private void HandleEscapeKeyDown(KeyEventArgs e)
    {
        if (ViewModel?.IsSetupWizardOpen == true)
        {
            // Nothing to dismiss to: the wizard only leaves via its own Finish button.
            e.Handled = true;
        }
        else if (ViewModel?.IsLyricsEditorOpen == true)
        {
            ViewModel.CloseLyricsEditorCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsCoverEditorOpen == true)
        {
            ViewModel.CloseCoverEditorCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsAddToPlaylistOpen == true)
        {
            ViewModel.CloseAddToPlaylistCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.SpotifyImport.IsOpen == true)
        {
            ViewModel.SpotifyImport.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsNewPlaylistDialogOpen == true)
        {
            ViewModel.CloseNewPlaylistDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsSettingsOpen == true)
        {
            ViewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsStatsOpen == true)
        {
            ViewModel.CloseStatsCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsUpdateAvailable == true && ViewModel.IsDownloadingUpdate == false)
        {
            ViewModel.DismissUpdateCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsWhatsNewOpen == true)
        {
            ViewModel.CloseWhatsNewCommand.Execute(null);
            e.Handled = true;
        }
        else if (ViewModel?.IsConfirmDialogOpen == true)
        {
            ViewModel.CancelConfirmDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            var isMaximized = WindowState == WindowState.Maximized;
            MaximizeIcon.Data = isMaximized ? Icons.WindowRestore : Icons.WindowMaximize;
            ResizeGrips.IsVisible = WindowState == WindowState.Normal;

            UpdateImmersiveFullScreenState();
        }
    }

    private void UpdateImmersiveFullScreenState()
    {
        bool isFullScreen = WindowState == WindowState.FullScreen;
        LyricsScroller.Classes.Set("immersive", isFullScreen);

        if (isFullScreen && !_isImmersiveFullScreen)
        {
            _isImmersiveFullScreen = true;

            // Leaving ExtendClientAreaToDecorationsHint alone: toggling it off here used to hand
            // the window back to the OS for a frame, which is what made fullscreen both fail to
            // cover the whole screen and flash native chrome on the way in. The custom title bar
            // just needs hiding, same as any other panel.
            TitleBar.IsVisible = false;

            // Windows shows a small native tooltip with the window's Title near the top edge
            // for any window without a visible caption - serves no purpose once there's nothing
            // left up there to hover.
            Title = string.Empty;

            // Not literally edge-to-edge: the corner buttons can still reopen the library/up next
            // panels while fullscreen, and with zero margin here their content (and the player
            // column's own time labels) end up hugging the monitor's edge with nothing around
            // them. A small margin survives everywhere except the transport bar's bottom, whose
            // own breathing room lives in its trailing spacer (see MainWindow.axaml) so it
            // collapses away with the bar instead of leaving a gap once the bar auto-hides.
            MainContentGrid.Margin = new Thickness(FullScreenContentMarginPx);

            // BorderOnly's invisible resize-hit margin is still sized into the window at this
            // point even though there's nothing to resize while fullscreen, which is what was
            // pushing content a few pixels past the monitor edge on every side.
            WindowDecorations = WindowDecorations.None;

            if (ViewModel is { } vm)
            {
                _savedIsLibraryOpen = vm.IsLibraryOpen;
                _savedIsUpNextOpen = vm.IsUpNextOpen;

                vm.IsLibraryOpen = false;
                vm.IsUpNextOpen = false;
            }

            UpdateColumnWidths(animate: false);

            _controlsIdleSecondsRemaining = ControlsIdleHideDelaySeconds;
            SetControlsVisible(true);
        }
        else if (!isFullScreen && _isImmersiveFullScreen)
        {
            _isImmersiveFullScreen = false;

            TitleBar.IsVisible = true;
            Title = "stramp";
            MainContentGrid.Margin = new Thickness(16, 6, 16, 16);
            PlayerColumnGrid.Margin = new Thickness(10, 0);
            WindowDecorations = WindowDecorations.BorderOnly;

            if (ViewModel is { } vm)
            {
                vm.IsLibraryOpen = _savedIsLibraryOpen;
                vm.IsUpNextOpen = _savedIsUpNextOpen;
            }

            UpdateColumnWidths(animate: false);
            SetControlsVisible(true);
        }
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;
        if (sender is Control { Tag: string edgeName } && Enum.TryParse<WindowEdge>(edgeName, out var edge))
            BeginResizeDrag(edge, e);
    }

    /// <summary>Keeps the artwork and the ring around it proportional to the viewport.</summary>
    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 420 wasn't enough buffer - the normal bar's buttons-plus-volume-slider row needs more
        // room than that to lay out without crowding/overlapping, so it was switching to compact
        // well after things had already started to squish rather than before.
        if (ViewModel is { } vm)
            vm.IsCompactControlBar = e.NewSize.Width < 560;
    }

    /// <summary>
    /// The cover and ring are sized against the stage rather than the whole card, so opening the
    /// lyrics panel shrinks them to fit the left half instead of spilling under the text.
    /// </summary>
    private void OnStageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _lastStageShortSide = Math.Min(e.NewSize.Width, e.NewSize.Height);
        UpdateStageArtworkAndVisualizer();
    }

    private void UpdateStageArtworkAndVisualizer()
    {
        if (_lastStageShortSide <= 0)
            return;

        // The old 260px ceiling was sized for the default windowed stage; fullscreen with both
        // side panels closed hands the stage several times that, and a cap that low left the art
        // and title floating at a fixed small size in the middle of all that empty space. 420px
        // keeps scaling sensibly up to large monitors instead of topping out almost immediately.
        var baseArtSize = Math.Clamp(_lastStageShortSide * 0.26, 64, 420);
        var targetRingRadius = baseArtSize * 0.5 * 1.30 + 12;
        var isCircular = ViewModel?.Theme.CircularAlbumArt ?? true;

        Visualizer.InnerRadius = targetRingRadius;

        if (isCircular)
        {
            var circleDiameter = Math.Max(32, (targetRingRadius - 18) * 2);
            ArtFrame.Width = circleDiameter;
            ArtFrame.Height = circleDiameter;
        }
        else
        {
            ArtFrame.Width = baseArtSize;
            ArtFrame.Height = baseArtSize;
        }

        // Scales the title/artist with the art instead of leaving them at a fixed size: at the
        // default ~166px art they land back on the original 320/20/13 numbers.
        var scale = baseArtSize / 166.0;
        NowPlayingTitleStack.MaxWidth = Math.Clamp(320 * scale, 220, 640);
        NowPlayingTitleText.FontSize = Math.Clamp(20 * scale, 16, 34);
        NowPlayingArtistText.FontSize = Math.Clamp(13 * scale, 12, 22);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the backdrop itself dismisses; clicks inside the card bubble up here too.
        if (ReferenceEquals(e.Source, SettingsScrim))
            ViewModel?.CloseSettingsCommand.Execute(null);
    }

    private void OnStatsScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, StatsScrim))
            ViewModel?.CloseStatsCommand.Execute(null);
    }

    private void OnAddToPlaylistScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, AddToPlaylistScrim))
            ViewModel?.CloseAddToPlaylistCommand.Execute(null);
    }

    private void OnUpdateScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, UpdateScrim) && ViewModel?.IsDownloadingUpdate == false)
            ViewModel.DismissUpdateCommand.Execute(null);
    }

    private void OnWhatsNewScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, WhatsNewScrim))
            ViewModel?.CloseWhatsNewCommand.Execute(null);
    }

    private void OnConfirmScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ConfirmScrim))
            ViewModel?.CancelConfirmDialogCommand.Execute(null);
    }

    private void OnNewPlaylistScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, NewPlaylistScrim))
            ViewModel?.CloseNewPlaylistDialogCommand.Execute(null);
    }

    private void OnNewPlaylistTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.ConfirmCreateNewPlaylistInDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelCreateNewPlaylistInDialogCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNewPlaylistFromSpotifyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel?.CloseNewPlaylistDialogCommand.Execute(null);
        ViewModel?.SpotifyImport.OpenCommand.Execute(null);
    }

    // ── Playlist covers ──────────────────────────────────────────────────────

    /// <summary>Asks for an image and opens it in the crop dialog. Null when cancelled or unreadable
    /// (an unreadable one is reported to the user here).</summary>
    private async Task<Avalonia.Media.Imaging.Bitmap?> PickCoverImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a playlist cover",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = PlaylistCoverStore.SupportedPatterns,
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif", "image/x-icon"],
                },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return null;

        var picture = await Task.Run(() => PlaylistCoverStore.LoadForEditing(path));
        if (picture is null && ViewModel is not null)
            ViewModel.StatusText = $"Couldn't open '{Path.GetFileName(path)}' as an image.";
        return picture;
    }

    private async void OnChangeCoverClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // A playlist that already has a custom cover offers Replace / Remove first.
        if (ViewModel?.CurrentSourceRow is { HasCustomCover: true } && sender is Control anchor)
        {
            Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(anchor);
            return;
        }

        await ChooseCoverAsync();
    }

    private async void OnReplaceCoverClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ChooseCoverAsync();

    private async Task ChooseCoverAsync()
    {
        if (await PickCoverImageAsync() is { } picture)
            ViewModel?.OpenCoverEditor(picture);
    }

    private async void OnCoverEditorChooseAnotherClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickCoverImageAsync() is { } picture)
            ViewModel?.ReplaceCoverEditorSource(picture);
    }

    private void OnCoverEditorSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CoverCropControl.RenderCrop(PlaylistCoverStore.SavedSize) is { } cropped)
            ViewModel?.SaveCoverFromEditor(cropped);
    }

    private void OnCoverEditorScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, CoverEditorScrim))
            ViewModel?.CloseCoverEditorCommand.Execute(null);
    }

    private void OnSpotifyImportScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SpotifyImportScrim))
            ViewModel?.SpotifyImport.CloseCommand.Execute(null);
    }

    private void OnSpotifyImportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.SpotifyImport.ImportCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.SpotifyImport.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnStandaloneNewPlaylistKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.ConfirmStandaloneNewPlaylistCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CloseNewPlaylistDialogCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPlaylistPickerItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is PlaylistPickerItem item)
        {
            ViewModel?.AddSongToExistingPlaylistCommand.Execute(item);
        }
    }

    private async void OnExportPlaylistSourceRowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is LibrarySourceRow row && row.IsPlaylist)
            await ExportPlaylistInteractive(row.Name);
    }

    private async void OnExportCurrentPlaylistClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is { CurrentSourceKind: LibrarySourceKind.Playlist } vm)
            await ExportPlaylistInteractive(vm.CurrentSourceName);
    }

    private async Task ExportPlaylistInteractive(string playlistName)
    {
        if (ViewModel is null)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export '{playlistName}' as M3U8",
            SuggestedFileName = $"{playlistName}.m3u8",
            DefaultExtension = "m3u8",
            FileTypeChoices =
            [
                new FilePickerFileType("M3U8 Playlist")
                {
                    Patterns = ["*.m3u8", "*.m3u"],
                    MimeTypes = ["audio/x-mpegurl", "application/vnd.apple.mpegurl"],
                },
            ],
        });

        var path = file?.TryGetLocalPath();
        if (path is null)
            return;

        var error = ViewModel.ExportPlaylist(playlistName, path);
        if (error is not null)
            ViewModel.StatusText = error;
        else
            ViewModel.StatusText = $"Exported playlist to {Path.GetFileName(path)}";
    }

    // ── Library / queue ──────────────────────────────────────────────────────

    private void OnSongContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is SongRow row)
            ViewModel?.LoadRowArt(row);
    }

    private void OnSongContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is SongRow row)
            ViewModel?.ReleaseRowArt(row);
    }

    private async void OnOpenLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a music library folder",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (folder is not null)
            ViewModel?.AddLibraryLocation(folder);
    }

    private async void OnChangePlaylistsFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder to store .m3u8 playlists",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (folder is not null)
            ViewModel?.SetPlaylistsDirectory(folder);
    }

    private void OnOpenPlaylistsFolderInExplorerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = ViewModel?.EffectivePlaylistsDirectory;
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            PlatformServices.OpenFolder(path);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.StatusText = $"Could not open folder: {ex.Message}";
        }
    }

    private void OnOpenListeningHistoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var filePath = ViewModel?.ListeningHistoryFilePath;
        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(filePath))
            {
                PlatformServices.RevealFile(filePath);
            }
            else if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                PlatformServices.OpenFolder(dir);
            }
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.StatusText = $"Could not open listening history: {ex.Message}";
        }
    }

    private async void OnImportPlaylistClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import playlist",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Playlist")
                {
                    Patterns = ["*.m3u", "*.m3u8"],
                    MimeTypes = ["audio/x-mpegurl", "application/vnd.apple.mpegurl"],
                },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null || ViewModel is null)
            return;

        var error = ViewModel.ImportPlaylist(path);
        if (error is not null)
            ViewModel.StatusText = error;
    }

    private void OnLibraryTapped(object? sender, TappedEventArgs e)
    {
        if (RowFrom(e) is { } row)
            ViewModel?.PlaySongCommand.Execute(row);
    }

    private void OnQueueTapped(object? sender, TappedEventArgs e)
    {
        if (RowFrom(e) is { } row)
            ViewModel?.JumpToQueueRowCommand.Execute(row);
    }

    private void OnSourceContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is LibrarySourceRow source)
            ViewModel?.LoadSourceCover(source);
    }

    private void OnSourceTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if ((e.Source as StyledElement)?.DataContext is LibrarySourceRow source)
            ViewModel?.SelectSourceCommand.Execute(source);
    }

    /// <summary>Everything inside an item template inherits the row as its DataContext; taps on
    /// empty list space resolve to the window's view model instead and are ignored.</summary>
    private static SongRow? RowFrom(TappedEventArgs e) =>
        (e.Source as StyledElement)?.DataContext as SongRow;

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e) =>
        ViewModel?.BeginSeek();

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ViewModel?.EndSeek(ProgressSlider.Value);

    /// <summary>Covers the case where the drag ends without a release we can see (e.g. focus loss).</summary>
    private void OnProgressCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ViewModel?.EndSeek(ProgressSlider.Value);

    // ── Animation loop ───────────────────────────────────────────────────────

    private void OnFrameTick(double dt)
    {
        var vm = ViewModel;
        if (vm is null)
            return;

        vm.TickProgress();
        StepColumnAnimation();
        StepLyricsPanelAnimation();
        StepLyricsScroll(dt);
        StepSmoothScrolls(dt);
        StepControlsIdleTimer(dt);
        StepWindowGrowAnimation();

        var frame = vm.VisualizerFeed.GetFrame(vm.ProgressSeconds, SpectrumBins);
        if (frame is not null && vm.IsPlaying)
        {
            Visualizer.UpdateSpectrum(frame.DisplayBins, dt);

            // Pulses are sized by how hard the hit was, so the cover tracks the track's dynamics
            // instead of punching identically on every detected onset.
            var kick = _kickDetector.Update(frame.LowBandEnergy, dt);
            if (kick.IsHit)
                _kickPulse = Math.Max(_kickPulse, kick.Strength);

            var hihat = _hihatDetector.Update(frame.HighBandEnergy, dt);
            if (hihat.IsHit)
                _hihatPulse = Math.Max(_hihatPulse, hihat.Strength);
        }

        var dtRatio = dt * 60.0;
        var pulseDecay = (float)Math.Pow(PulseDecayPerTick, dtRatio);
        _kickPulse *= pulseDecay;
        _hihatPulse *= pulseDecay;

        if (!vm.Theme.AnimateAlbumArt)
        {
            _artTranslate.X = 0;
            _artTranslate.Y = 0;
            _artRotate.Angle = 0;
            _artScale.ScaleX = 1;
            _artScale.ScaleY = 1;
            return;
        }

        _driftPhase += 0.018 * dtRatio;
        var driftX = Math.Sin(_driftPhase * 0.7) * DriftPixels;
        var driftY = Math.Cos(_driftPhase * 0.5) * DriftPixels;

        // A kick also shoves the cover down a touch, which sells the impact more than scale alone.
        _artTranslate.X = driftX;
        _artTranslate.Y = driftY + _kickPulse * KickShiftPixels;
        _artRotate.Angle = Math.Sin(_driftPhase * 0.33) * MaxRotationDegrees;

        // Kicks stretch horizontally (with a little vertical squash for punch), hats stretch vertically.
        _artScale.ScaleX = 1 + _kickPulse * KickStretch;
        _artScale.ScaleY = 1 + _hihatPulse * HihatStretch - _kickPulse * KickSquash;
    }
}
