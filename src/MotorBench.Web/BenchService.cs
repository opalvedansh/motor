using System.Net.Sockets;
using MotorBench.Logging;
using MotorBench.Modbus;
using MotorBench.Plc;
using MotorBench.Sequencing;

namespace MotorBench.Web;

public sealed record PlcEndpoint(string Host, int Port, string DisplayName);

/// <param name="RetentionDays">Run files older than this are deleted; 0 keeps them forever.</param>
public sealed record WebSettings(string DataDir, string ProfileDir, int RetentionDays = 365);

public sealed record SequenceView(
    string? Profile, string? DutSerial, RunnerState State, bool Held, TestStep? Step, int StepCount,
    double DwellElapsed, string? AbortReason, StepResult[] Results, string? Report);

public sealed record AlarmView(DateTime Time, AlarmCode Code, bool Raised);

/// <summary>Everything the page shows, built under the gate so it is always consistent.</summary>
public sealed record BenchSnapshot(
    string Plc, string? Error, BenchStatus? Status, SequenceView Sequence, AlarmView[] Alarms, DateTime UpdatedUtc);

/// <summary>A command the bench refused; <see cref="StatusCode"/> is the HTTP status to answer with.</summary>
public sealed class BenchCommandException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Owns the PLC connection. Polls every 100 ms (watchdog + status), drives the
/// sequence runner, and runs operator commands one at a time with the poll loop.
/// </summary>
public sealed class BenchService : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownStopTimeout = TimeSpan.FromSeconds(10);
    private const int MaxAlarmHistory = 50;

    private readonly PlcEndpoint _endpoint;
    private readonly TimeSpan _pollInterval;
    private readonly WebSettings _settings;
    private readonly ILogger<BenchService> _log;
    private readonly ModbusTcpClient _modbus = new();
    private readonly BenchPlc _plc;
    private readonly SequenceRunner _runner;
    private readonly AlarmTracker _alarms;
    private readonly SemaphoreSlim _gate = new(1, 1); // runner and PLC calls are not thread safe

    // only touched while holding _gate
    private readonly List<AlarmView> _alarmLog = [];
    private BenchStatus? _last;
    private string? _error = "Connecting...";
    private string? _report;
    private string? _dutSerial;
    private RunRecord? _run;
    private int _lastHeartbeat = -1;
    private DateTime _heartbeatChangedUtc;
    private DateTime _updatedUtc;

    private volatile BenchSnapshot _snapshot;
    private readonly object _shutdownLock = new();
    private Task? _shutdownStop;

    public BenchService(PlcEndpoint endpoint, PlcOptions options, WebSettings settings, ILogger<BenchService> log)
    {
        _endpoint = endpoint;
        _pollInterval = TimeSpan.FromMilliseconds(options.PollIntervalMs);
        _modbus.UnitId = (byte)options.UnitId;
        _modbus.Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        _settings = settings;
        _log = log;
        _plc = new BenchPlc(_modbus) { CommandTimeout = TimeSpan.FromMilliseconds(options.CommandTimeoutMs) };
        _runner = new SequenceRunner(_plc);
        _alarms = new AlarmTracker(Path.Combine(settings.DataDir, "alarm_history.csv"));

        _alarms.Changed += e =>
        {
            _alarmLog.Insert(0, new AlarmView(e.Time, e.Code, e.Raised));
            if (_alarmLog.Count > MaxAlarmHistory)
                _alarmLog.RemoveAt(_alarmLog.Count - 1);
            if (e.Raised)
                _log.LogWarning("Alarm raised: {Alarm}", e.Code);
        };
        _runner.StateChanged += OnRunnerStateChanged;
        _snapshot = BuildSnapshot();
    }

    /// <summary>Latest snapshot. Reported offline if the poll loop has not updated it recently.</summary>
    public BenchSnapshot Snapshot
    {
        get
        {
            var s = _snapshot;
            var age = DateTime.UtcNow - s.UpdatedUtc;
            return s.Error is null && age > StaleAfter
                ? s with { Error = $"No status update for {age.TotalSeconds:0} s", Status = null }
                : s;
        }
    }

    // ---- operator commands ------------------------------------------------------

    public Task StartAsync() => CommandAsync(ct => { RefuseDuringSequence(); return _plc.StartAsync(ct); });

    /// <summary>Stopping the bench mid-sequence is an operator abort.</summary>
    public Task StopBenchAsync() => CommandAsync(ct => _runner.IsActive ? _runner.AbortAsync(ct) : _plc.StopAsync(ct));

    public Task ResetAsync() => CommandAsync(_plc.ResetAsync);

    public Task SetSetpointsAsync(int speedRpm, int loadPct) =>
        CommandAsync(ct => { RefuseDuringSequence(); return _plc.SetSetpointsAsync(speedRpm, loadPct, ct); }); // BenchPlc clamps

    public Task SetSimEStopAsync(bool on) => CommandAsync(ct => _plc.SetSimEStopAsync(on, ct));

    public Task SetSimRunawayAsync(bool on) => CommandAsync(ct => _plc.SetSimRunawayAsync(on, ct));

    /// <summary>
    /// Starts a traceable run: raw data is logged from the first poll, and the
    /// step report records who tested which unit with exactly which profile.
    /// </summary>
    public Task StartSequenceAsync(TestProfile profile, string dutSerial, string operatorName, string profileSha256) =>
        CommandAsync(async ct =>
        {
            RefuseDuringSequence();
            var started = DateTime.Now;
            CsvDataLogger raw;
            try
            {
                raw = new CsvDataLogger(_settings.DataDir, $"raw_{profile.Name}_{dutSerial}", started);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an untraceable run must not happen
                throw new BenchCommandException($"Cannot record run data in {_settings.DataDir}: {ex.Message}", 500);
            }

            _report = null;
            _dutSerial = dutSerial;
            _run = new RunRecord(dutSerial, operatorName, profileSha256, started, raw);
            _log.LogInformation("Run started: {Profile} on DUT {Dut} by {Operator}", profile.Name, dutSerial, operatorName);
            try
            {
                // fresh status, not the last poll: a reset a moment ago must count
                _last = await _plc.ReadStatusAsync(ct);
                await _runner.StartAsync(profile, _last, ct);
            }
            catch when (_run is { } refused)
            {
                // refused before anything ran (e.g. alarm not reset): no record of a run that never happened
                refused.Raw.Dispose();
                File.Delete(refused.Raw.FilePath);
                _run = null;
                throw;
            }
        });

    public Task HoldAsync() => CommandAsync(_ => { _runner.Hold(); return Task.CompletedTask; });

    public Task ResumeAsync() => CommandAsync(_ => { _runner.Resume(); return Task.CompletedTask; });

    public Task AbortSequenceAsync() => CommandAsync(ct => _runner.AbortAsync(ct));

    private void RefuseDuringSequence()
    {
        if (_runner.IsActive)
            throw new BenchCommandException("A test sequence is running. Hold or abort it first.", 409);
    }

    private async Task CommandAsync(Func<CancellationToken, Task> action)
    {
        if (Snapshot.Error is { } offline)
            throw new BenchCommandException(offline, 503);

        await _gate.WaitAsync();
        try
        {
            await action(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            // runner refusals ("reset the alarm first") and "not connected"
            throw new BenchCommandException(ex.Message, 409);
        }
        catch (Exception ex) when (IsCommError(ex))
        {
            throw new BenchCommandException(ex.Message, 502);
        }
        finally
        {
            _snapshot = BuildSnapshot();
            _gate.Release();
        }
    }

    // ---- poll loop --------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            bool backOff = false;
            await _gate.WaitAsync(ct);
            try
            {
                await PollOnceAsync(ct);
            }
            catch (Exception ex) when (IsCommError(ex))
            {
                _modbus.Close();
                SetOffline($"No connection to PLC at {_endpoint.Host}:{_endpoint.Port}: {ex.Message}");
                backOff = true;
            }
            catch (PlcMismatchException ex)
            {
                _modbus.Close();
                SetOffline(ex.Message);
                backOff = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let the loop die: the PLC would fault the bench on its
                // watchdog and the page would keep showing the last status.
                _log.LogError(ex, "Poll cycle failed");
                SetOffline($"HMI internal error: {ex.Message}");
            }
            finally
            {
                _snapshot = BuildSnapshot();
                _gate.Release();
            }

            if (backOff)
                await Task.Delay(ReconnectDelay, ct);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (!_modbus.IsConnected)
        {
            await _modbus.ConnectAsync(_endpoint.Host, _endpoint.Port, ct);
            await CheckMapVersionAsync(ct); // before any write: never send commands into a program we do not know
            _alarms.Clear();
            _lastHeartbeat = -1;
            _log.LogInformation("Connected to PLC at {Host}:{Port}", _endpoint.Host, _endpoint.Port);
        }

        await _plc.KickWatchdogAsync(ct);
        var status = await _plc.ReadStatusAsync(ct);
        var now = DateTime.UtcNow;
        _last = status;
        _updatedUtc = now;

        // Modbus can keep answering while the PLC program is stopped; only the
        // heartbeat tells us the scan is running (same check as the WinForms HMI).
        if (status.Heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = status.Heartbeat;
            _heartbeatChangedUtc = now;
        }
        if (now - _heartbeatChangedUtc > HeartbeatTimeout)
        {
            SetOffline("PLC program is not scanning (heartbeat frozen)");
            return;
        }

        if (_error is not null)
            _log.LogInformation("PLC online");
        _error = null;
        _alarms.Update(status);
        if (_run is not null && !await TryLogRawAsync(status))
            return;
        await _runner.UpdateAsync(status, ct);
    }

    private async Task CheckMapVersionAsync(CancellationToken ct)
    {
        int version;
        try
        {
            version = await _plc.ReadMapVersionAsync(ct);
        }
        catch (ModbusException)
        {
            version = 0; // device refuses HR 8: not our program
        }
        if (version != RegisterMap.CurrentMapVersion)
            throw new PlcMismatchException(
                $"PLC program implements register map version {version}, this HMI needs {RegisterMap.CurrentMapVersion}. " +
                "Load plc/bench_control.st from this release.");
    }

    private void SetOffline(string reason)
    {
        if (_error != reason)
            _log.LogWarning("PLC offline: {Reason}", reason);
        _error = reason;
        _runner.Fail(reason);
    }

    // ---- shutdown ---------------------------------------------------------------

    /// <summary>
    /// Stop the motor before the HMI goes away, and keep the watchdog alive until
    /// it is at standstill. Otherwise the PLC faults the bench on comm loss.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The host can call this twice at once (app.Run's shutdown and an outer
        // StopAsync); both must wait for the same stop before anything disposes.
        Task stop;
        lock (_shutdownLock)
            stop = _shutdownStop ??= StopBenchOnShutdownAsync(cancellationToken);
        await stop;
        await base.StopAsync(cancellationToken);
    }

    private async Task StopBenchOnShutdownAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ShutdownStopTimeout);
        try
        {
            await _gate.WaitAsync(cts.Token);
            try
            {
                if (_modbus.IsConnected && _error is null && _last?.State is BenchState.Running or BenchState.Stopping)
                {
                    _log.LogInformation("Shutting down: stopping the bench");
                    if (_runner.IsActive)
                        await _runner.AbortAsync(cts.Token);
                    else if (_last.State == BenchState.Running)
                        await _plc.StopAsync(cts.Token);

                    BenchStatus status;
                    do
                    {
                        await Task.Delay(_pollInterval, cts.Token);
                        await _plc.KickWatchdogAsync(cts.Token);
                        status = await _plc.ReadStatusAsync(cts.Token);
                    }
                    while (status.State == BenchState.Stopping);
                    _log.LogInformation("Bench stopped, state {State}", status.State);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException || IsCommError(ex))
        {
            _log.LogWarning("Could not stop the bench cleanly on shutdown ({Message}); the PLC watchdog will stop it", ex.Message);
        }
    }

    public override void Dispose()
    {
        _run?.Raw.Dispose();
        _modbus.Dispose();
        base.Dispose();
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<bool> TryLogRawAsync(BenchStatus status)
    {
        try
        {
            _run!.Raw.Write(status);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not write run data; aborting the sequence");
            _runner.Fail($"run data could not be written: {ex.Message}");
            await _plc.StopAsync();
            return false;
        }
    }

    // Every run that started gets a report, aborted ones included, even with no finished steps.
    private void OnRunnerStateChanged()
    {
        if (_runner.State is not (RunnerState.Completed or RunnerState.Aborted) || _runner.Profile is null || _run is not { } run)
            return;
        _run = null;
        run.Raw.Dispose();

        string outcome = _runner.State == RunnerState.Completed ? "completed" : $"aborted: {_runner.AbortReason}";
        _log.LogInformation("Run {Outcome}: {Profile} on DUT {Dut}", outcome, _runner.Profile.Name, run.DutSerial);
        try
        {
            _report = Path.GetFileName(StepReport.Write(_settings.DataDir, _runner.Profile, _runner.Results, outcome, DateTime.Now,
            [
                new("dut_serial", run.DutSerial),
                new("operator", run.Operator),
                new("profile_sha256", run.ProfileSha256),
                new("started", run.Started.ToString("yyyy-MM-dd HH:mm:ss")),
                new("raw_data", Path.GetFileName(run.Raw.FilePath)),
            ]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the results are still shown on the page and the raw data file exists
            _log.LogError(ex, "Could not write step report to {DataDir}", _settings.DataDir);
        }
    }

    private sealed record RunRecord(string DutSerial, string Operator, string ProfileSha256, DateTime Started, CsvDataLogger Raw);

    private BenchSnapshot BuildSnapshot() => new(
        _endpoint.DisplayName,
        _error,
        _error is null ? _last : null,
        new SequenceView(
            _runner.Profile?.Name,
            _dutSerial,
            _runner.State,
            _runner.IsHeld,
            _runner.CurrentStep,
            _runner.Profile?.Steps.Count ?? 0,
            _runner.DwellElapsed,
            _runner.AbortReason,
            [.. _runner.Results],
            _report),
        [.. _alarmLog],
        _updatedUtc);

    private static bool IsCommError(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or ModbusException;

    private sealed class PlcMismatchException(string message) : Exception(message);
}
