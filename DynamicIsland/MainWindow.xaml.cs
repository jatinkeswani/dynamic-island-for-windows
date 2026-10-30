using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static DynamicIsland.NativeMethods;

namespace DynamicIsland;

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
}

public partial class MainWindow : Window
{
    private enum IslandState { Hidden, Compact, Expanded, Alert }
    private enum Activity { None, Media, Timer, Shelf }

    // Window and island geometry, in DIPs
    private const double WindowWidth = 560;
    private const double WindowHeight = 400;
    private const double TabWidth = 40;
    private const double TabHeight = 5;
    private const double DropWidth = 28;
    private const double FloatGap = 10;
    private const double CompactHeight = 34;
    private const double ExpandedWidth = 440;
    private const double ExpandedHeight = 336;
    private const double ExpandedRadius = 30;
    private const double AlertWidth = 340;
    private const double AlertHeight = 84;
    private const double BubbleGap = 8;

    private static readonly TimeSpan AlarmLength = TimeSpan.FromSeconds(15);
    private const string AlarmSound = @"C:\Windows\Media\Alarm01.wav";

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly Settings _settings;
    private readonly MediaService _media = new();
    private readonly IslandTimer _timer = new();
    private readonly ObservableCollection<ShelfItem> _shelf = new();

    // The island's shape is three springs: distance from the screen edge, width and height
    private readonly Spring _top = new(0, 0.02);
    private readonly Spring _width = new(TabWidth, 0.05);
    private readonly Spring _height = new(TabHeight, 0.05);
    private readonly Spring _scale = new(1, 0.0005) { Response = 0.2 };
    // Horizontal shake for the timer alert, and the split-off bubble (0 = merged into the pill, 1 = separate)
    private readonly Spring _shake = new(0, 0.05) { Response = 0.12, DampingRatio = 0.2 };
    private readonly Spring _bubble = new(0, 0.002);
    private readonly MediaPlayer _alarm = new();

    private readonly DispatcherTimer _pointerTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _reduceMotion = !SystemParameters.ClientAreaAnimation;

    private IntPtr _hwnd;
    private IslandState _state = IslandState.Hidden;
    private int _sequence; // bumped to cancel the remaining stages of a staged animation
    private bool _animating, _pressed, _menuOpen, _draggingOut, _suppressed, _timerDone;
    private bool _pressTracked, _pressIsOutsideClick;
    private bool _alertMode; // the large shape shows the timer alert instead of the panel
    private int _alarmSession;
    private bool? _barsPlaying;
    private TimeSpan _lastFrame;
    private DateTime? _outsideSince;
    private DateTime _dragOverUntil;
    private MediaState? _mediaState;
    private byte[]? _thumbnail;
    private Point _dragStart;
    private ShelfItem? _dragItem;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        ShelfList.ItemsSource = _shelf;
        _shelf.CollectionChanged += (_, _) => UpdateShelf();

        MinutePicker.Select((int)_timer.Duration.TotalMinutes);
        SecondPicker.Select(_timer.Duration.Seconds);
        MinutePicker.ValueChanged += OnPickerChanged;
        SecondPicker.ValueChanged += OnPickerChanged;
        _timer.Changed += UpdateTimer;
        _timer.Finished += OnTimerFinished;

        _media.Changed += state => Dispatcher.BeginInvoke(() => ApplyMedia(state));
        // Loop the alarm until it is stopped
        _alarm.MediaEnded += (_, _) =>
        {
            _alarm.Position = TimeSpan.Zero;
            _alarm.Play();
        };
        _pointerTimer.Tick += OnPointerTick;
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // A tool window has no taskbar button and doesn't appear in Alt+Tab
            long style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow));
            PositionWindow();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        Loaded += async (_, _) =>
        {
            if (ScreenshotDir != null)
            {
                RenderScreenshots(ScreenshotDir);
                Application.Current.Shutdown();
                return;
            }

            ApplyFrame();
            UpdateTimer();
            UpdateShelf();
            _pointerTimer.Start();
            _fullscreenTimer.Start();
            await _media.InitAsync();
        };
    }

    public void ApplySettings() => PositionWindow();

    /// <summary>Set by <c>--screenshots</c>: the window stays off-screen and renders demo states instead.</summary>
    public string? ScreenshotDir { get; init; }

    // ---------------------------------------------------------------- Placement

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PositionWindow);

    private System.Windows.Forms.Screen CurrentScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        return _settings.MonitorIndex >= 0 && _settings.MonitorIndex < screens.Length
            ? screens[_settings.MonitorIndex]
            : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    // Positions the window in physical pixels on the chosen display, scaled for that display's DPI
    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (ScreenshotDir != null)
        {
            const uint NoSize = 0x0001;
            SetWindowPos(_hwnd, HwndTopmost, -20000, -20000, 0, 0, SwpNoActivate | NoSize);
            return;
        }
        var bounds = CurrentScreen().Bounds;
        var center = new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        double scale = GetDpiForMonitor(MonitorFromPoint(center, MonitorDefaultToNearest), 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;

        int width = (int)Math.Ceiling(WindowWidth * scale);
        int height = (int)Math.Ceiling(WindowHeight * scale);
        int x = center.X + (int)Math.Round(_settings.OffsetX * scale) - width / 2;
        x = Math.Clamp(x, bounds.Left - width / 2, bounds.Right - width / 2);
        SetWindowPos(_hwnd, HwndTopmost, x, bounds.Top, width, height, SwpNoActivate);
    }

    // ---------------------------------------------------------------- Hover detection & auto-hide

    // Polling the cursor (instead of invisible hit areas) means the activation and safe zones never block clicks
    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (_suppressed || _hwnd == IntPtr.Zero || !GetCursorPos(out var cursor)) return;
        Point p = PointFromScreen(new Point(cursor.X, cursor.Y));
        double cx = WindowWidth / 2;

        if (_state == IslandState.Hidden)
        {
            var activation = new Rect(cx - _settings.ActivationWidth / 2, -2, _settings.ActivationWidth, _settings.ActivationHeight + 2);
            if (activation.Contains(p)) Reveal();
            return;
        }

        // The timer alert stays until it is clicked
        if (_state == IslandState.Alert) return;

        // The expanded panel ignores pointer distance; only a click elsewhere closes it
        if (_state == IslandState.Expanded)
        {
            _outsideSince = null;
            TrackOutsideClick(p);
            return;
        }

        // Safe zone: the island plus a margin, joined to the top edge by the activation corridor
        var island = TargetRect();
        island.Inflate(_settings.SafeMargin, _settings.SafeMargin);
        var corridor = new Rect(cx - _settings.ActivationWidth / 2, -2, _settings.ActivationWidth, FloatGap + 4);

        if (island.Contains(p) || corridor.Contains(p) || KeepOpen())
        {
            _outsideSince = null;
            return;
        }

        _outsideSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _outsideSince.Value).TotalMilliseconds >= _settings.HideDelayMs)
        {
            _outsideSince = null;
            Retract();
        }
    }

    // A press that starts and ends outside the panel closes it. A press that turns into dragging
    // something onto the island (e.g. a file from Explorer) doesn't count.
    private void TrackOutsideClick(Point p)
    {
        bool down = (GetAsyncKeyState(VkLButton) & 0x8000) != 0 || (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
        var island = TargetRect();

        if (down && !_pressTracked)
        {
            _pressTracked = true;
            _pressIsOutsideClick = !island.Contains(p) && !_menuOpen && !_draggingOut;
        }
        else if (down && DateTime.UtcNow < _dragOverUntil)
        {
            _pressIsOutsideClick = false;
        }
        else if (!down && _pressTracked)
        {
            _pressTracked = false;
            if (_pressIsOutsideClick && !island.Contains(p)) Retract();
        }
    }

    // Stay open while typing, dragging a slider or file, or using a menu
    private bool KeepOpen()
    {
        return _draggingOut || _menuOpen || DateTime.UtcNow < _dragOverUntil
            || Mouse.Captured != null
            || (IsActive && Keyboard.FocusedElement is TextBoxBase);
    }

    private Rect TargetRect()
    {
        double cx = WindowWidth / 2;
        if (_state == IslandState.Expanded) return new Rect(cx - ExpandedWidth / 2, FloatGap, ExpandedWidth, ExpandedHeight);
        if (_state == IslandState.Alert) return new Rect(cx - AlertWidth / 2, FloatGap, AlertWidth, AlertHeight);
        // The compact pill, plus the bubble on its right when there is a second activity
        double extra = SecondaryActivity != Activity.None ? BubbleGap + CompactHeight : 0;
        return new Rect(cx - CompactWidth / 2, FloatGap, CompactWidth + extra, CompactHeight);
    }

    private void CheckFullscreen()
    {
        bool fullscreen = _settings.HideInFullscreen && IsFullscreenAppRunning();
        if (fullscreen == _suppressed || _state == IslandState.Alert) return;
        _suppressed = fullscreen;

        if (fullscreen)
        {
            _sequence++;
            _state = IslandState.Hidden;
            Expanded.IsHitTestVisible = false;
            _top.Snap(0);
            _width.Snap(TabWidth);
            _height.Snap(TabHeight);
            _bubble.Snap(0);
            ApplyFrame();
            Root.Visibility = Visibility.Hidden;
        }
        else
        {
            Root.Visibility = Visibility.Visible;
        }
    }

    private bool IsFullscreenAppRunning()
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state is QunsRunningD3DFullScreen or QunsPresentationMode)
            return true;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd || foreground == GetShellWindow()
            || foreground == GetDesktopWindow() || IsZoomed(foreground))
            return false;

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "WorkerW" or "Progman") return false;

        // Borderless fullscreen: the foreground window covers the island's whole display
        if (!GetWindowRect(foreground, out var r)) return false;
        var m = CurrentScreen().Bounds;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ---------------------------------------------------------------- State transitions

    private double CompactWidth => PrimaryActivity switch
    {
        Activity.Media => 170,
        Activity.Timer => 150,
        Activity.Shelf => 136,
        _ => 110,
    };

    // Active things in priority order: the first fills the pill, the second gets the bubble
    private List<Activity> Activities()
    {
        var list = new List<Activity>();
        if (_timer.IsRunning || _timerDone) list.Add(Activity.Timer);
        if (_mediaState?.IsPlaying == true) list.Add(Activity.Media);
        if (_shelf.Count > 0) list.Add(Activity.Shelf);
        if (list.Count == 0 && _mediaState != null) list.Add(Activity.Media); // paused music only when nothing else
        return list;
    }

    private Activity PrimaryActivity => Activities().FirstOrDefault(Activity.None);
    private Activity SecondaryActivity => Activities().Skip(1).FirstOrDefault(Activity.None);

    private void UpdateBubble()
    {
        bool show = _state == IslandState.Compact && _top.Target == FloatGap && SecondaryActivity != Activity.None;
        SetSpring(_bubble, show ? 1 : 0, 0.4, show ? 0.85 : 1);
    }

    // Tab → downward liquid stretch → drop detaches from the edge → compact pill
    private async void Reveal()
    {
        if (_state != IslandState.Hidden || _suppressed) return;
        _state = IslandState.Compact;
        int sequence = ++_sequence;
        double response = _settings.RevealMs / 1000.0;

        SetSpring(_height, CompactHeight, response * 0.7, 0.85);
        SetSpring(_width, DropWidth, response * 0.55, 0.85);

        await Task.Delay((int)(_settings.RevealMs * 0.3));
        if (sequence != _sequence) return;

        SetSpring(_top, FloatGap, response * 0.8, 0.8);
        SetSpring(_width, CompactWidth, response, 0.85);
        UpdateBubble();
    }

    // The same path in reverse: panel → pill → drop rejoins the edge → tab
    private async void Retract()
    {
        if (_state == IslandState.Hidden) return;
        bool wasLarge = _state is IslandState.Expanded or IslandState.Alert;
        _state = IslandState.Hidden;
        Expanded.IsHitTestVisible = false;
        int sequence = ++_sequence;
        double response = _settings.RevealMs / 1000.0;
        SetSpring(_bubble, 0, 0.3, 1);

        if (wasLarge)
        {
            SetSpring(_width, CompactWidth, 0.3, 1);
            SetSpring(_height, CompactHeight, 0.3, 1);
            await Task.Delay(170);
            if (sequence != _sequence) return;
        }

        SetSpring(_width, DropWidth, response * 0.6, 1);
        SetSpring(_top, 0, response * 0.6, 1);

        await Task.Delay((int)(_settings.RevealMs * 0.3));
        if (sequence != _sequence) return;

        SetSpring(_height, TabHeight, response * 0.6, 1);
        SetSpring(_width, TabWidth, response * 0.6, 1);
    }

    private void Expand()
    {
        if (_state != IslandState.Compact) return;
        _state = IslandState.Expanded;
        _sequence++;
        _pressTracked = false;
        _alertMode = false;
        Expanded.IsHitTestVisible = true;
        RefreshOutputs();
        SetSpring(_bubble, 0, 0.3, 1);

        SetSpring(_top, FloatGap, 0.4, 1);
        SetSpring(_width, ExpandedWidth, 0.42, 0.85);
        SetSpring(_height, ExpandedHeight, 0.42, 0.85);
    }

    private void Collapse()
    {
        if (_state != IslandState.Expanded) return;
        _state = IslandState.Compact;
        _sequence++;
        Expanded.IsHitTestVisible = false;

        SetSpring(_width, CompactWidth, 0.35, 1);
        SetSpring(_height, CompactHeight, 0.35, 1);
        UpdateBubble();
    }

    // Timer finished: open straight into the alert (via the droplet if hidden), shake, and ring for 15 s
    private async void ShowAlert()
    {
        bool wasHidden = _state == IslandState.Hidden;
        _state = IslandState.Alert;
        _alertMode = true;
        Expanded.IsHitTestVisible = false;
        int sequence = ++_sequence;
        double response = _settings.RevealMs / 1000.0;
        SetSpring(_bubble, 0, 0.3, 1);
        StartAlarm();

        if (wasHidden)
        {
            SetSpring(_height, CompactHeight, response * 0.7, 0.85);
            SetSpring(_width, DropWidth, response * 0.55, 0.85);
            await Task.Delay((int)(_settings.RevealMs * 0.3));
            if (sequence != _sequence) return;
        }

        SetSpring(_top, FloatGap, response * 0.8, 0.8);
        SetSpring(_width, AlertWidth, 0.42, 0.85);
        SetSpring(_height, AlertHeight, 0.42, 0.85);

        // Shake once it has opened, then again every couple of seconds while the alarm rings
        await Task.Delay(450);
        int session = _alarmSession;
        while (sequence == _sequence && session == _alarmSession && _state == IslandState.Alert)
        {
            Shake();
            await Task.Delay(2500);
        }
    }

    private void DismissAlert()
    {
        StopAlarm();
        _timerDone = false;
        _timer.Reset();
        Retract();
    }

    private void Shake()
    {
        if (_reduceMotion) return;
        _shake.SetVelocity(500);
        StartAnimating();
    }

    private async void StartAlarm()
    {
        int session = ++_alarmSession;
        if (File.Exists(AlarmSound))
        {
            _alarm.Open(new Uri(AlarmSound));
            _alarm.Play();
        }
        else
        {
            SystemSounds.Exclamation.Play();
        }

        await Task.Delay(AlarmLength);
        if (session == _alarmSession) StopAlarm();
    }

    // The sound stops after 15 s (or on dismiss); the alert itself stays until clicked
    private void StopAlarm()
    {
        _alarmSession++;
        _alarm.Stop();
        _alarm.Close();
    }

    // ---------------------------------------------------------------- Animation

    private void SetSpring(Spring spring, double target, double response, double damping)
    {
        spring.Target = target;
        spring.Response = response;
        spring.DampingRatio = _reduceMotion ? 1 : damping;
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = default;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        double dt = _lastFrame == default ? 1.0 / 60 : Math.Min((now - _lastFrame).TotalSeconds, 1.0 / 30);
        _lastFrame = now;

        _top.Step(dt);
        _width.Step(dt);
        _height.Step(dt);
        _scale.Step(dt);
        _shake.Step(dt);
        _bubble.Step(dt);
        ApplyFrame();

        if (_top.IsSettled && _width.IsSettled && _height.IsSettled && _scale.IsSettled && _shake.IsSettled && _bubble.IsSettled)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    private void ApplyFrame()
    {
        double cx = WindowWidth / 2 + _shake.Value;
        double top = Math.Max(_top.Value, 0);
        double w = Math.Max(_width.Value, 2);
        double h = Math.Max(_height.Value, 1);

        // 0 = compact, 1 = fully open (as the panel or as the timer alert)
        double fullHeight = _alertMode ? AlertHeight : ExpandedHeight;
        double expand = Math.Clamp((h - CompactHeight) / (fullHeight - CompactHeight), 0, 1);
        // 0 = attached to the screen edge, 1 = floating free
        double detach = Math.Clamp(top / FloatGap, 0, 1);

        double radius = h <= CompactHeight ? h / 2 : Lerp(CompactHeight / 2, ExpandedRadius, expand);
        radius = Math.Min(radius, w / 2);
        // While attached, the top corners are flat against the edge, like liquid hanging from it
        double topRadius = radius * Smooth(detach);

        var bodyRect = new Rect(cx - w / 2, top, w, h);
        var body = RoundedRect(bodyRect, topRadius, radius);
        Geometry shape = body;

        // Liquid neck that pinches off as the drop pulls away from the edge
        double waist = Math.Min(w, 44) / 2 * (1 - Smooth(detach));
        if (top > 0.5 && waist > 0.3)
        {
            double baseHalf = waist * 1.5;
            double joinHalf = Math.Min(w / 2, waist * 1.3);
            double joinY = top + radius * 0.6;
            var neck = new StreamGeometry();
            using (var g = neck.Open())
            {
                g.BeginFigure(new Point(cx - baseHalf, 0), true, true);
                g.LineTo(new Point(cx + baseHalf, 0), false, false);
                g.QuadraticBezierTo(new Point(cx + waist, top * 0.5), new Point(cx + joinHalf, joinY), false, false);
                g.LineTo(new Point(cx - joinHalf, joinY), false, false);
                g.QuadraticBezierTo(new Point(cx - waist, top * 0.5), new Point(cx - baseHalf, 0), false, false);
            }
            neck.Freeze();
            shape = new CombinedGeometry(GeometryCombineMode.Union, body, neck);
        }

        // Second-activity bubble: grows out of the pill's right end and slides clear of it, like iOS
        double split = Math.Max(_bubble.Value, 0);
        double bubbleCx = bodyRect.Right + Lerp(-CompactHeight / 2, BubbleGap + CompactHeight / 2, split);
        double bubbleCy = top + Math.Min(h, CompactHeight) / 2;
        if (split > 0.01)
        {
            double bubbleRadius = CompactHeight / 2 * Math.Min(split, 1);
            shape = new CombinedGeometry(GeometryCombineMode.Union, shape, new EllipseGeometry(new Point(bubbleCx, bubbleCy), bubbleRadius, bubbleRadius));
        }
        Canvas.SetLeft(BubbleContent, bubbleCx - CompactHeight / 2);
        Canvas.SetTop(BubbleContent, bubbleCy - CompactHeight / 2);
        BubbleContent.Opacity = Smooth(Math.Clamp((split - 0.6) / 0.4, 0, 1));

        Body.Data = Gloss.Data = shape;
        ContentLayer.Clip = body;

        Canvas.SetLeft(Compact, bodyRect.Left);
        Canvas.SetTop(Compact, top);
        Compact.Width = w;
        Compact.Opacity = Smooth(Math.Clamp((h - 22) / 12, 0, 1)) * Math.Clamp((w - 60) / 40, 0, 1) * (1 - Math.Clamp(expand / 0.3, 0, 1));

        Canvas.SetLeft(Expanded, cx - ExpandedWidth / 2);
        Canvas.SetTop(Expanded, top);
        double largeOpacity = Math.Clamp((expand - 0.5) / 0.5, 0, 1);
        Expanded.Opacity = _alertMode ? 0 : largeOpacity;
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = Lerp(0.96, 1, expand);

        Canvas.SetLeft(AlertPanel, cx - AlertWidth / 2);
        Canvas.SetTop(AlertPanel, top);
        AlertPanel.Opacity = _alertMode ? largeOpacity : 0;

        IslandScale.CenterX = cx;
        IslandScale.ScaleX = IslandScale.ScaleY = _scale.Value;
    }

    private static Geometry RoundedRect(Rect r, double topRadius, double bottomRadius)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(r.Left + topRadius, r.Top), true, true);
            g.LineTo(new Point(r.Right - topRadius, r.Top), true, false);
            if (topRadius > 0) g.ArcTo(new Point(r.Right, r.Top + topRadius), new Size(topRadius, topRadius), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(r.Right, r.Bottom - bottomRadius), true, false);
            if (bottomRadius > 0) g.ArcTo(new Point(r.Right - bottomRadius, r.Bottom), new Size(bottomRadius, bottomRadius), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(r.Left + bottomRadius, r.Bottom), true, false);
            if (bottomRadius > 0) g.ArcTo(new Point(r.Left, r.Bottom - bottomRadius), new Size(bottomRadius, bottomRadius), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(r.Left, r.Top + topRadius), true, false);
            if (topRadius > 0) g.ArcTo(new Point(r.Left + topRadius, r.Top), new Size(topRadius, topRadius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    private static double Smooth(double t) => t * t * (3 - 2 * t);

    // ---------------------------------------------------------------- Island input

    // Feedback on press, commit on release
    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state is not (IslandState.Compact or IslandState.Alert)) return;
        _pressed = true;
        SetSpring(_scale, _reduceMotion ? 1 : 0.96, 0.2, 1);
    }

    private void Body_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        SetSpring(_scale, 1, 0.2, 1);

        // Clicking the alert silences it and closes the island
        if (_state == IslandState.Alert)
        {
            DismissAlert();
            return;
        }
        Activate();
        Expand();
    }

    private void Body_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        SetSpring(_scale, 1, 0.2, 1);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Collapse();
    }

    private void Menu_Opened(object sender, RoutedEventArgs e) => _menuOpen = true;
    private void Menu_Closed(object sender, RoutedEventArgs e) => _menuOpen = false;
    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();
    private void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    // ---------------------------------------------------------------- Compact pill

    private void UpdateCompact()
    {
        var primary = PrimaryActivity;
        CompactMedia.Visibility = primary == Activity.Media ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = primary == Activity.Timer ? Visibility.Visible : Visibility.Collapsed;
        CompactShelf.Visibility = primary == Activity.Shelf ? Visibility.Visible : Visibility.Collapsed;

        var secondary = SecondaryActivity;
        if (secondary != Activity.None)
        {
            BubbleMedia.Visibility = secondary == Activity.Media ? Visibility.Visible : Visibility.Collapsed;
            BubbleShelf.Visibility = secondary == Activity.Shelf ? Visibility.Visible : Visibility.Collapsed;
        }

        // Resize the pill for its new content, unless it is still mid-reveal
        if (_state == IslandState.Compact && _top.Target == FloatGap)
            SetSpring(_width, CompactWidth, 0.4, 1);
        UpdateBubble();
    }

    // ---------------------------------------------------------------- Music

    private void ApplyMedia(MediaState? state)
    {
        _mediaState = state;
        PrevButton.IsEnabled = state?.CanPrevious == true;
        NextButton.IsEnabled = state?.CanNext == true;
        PlayPauseButton.IsEnabled = state?.CanPlayPause == true;

        if (state == null)
        {
            TitleText.Text = "Nothing playing";
            ArtistText.Text = "Play something in Spotify or your browser";
            UpdateArtwork(null);
            SetPlaying(false);
        }
        else
        {
            TitleText.Text = string.IsNullOrWhiteSpace(state.Title) ? "Unknown title" : state.Title;
            ArtistText.Text = state.Artist;
            UpdateArtwork(state.Thumbnail);
            SetPlaying(state.IsPlaying);
        }
        UpdateCompact();
    }

    private void UpdateArtwork(byte[]? thumbnail)
    {
        if (thumbnail == _thumbnail || (thumbnail != null && _thumbnail != null && thumbnail.AsSpan().SequenceEqual(_thumbnail))) return;
        _thumbnail = thumbnail;

        Brush? art = null;
        if (thumbnail != null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(thumbnail);
                image.EndInit();
                image.Freeze();
                art = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception)
            {
                // Unreadable artwork falls back to the placeholder
            }
        }

        var placeholder = (Brush)FindResource("ArtPlaceholder");
        ArtLarge.Background = ArtSmall.Background = BubbleArt.Fill = art ?? placeholder;
        ArtLargeGlyph.Visibility = ArtSmallGlyph.Visibility = BubbleArtGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPlaying(bool playing)
    {
        PlayPauseGlyph.Text = playing ? PauseGlyph : PlayGlyph;
        if (_barsPlaying == playing) return;
        _barsPlaying = playing;

        var bars = new[] { Bar1, Bar2, Bar3 };
        var periods = new[] { 0.42, 0.55, 0.48 };
        for (int i = 0; i < bars.Length; i++)
        {
            if (playing && !_reduceMotion)
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(periods[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(i * 0.12),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            }
            else
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bars[i].ScaleY = playing ? 0.7 : 0.35;
            }
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // Flip the icon immediately; the real state arrives from Windows a moment later
        SetPlaying(PlayPauseGlyph.Text == PlayGlyph);
        _media.TogglePlayPause();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    // ---------------------------------------------------------------- Picture-in-picture

    private void ArtButton_MouseEnter(object sender, MouseEventArgs e) => FadeArtHint(_mediaState != null);
    private void ArtButton_MouseLeave(object sender, MouseEventArgs e) => FadeArtHint(false);
    private void ArtButton_Click(object sender, MouseButtonEventArgs e) => OpenPip();

    private void FadeArtHint(bool show) =>
        ArtPipHint.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 180))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        });

    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "opera", "vivaldi"];

    // The companion browser extension listens for Ctrl+Shift+9 as a global shortcut (so it works while
    // the browser is in the background) and puts the playing tab's video into the browser's own PiP
    private void OpenPip()
    {
        if (_mediaState is not { } media) return;

        string app = media.SourceApp.ToLowerInvariant();
        if (!Browsers.Any(app.Contains))
        {
            FlashMessage("Picture-in-picture works for videos playing in Chrome or Edge");
            return;
        }

        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkShift, 0, 0, UIntPtr.Zero);
        keybd_event((byte)'9', 0, 0, UIntPtr.Zero);
        keybd_event((byte)'9', 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkShift, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);

        Retract();
    }

    private async void FlashMessage(string message)
    {
        ArtistText.Text = message;
        await Task.Delay(3000);
        ApplyMedia(_mediaState);
    }

    // ---------------------------------------------------------------- Timer

    private void UpdateTimer()
    {
        // Wheels while setting the time; the countdown while running, paused or done
        bool setting = !_timer.IsRunning && !_timer.IsPaused && !_timerDone;
        PickerPanel.Visibility = setting ? Visibility.Visible : Visibility.Collapsed;
        TimerText.Visibility = setting ? Visibility.Collapsed : Visibility.Visible;

        TimerText.Text = IslandTimer.Format(_timer.Remaining);
        CompactTimerText.Text = _timerDone ? "Done" : IslandTimer.Format(_timer.Remaining);
        TimerStartButton.Content = _timer.IsRunning ? "Pause" : _timer.IsPaused ? "Resume" : "Start";
        TimerStartButton.IsEnabled = _timer.Duration > TimeSpan.Zero;
        UpdateCompact();
    }

    private void OnPickerChanged() =>
        _timer.SetDuration(TimeSpan.FromMinutes(MinutePicker.Value) + TimeSpan.FromSeconds(SecondPicker.Value));

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        _timerDone = false;
        if (_timer.IsRunning) _timer.Pause();
        else _timer.Start();
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        _timerDone = false;
        _timer.Reset();
    }

    private void OnTimerFinished()
    {
        _timerDone = true;
        AlertDetail.Text = $"{IslandTimer.Format(_timer.Duration)} timer · click to dismiss";
        UpdateTimer();
        ShowAlert();
    }

    // ---------------------------------------------------------------- Audio output

    private void RefreshOutputs()
    {
        try
        {
            OutputList.ItemsSource = AudioOutputs.GetOutputs();
            OutputError.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            OutputList.ItemsSource = null;
            OutputError.Visibility = Visibility.Visible;
        }
    }

    private void Output_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id) return;
        try
        {
            AudioOutputs.SetDefault(id);
        }
        catch (Exception)
        {
            // Leave the list unchanged; it is re-read below either way
        }
        RefreshOutputs();
    }

    // ---------------------------------------------------------------- File shelf

    private void UpdateShelf()
    {
        ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactShelfText.Text = _shelf.Count == 1 ? "1 file" : $"{_shelf.Count} files";
        UpdateCompact();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool files = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;

        _dragOverUntil = DateTime.UtcNow.AddMilliseconds(400);
        // Dragging files onto the pill opens the panel so they can be dropped on the shelf
        if (_state == IslandState.Compact) Expand();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            if (_shelf.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _shelf.Add(new ShelfItem { Path = path, Name = System.IO.Path.GetFileName(path.TrimEnd('\\')), Icon = LoadIcon(path) });
        }
        e.Handled = true;
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (!File.Exists(path)) return null; // folders use the glyph
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = (sender as FrameworkElement)?.DataContext as ShelfItem;
        _dragStart = e.GetPosition(this);
    }

    // Drag a shelf item out into any app that accepts files
    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (effect == DragDropEffects.Move || (!File.Exists(item.Path) && !Directory.Exists(item.Path)))
                _shelf.Remove(item);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ShelfItem item) _shelf.Remove(item);
    }

    private void ShelfScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShelfScroll.ScrollToHorizontalOffset(ShelfScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }
}
