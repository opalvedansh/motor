using MotorBench.Plc;

namespace MotorBench.Sequencing;

public enum RunnerState
{
    Idle,
    Settling,
    Dwelling,
    Completed,
    Aborted,
}

public sealed record StepResult(
    TestStep Step,
    double SettleSeconds,
    double AvgSpeedRpm,
    double AvgTorqueNm,
    double MaxTempC);

/// <summary>
/// Runs a test profile step by step. It has no timer of its own: the HMI poll
/// loop calls <see cref="UpdateAsync"/> with every new status, and elapsed time
/// is taken from the status timestamps. That keeps it easy to test.
/// </summary>
public sealed class SequenceRunner
{
    private readonly IBenchPlc _plc;
    private readonly List<StepResult> _results = [];

    private TestProfile? _profile;
    private int _stepIndex;
    private DateTime _lastUpdate;
    private bool _starting;

    // per-step accumulators, in seconds; they do not advance while held
    private double _stepTime;
    private double _inBandTime;
    private double _dwellTime;
    private double _settleTime;

    private int _samples;
    private double _sumSpeed;
    private double _sumTorque;
    private double _maxTemp;

    public SequenceRunner(IBenchPlc plc)
    {
        _plc = plc;
    }

    public int SpeedToleranceRpm { get; init; } = 20;
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public RunnerState State { get; private set; } = RunnerState.Idle;
    public bool IsHeld { get; private set; }
    public bool IsActive => State is RunnerState.Settling or RunnerState.Dwelling;
    public string? AbortReason { get; private set; }
    public TestProfile? Profile => _profile;
    public TestStep? CurrentStep => IsActive ? _profile!.Steps[_stepIndex] : null;
    public double DwellElapsed => _dwellTime;
    public IReadOnlyList<StepResult> Results => _results;

    public event Action? StateChanged;
    public event Action<TestStep>? StepStarted;
    public event Action<StepResult>? StepCompleted;

    public async Task StartAsync(TestProfile profile, BenchStatus status, CancellationToken ct = default)
    {
        if (IsActive)
            throw new InvalidOperationException("A sequence is already running");
        if (status.State == BenchState.Fault)
            throw new InvalidOperationException("Reset the bench alarm before starting a sequence");

        _profile = profile;
        _results.Clear();
        _stepIndex = 0;
        AbortReason = null;
        IsHeld = false;

        // A status read before the PLC took the start command may still say
        // Idle or Stopping; ignore updates until the start is confirmed.
        _starting = true;
        try
        {
            // setpoints first, so the drive never starts towards an old setpoint
            await BeginStepAsync(status.Timestamp, ct);
            await _plc.StartAsync(ct);
        }
        catch (Exception ex)
        {
            Fail($"start failed: {ex.Message}");
            throw;
        }
        finally
        {
            _starting = false;
        }
    }

    public async Task UpdateAsync(BenchStatus status, CancellationToken ct = default)
    {
        if (!IsActive || _starting)
            return;

        // clamp so a stalled poll loop or a clock change can't skip a step
        double dt = Math.Clamp((status.Timestamp - _lastUpdate).TotalSeconds, 0, 1.0);
        _lastUpdate = status.Timestamp;

        if (status.State == BenchState.Fault)
        {
            // the PLC has already stopped the drive
            Fail($"bench alarm: {BenchText.Describe(status.Alarm)}");
            return;
        }
        if (status.State == BenchState.Stopping)
        {
            Fail("bench was stopped outside the sequence");
            return;
        }

        if (IsHeld)
            return;

        var step = _profile!.Steps[_stepIndex];
        _stepTime += dt;

        if (State == RunnerState.Settling)
        {
            bool inBand = status.State == BenchState.Running
                && Math.Abs(status.SpeedRpm - step.SpeedRpm) <= SpeedToleranceRpm;
            _inBandTime = inBand ? _inBandTime + dt : 0;

            if (_inBandTime >= SettleTime.TotalSeconds)
            {
                _settleTime = _stepTime;
                SetState(RunnerState.Dwelling);
            }
            else if (_stepTime > SettleTimeout.TotalSeconds)
            {
                await AbortAsync($"step {step.Number} did not reach {step.SpeedRpm} rpm within {SettleTimeout.TotalSeconds:0} s", ct);
            }
            return;
        }

        _samples++;
        _sumSpeed += status.SpeedRpm;
        _sumTorque += status.TorqueNm;
        _maxTemp = Math.Max(_maxTemp, status.MotorTempC);
        _dwellTime += dt;

        if (_dwellTime < step.DwellSeconds)
            return;

        var result = new StepResult(step, _settleTime, _sumSpeed / _samples, _sumTorque / _samples, _maxTemp);
        _results.Add(result);
        StepCompleted?.Invoke(result);

        if (_stepIndex + 1 < _profile.Steps.Count)
        {
            _stepIndex++;
            await BeginStepAsync(status.Timestamp, ct);
        }
        else
        {
            SetState(RunnerState.Completed);
            await _plc.StopAsync(ct);
        }
    }

    /// <summary>Pause the profile. The bench keeps running at the current setpoints.</summary>
    public void Hold()
    {
        if (!IsActive || IsHeld)
            return;
        IsHeld = true;
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        if (!IsHeld)
            return;
        IsHeld = false;
        StateChanged?.Invoke();
    }

    /// <summary>Operator abort: stop the sequence and the bench.</summary>
    public Task AbortAsync(CancellationToken ct = default) => AbortAsync("aborted by operator", ct);

    /// <summary>Stop the sequence without talking to the PLC (e.g. connection lost).</summary>
    public void Fail(string reason)
    {
        if (!IsActive)
            return;
        AbortReason = reason;
        IsHeld = false;
        SetState(RunnerState.Aborted);
    }

    private async Task AbortAsync(string reason, CancellationToken ct)
    {
        if (!IsActive)
            return;
        Fail(reason);
        await _plc.StopAsync(ct);
    }

    private async Task BeginStepAsync(DateTime now, CancellationToken ct)
    {
        var step = _profile!.Steps[_stepIndex];

        _lastUpdate = now;
        _stepTime = 0;
        _inBandTime = 0;
        _dwellTime = 0;
        _settleTime = 0;
        _samples = 0;
        _sumSpeed = 0;
        _sumTorque = 0;
        _maxTemp = double.MinValue;

        SetState(RunnerState.Settling);
        StepStarted?.Invoke(step);
        await _plc.SetSetpointsAsync(step.SpeedRpm, step.LoadPct, ct);
    }

    private void SetState(RunnerState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
