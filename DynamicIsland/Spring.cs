namespace DynamicIsland;

/// <summary>
/// A damped spring described the way Apple describes them: a damping ratio (1 = no overshoot,
/// lower = bouncier) and a response in seconds (lower = snappier). Changing Target mid-flight keeps
/// the current value and velocity, so every animation is interruptible without a visible jump.
/// </summary>
public sealed class Spring
{
    private const double StepSize = 1.0 / 240;

    private readonly double _epsilon;

    public Spring(double value, double epsilon)
    {
        Value = Target = value;
        _epsilon = epsilon;
    }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }
    public double DampingRatio { get; set; } = 1.0;
    public double Response { get; set; } = 0.4;

    public bool IsSettled => Math.Abs(Target - Value) < _epsilon && Math.Abs(Velocity) < _epsilon;

    /// <summary>Hand off a gesture's release velocity so the motion continues without a seam.</summary>
    public void SetVelocity(double velocity) => Velocity = velocity;

    /// <summary>Jump straight to a value with no motion.</summary>
    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public void Step(double seconds)
    {
        double stiffness = Math.Pow(2 * Math.PI / Response, 2);
        double damping = 4 * Math.PI * DampingRatio / Response;

        // Fixed sub-steps keep the integration stable regardless of frame rate
        while (seconds > 0)
        {
            double dt = Math.Min(StepSize, seconds);
            double acceleration = -stiffness * (Value - Target) - damping * Velocity;
            Velocity += acceleration * dt;
            Value += Velocity * dt;
            seconds -= dt;
        }

        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
        }
    }
}
