namespace SmoothMotion.Windows;

/// <summary>
/// A fixed output cadence driven by a display's callbacks. Carries fractional deadlines across
/// callbacks, so a 75/144 Hz display can present 60 fps without falling to 37.5/48 fps.
/// </summary>
public sealed class OutputClock
{
    private readonly double _step;
    private double _next;
    private bool _started;

    public OutputClock(double framesPerSecond)
    {
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _step = 1 / framesPerSecond;
    }

    public bool TryTick(double wallTime, out int missed)
    {
        missed = 0;
        if (!_started)
        {
            _started = true;
            _next = wallTime + _step;
            return true;
        }
        // Numerical tolerance only: this never intentionally presents a millisecond early.
        const double epsilon = 0.0000001;
        if (wallTime + epsilon < _next) return false;
        missed = Math.Max(0, (int)Math.Floor((wallTime - _next + epsilon) / _step));
        _next += (missed + 1) * _step;
        return true;
    }

    public void Reset() => _started = false;

    public double SecondsUntilNextTick(double wallTime) => _started ? Math.Max(0, _next - wallTime) : 0;

    public static double TargetRate(int cap, double displayHz) => Math.Min(cap, displayHz > 1 ? displayHz : 60);
}
