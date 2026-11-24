namespace SmoothMotion.Windows;

public sealed record MotionSample(double InputFps, double OutputFps, double IntermediateFps,
    long Captured, long Presented, long Intermediate, long CaptureDrops, long MissedTicks, double SubmitCpuMs);

/// <summary>Thread-safe counters. Output means accepted Presents, not verified physical scanouts.</summary>
public sealed class MotionMetrics
{
    private readonly object _gate = new();
    private long _captured, _presented, _intermediate, _captureDrops, _missed;
    private long _previousInput, _previousOutput, _previousIntermediate;
    private double _sampleTime = double.NaN, _work;
    private MotionSample _last = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public void Capture(bool dropped)
    {
        lock (_gate) { if (dropped) _captureDrops++; else _captured++; }
    }

    public void Present(bool accepted, bool intermediate, int missed, double cpuMilliseconds)
    {
        lock (_gate)
        {
            _missed += missed + (accepted ? 0 : 1);
            if (!accepted) return;
            _presented++;
            if (intermediate) _intermediate++;
            _work = _work == 0 ? cpuMilliseconds : _work * 0.9 + cpuMilliseconds * 0.1;
        }
    }

    public MotionSample Sample(double now)
    {
        lock (_gate)
        {
            if (double.IsNaN(_sampleTime)) _sampleTime = now;
            var elapsed = now - _sampleTime;
            double input = _last.InputFps, output = _last.OutputFps, intermediate = _last.IntermediateFps;
            if (elapsed >= 0.25)
            {
                input = (_captured - _previousInput) / elapsed;
                output = (_presented - _previousOutput) / elapsed;
                intermediate = (_intermediate - _previousIntermediate) / elapsed;
                _previousInput = _captured; _previousOutput = _presented; _previousIntermediate = _intermediate;
                _sampleTime = now;
            }
            return _last = new(input, output, intermediate, _captured, _presented, _intermediate, _captureDrops, _missed, _work);
        }
    }

    public void Reset(double now)
    {
        lock (_gate)
        {
            _captured = _presented = _intermediate = _captureDrops = _missed = 0;
            _previousInput = _previousOutput = _previousIntermediate = 0;
            _sampleTime = now; _work = 0;
            _last = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }
}
