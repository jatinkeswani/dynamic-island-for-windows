using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// iOS-style looping number wheel. Scroll it, drag it (a flick carries momentum and lands on the
/// projected row), or click a row to jump to it.
/// </summary>
public sealed class WheelPicker : FrameworkElement
{
    private const double RowHeight = 22;
    private const double DecelerationRate = 0.998;
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly Spring _offset = new(0, 0.001) { Response = 0.3 };
    private readonly List<(DateTime Time, double Y)> _samples = new();
    private bool _animating, _dragging, _moved;
    private double _dragStartY, _dragStartOffset;
    private TimeSpan _lastFrame;
    private int _value;

    public WheelPicker()
    {
        Cursor = Cursors.SizeNS;
    }

    public int Count { get; set; } = 60;
    public int Value => _value;
    public event Action? ValueChanged;

    public void Select(int value)
    {
        _offset.Snap(value);
        _value = Mod(value, Count);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(36, RowHeight * 3);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, bounds); // makes the whole area hit-testable
        dc.PushClip(new RectangleGeometry(bounds));

        double center = RenderSize.Height / 2;
        double offset = _offset.Value;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = (int)Math.Floor(offset) - 2; i <= (int)Math.Ceiling(offset) + 2; i++)
        {
            double distance = Math.Abs(i - offset);
            double opacity = 1 - distance * 0.55;
            if (opacity <= 0) continue;

            var text = new FormattedText(
                Mod(i, Count).ToString("00"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font,
                18 - 4 * Math.Min(distance, 1),
                new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255)),
                pixelsPerDip);
            double y = center + (i - offset) * RowHeight - text.Height / 2;
            dc.DrawText(text, new Point((RenderSize.Width - text.Width) / 2, y));
        }
        dc.Pop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        MoveTo(Math.Round(_offset.Target) - Math.Sign(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        _dragging = true;
        _moved = false;
        _dragStartY = e.GetPosition(this).Y;
        _offset.Snap(_offset.Value); // grabbing stops any motion where it is
        _dragStartOffset = _offset.Value;
        _samples.Clear();
        _samples.Add((DateTime.UtcNow, _dragStartY));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        double y = e.GetPosition(this).Y;
        if (Math.Abs(y - _dragStartY) > 3) _moved = true;

        // Track the pointer 1:1, respecting where the wheel was grabbed
        _offset.Snap(_dragStartOffset - (y - _dragStartY) / RowHeight);
        _samples.Add((DateTime.UtcNow, y));
        if (_samples.Count > 6) _samples.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        double y = e.GetPosition(this).Y;

        if (!_moved)
        {
            MoveTo(Math.Round(_offset.Value + (y - RenderSize.Height / 2) / RowHeight));
            return;
        }

        // Project the flick forward (Apple's deceleration formula), land on the nearest row, keep the velocity
        double velocity = -PointerVelocity() / RowHeight; // rows per second
        double projected = _offset.Value + velocity / 1000 * DecelerationRate / (1 - DecelerationRate);
        _offset.SetVelocity(velocity);
        MoveTo(Math.Round(projected));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        MoveTo(Math.Round(_offset.Value));
    }

    private double PointerVelocity()
    {
        if (_samples.Count < 2) return 0;
        var (firstTime, firstY) = _samples[0];
        var (lastTime, lastY) = _samples[^1];
        double seconds = (lastTime - firstTime).TotalSeconds;
        // A pause before release means no flick
        if (seconds <= 0 || (DateTime.UtcNow - lastTime).TotalMilliseconds > 80) return 0;
        return (lastY - firstY) / seconds;
    }

    private void MoveTo(double target)
    {
        _offset.Target = target;
        int value = Mod((int)target, Count);
        if (value != _value)
        {
            _value = value;
            ValueChanged?.Invoke();
        }

        if (_animating) return;
        _animating = true;
        _lastFrame = default;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == default ? 1.0 / 60 : Math.Min((now - _lastFrame).TotalSeconds, 1.0 / 30);
        _lastFrame = now;

        if (!_dragging) _offset.Step(dt);
        InvalidateVisual();

        if (_dragging || _offset.IsSettled)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    private static int Mod(int value, int count) => ((value % count) + count) % count;
}
