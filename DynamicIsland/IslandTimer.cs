using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>A countdown timer. Remaining time is computed from the wall clock so ticks can't drift.</summary>
public sealed class IslandTimer
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _endsAt;

    public IslandTimer()
    {
        _tick.Tick += (_, _) => Update();
    }

    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(5);
    public bool IsRunning { get; private set; }
    public bool IsPaused => !IsRunning && Remaining > TimeSpan.Zero && Remaining < Duration;

    public event Action? Changed;
    public event Action? Finished;

    public void SetDuration(TimeSpan duration)
    {
        if (IsRunning) return;
        Duration = Remaining = duration;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (Remaining <= TimeSpan.Zero) Remaining = Duration;
        _endsAt = DateTime.UtcNow + Remaining;
        IsRunning = true;
        _tick.Start();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!IsRunning) return;
        Remaining = Max(_endsAt - DateTime.UtcNow, TimeSpan.Zero);
        IsRunning = false;
        _tick.Stop();
        Changed?.Invoke();
    }

    public void Reset()
    {
        IsRunning = false;
        _tick.Stop();
        Remaining = Duration;
        Changed?.Invoke();
    }

    private void Update()
    {
        Remaining = Max(_endsAt - DateTime.UtcNow, TimeSpan.Zero);
        if (Remaining == TimeSpan.Zero)
        {
            IsRunning = false;
            _tick.Stop();
            Changed?.Invoke();
            Finished?.Invoke();
            return;
        }
        Changed?.Invoke();
    }

    public static string Format(TimeSpan time)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(time.TotalSeconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
