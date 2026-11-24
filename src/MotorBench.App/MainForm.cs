using System.Globalization;
using System.Net.Sockets;
using MotorBench.Logging;
using MotorBench.Modbus;
using MotorBench.Plc;
using MotorBench.Sequencing;

namespace MotorBench.App;

public sealed partial class MainForm : Form
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxEvents = 500;

    private readonly ModbusTcpClient _modbus = new();
    private readonly BenchPlc _plc;
    private readonly SequenceRunner _runner;
    private readonly AlarmTracker _alarms;
    private readonly string _dataDir;

    private CancellationTokenSource? _pollCts;
    private BenchStatus? _last;
    private CsvDataLogger? _logger;
    private TestProfile? _profile;
    private RunnerState _lastRunnerState;
    private int _currentStepNumber;

    private int _lastHeartbeat = -1;
    private DateTime _heartbeatChangedAt;
    private bool _plcStalled;

    private bool _syncingChecks;
    private bool _closeConfirmed;

    public MainForm()
    {
        _plc = new BenchPlc(_modbus);
        _runner = new SequenceRunner(_plc);
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MotorBench");
        _alarms = new AlarmTracker(Path.Combine(_dataDir, "alarm_history.csv"));

        BuildLayout();

        _speedChart.AddSeries("実速度", Color.RoyalBlue);
        _speedChart.AddSeries("速度指令", Color.DarkOrange);
        _loadChart.AddSeries("トルク [N·m]", Color.SeaGreen);
        _loadChart.AddSeries("温度 [°C]", Color.Firebrick);

        WireEvents();
        ShowDisconnected();
        UpdateControls();
        LogEvent("情報", $"データ保存先: {_dataDir}");
    }

    private bool IsConnected => _pollCts is not null;
    private int SpeedSetpoint => (int)_speedSpBox.Value;
    private int LoadSetpoint => (int)_loadSpBox.Value;

    private void WireEvents()
    {
        _connectButton.Click += async (_, _) =>
        {
            if (IsConnected)
                Disconnect("操作による切断");
            else
                await ConnectAsync();
        };

        _applyButton.Click += async (_, _) =>
            await RunAsync("指令送信", () => _plc.SetSetpointsAsync(SpeedSetpoint, LoadSetpoint));

        _startButton.Click += async (_, _) => await RunAsync("運転", async () =>
        {
            // what the operator sees in the boxes is what the bench runs at
            await _plc.SetSetpointsAsync(SpeedSetpoint, LoadSetpoint);
            await _plc.StartAsync();
        });

        _stopButton.Click += async (_, _) => await RunAsync("停止", () => _plc.StopAsync());
        _resetButton.Click += async (_, _) => await RunAsync("リセット", ResetAsync);

        _estopCheck.CheckedChanged += async (_, _) =>
        {
            if (!_syncingChecks)
                await RunAsync("非常停止", () => _plc.SetSimEStopAsync(_estopCheck.Checked));
        };
        _runawayCheck.CheckedChanged += async (_, _) =>
        {
            if (!_syncingChecks)
                await RunAsync("故障注入", () => _plc.SetSimRunawayAsync(_runawayCheck.Checked));
        };

        _loadProfileButton.Click += (_, _) => LoadProfile();
        _runSeqButton.Click += async (_, _) => await RunAsync("自動試験開始", StartSequenceAsync);
        _holdButton.Click += (_, _) =>
        {
            if (_runner.IsHeld)
                _runner.Resume();
            else
                _runner.Hold();
        };
        _abortButton.Click += async (_, _) => await RunAsync("中止", () => _runner.AbortAsync());

        _runner.StepStarted += OnStepStarted;
        _runner.StepCompleted += OnStepCompleted;
        _runner.StateChanged += OnRunnerStateChanged;
        _alarms.Changed += e => LogEvent("アラーム", e.Text);

        FormClosing += OnFormClosing;
    }

    // ---- connection -------------------------------------------------------

    private async Task ConnectAsync()
    {
        string host = _hostBox.Text.Trim();
        int port = (int)_portBox.Value;
        _connectButton.Enabled = false;

        try
        {
            await _modbus.ConnectAsync(host, port);

            // one read up front so a wrong address shows up here, not in the loop
            var status = await _plc.ReadStatusAsync();
            _speedSpBox.Value = Math.Clamp(status.SpeedSetpointRpm, 0, RegisterMap.MaxSpeedRpm);
            _loadSpBox.Value = Math.Clamp(status.LoadSetpointPct, 0, RegisterMap.MaxLoadPct);

            _syncingChecks = true;
            _estopCheck.Checked = false;
            _runawayCheck.Checked = false;
            _syncingChecks = false;
            await _plc.SetSimEStopAsync(false);
            await _plc.SetSimRunawayAsync(false);

            _alarms.Clear();
            _speedChart.Clear();
            _loadChart.Clear();
            _lastHeartbeat = -1;
            _plcStalled = false;

            LogEvent("接続", $"{host}:{port}");
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }
        catch (Exception ex) when (IsCommError(ex))
        {
            _modbus.Close();
            LogEvent("エラー", $"接続できません ({host}:{port}): {ex.Message}");
        }
        finally
        {
            _syncingChecks = false;
            _connectButton.Enabled = true;
            UpdateControls();
        }
    }

    private void Disconnect(string reason)
    {
        if (!IsConnected)
            return;

        _pollCts!.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
        _modbus.Close();

        // the PLC watchdog stops the bench on its own if it was running
        _runner.Fail(reason);
        CloseLogger();
        _last = null;

        LogEvent("切断", reason);
        ShowDisconnected();
        UpdateControls();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _plc.KickWatchdogAsync(ct);
                var status = await _plc.ReadStatusAsync(ct);
                OnStatus(status);
                await _runner.UpdateAsync(status, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect() was called
        }
        catch (Exception ex) when (IsCommError(ex) || ex is InvalidOperationException)
        {
            Disconnect($"通信エラー: {ex.Message}");
        }
    }

    private static bool IsCommError(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or ModbusException;

    // ---- status -----------------------------------------------------------

    private void OnStatus(BenchStatus s)
    {
        var previous = _last;
        _last = s;

        ShowStatus(s);
        CheckHeartbeat(s);
        _alarms.Update(s);
        _speedChart.Push(s.SpeedRpm, s.SpeedSetpointRpm);
        _loadChart.Push(s.TorqueNm, s.MotorTempC);
        LogData(s);
        UpdateSequenceLabel();

        if (previous is null || previous.State != s.State)
            UpdateControls();
    }

    private void ShowStatus(BenchStatus s)
    {
        _stateLabel.Text = BenchText.Describe(s.State);
        _stateLabel.ForeColor = s.State switch
        {
            BenchState.Running => Color.ForestGreen,
            BenchState.Fault => Color.Firebrick,
            _ => SystemColors.ControlText,
        };
        _speedLabel.Text = $"{s.SpeedRpm} rpm";
        _torqueLabel.Text = $"{s.TorqueNm:0.0} N·m";
        _tempLabel.Text = $"{s.MotorTempC:0.0} °C";
        _alarmLabel.Text = s.Alarm == AlarmCode.None ? "なし" : $"{(int)s.Alarm}: {BenchText.Describe(s.Alarm)}";
        _alarmLabel.ForeColor = s.Alarm == AlarmCode.None ? SystemColors.ControlText : Color.Firebrick;

        _driveLamp.On = s.DriveEnable;
        _brakeLamp.On = s.BrakeRelease;
        _runLamp.On = s.RunLamp;
        _faultLamp.On = s.FaultLamp;
    }

    private void ShowDisconnected()
    {
        _connLabel.Text = "未接続";
        _connLabel.ForeColor = Color.Gray;
        foreach (var label in new[] { _stateLabel, _speedLabel, _torqueLabel, _tempLabel, _alarmLabel })
        {
            label.Text = "-";
            label.ForeColor = SystemColors.ControlText;
        }
        _driveLamp.On = _brakeLamp.On = _runLamp.On = _faultLamp.On = false;
    }

    // The PLC increments the heartbeat every scan. If it stops, the runtime is
    // up (it still answers Modbus) but the program is not running.
    private void CheckHeartbeat(BenchStatus s)
    {
        if (s.Heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = s.Heartbeat;
            _heartbeatChangedAt = s.Timestamp;
            if (_plcStalled)
                LogEvent("情報", "PLCのハートビートが復帰しました");
            _plcStalled = false;
        }
        else if (!_plcStalled && (s.Timestamp - _heartbeatChangedAt).TotalSeconds > 1.0)
        {
            _plcStalled = true;
            LogEvent("警告", "PLCのハートビートが止まっています (プログラム停止?)");
        }

        _connLabel.Text = _plcStalled ? "PLC応答なし" : $"接続中  HB {s.Heartbeat}";
        _connLabel.ForeColor = _plcStalled ? Color.DarkOrange : Color.ForestGreen;
    }

    private void LogData(BenchStatus s)
    {
        bool driving = s.State is BenchState.Running or BenchState.Stopping;

        if (driving && _logger is null)
        {
            try
            {
                _logger = new CsvDataLogger(_dataDir, _runner.IsActive ? "seq" : "manual", s.Timestamp);
            }
            catch (IOException ex)
            {
                LogEvent("エラー", $"記録ファイルを作成できません: {ex.Message}");
                return;
            }
        }

        _logger?.Write(s);

        if (!driving && !_runner.IsActive)
            CloseLogger();
    }

    private void CloseLogger()
    {
        if (_logger is null)
            return;
        LogEvent("記録", $"保存しました: {Path.GetFileName(_logger.FilePath)}");
        _logger.Dispose();
        _logger = null;
    }

    // ---- manual commands --------------------------------------------------

    private async Task ResetAsync()
    {
        await _plc.ResetAsync();

        // the PLC ignores a reset while the cause is still there; tell the operator why
        var s = await _plc.ReadStatusAsync();
        if (s.State == BenchState.Fault)
            LogEvent("警告", "リセットできません: モーター停止を待つか、アラーム要因を取り除いてください");
    }

    private async Task RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (IsCommError(ex) || ex is InvalidOperationException)
        {
            LogEvent("エラー", $"{what}: {ex.Message}");
        }
        UpdateControls();
    }

    // ---- auto test --------------------------------------------------------

    private void LoadProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "試験パターン (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            Title = "試験パターンを開く",
        };
        var bundled = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (Directory.Exists(bundled))
            dialog.InitialDirectory = bundled;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _profile = TestProfile.Load(dialog.FileName);
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException)
        {
            LogEvent("エラー", $"{Path.GetFileName(dialog.FileName)}: {ex.Message}");
            return;
        }

        _profileLabel.Text = string.Create(CultureInfo.InvariantCulture,
            $"{_profile.Name}  ({_profile.Steps.Count} ステップ, 計測 {_profile.TotalDwellSeconds:0} s)");

        _stepsGrid.Rows.Clear();
        foreach (var step in _profile.Steps)
            _stepsGrid.Rows.Add(step.Number, step.SpeedRpm, step.LoadPct, step.DwellSeconds, "", "", "", "", "待機");

        LogEvent("情報", $"試験パターン読込: {_profile.Name}");
        UpdateControls();
    }

    private async Task StartSequenceAsync()
    {
        if (_profile is null || _last is null)
            return;

        foreach (DataGridViewRow row in _stepsGrid.Rows)
        {
            foreach (var col in new[] { ColSettle, ColAvgSpeed, ColAvgTorque, ColMaxTemp })
                row.Cells[col].Value = "";
            row.Cells[ColStatus].Value = "待機";
        }

        LogEvent("自動試験", $"開始: {_profile.Name}");
        await _runner.StartAsync(_profile, _last);
    }

    private void OnStepStarted(TestStep step)
    {
        _currentStepNumber = step.Number;
        SetRowStatus(step.Number, "整定中");

        int index = step.Number - 1;
        if (index < 0 || index >= _stepsGrid.RowCount)
            return;
        _stepsGrid.ClearSelection();
        _stepsGrid.Rows[index].Selected = true;
        _stepsGrid.FirstDisplayedScrollingRowIndex = index;
    }

    private void OnStepCompleted(StepResult r)
    {
        var row = _stepsGrid.Rows[r.Step.Number - 1];
        row.Cells[ColSettle].Value = r.SettleSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        row.Cells[ColAvgSpeed].Value = r.AvgSpeedRpm.ToString("0", CultureInfo.InvariantCulture);
        row.Cells[ColAvgTorque].Value = r.AvgTorqueNm.ToString("0.00", CultureInfo.InvariantCulture);
        row.Cells[ColMaxTemp].Value = r.MaxTempC.ToString("0.0", CultureInfo.InvariantCulture);
        row.Cells[ColStatus].Value = "完了";
    }

    private void OnRunnerStateChanged()
    {
        var state = _runner.State;
        if (state != _lastRunnerState)
        {
            _lastRunnerState = state;
            if (state is RunnerState.Completed or RunnerState.Aborted)
                FinishSequence();
        }

        if (_runner.IsActive)
        {
            string phase = _runner.IsHeld ? "一時停止" : state == RunnerState.Dwelling ? "計測中" : "整定中";
            SetRowStatus(_currentStepNumber, phase);
        }

        UpdateSequenceLabel();
        UpdateControls();
    }

    private void FinishSequence()
    {
        string outcome;
        if (_runner.State == RunnerState.Completed)
        {
            outcome = "completed";
            LogEvent("自動試験", "完了");
        }
        else
        {
            outcome = $"aborted: {_runner.AbortReason}";
            SetRowStatus(_currentStepNumber, "中止");
            LogEvent("自動試験", $"中止: {_runner.AbortReason}");
        }

        if (_runner.Profile is not { } profile || _runner.Results.Count == 0)
            return;

        try
        {
            var path = StepReport.Write(_dataDir, profile, _runner.Results, outcome, DateTime.Now);
            LogEvent("記録", $"試験結果: {Path.GetFileName(path)}");
        }
        catch (IOException ex)
        {
            LogEvent("エラー", $"試験結果を保存できません: {ex.Message}");
        }
    }

    private void UpdateSequenceLabel()
    {
        if (_runner.CurrentStep is not { } step || _runner.Profile is null)
        {
            _seqLabel.Text = _runner.State switch
            {
                RunnerState.Completed => "完了",
                RunnerState.Aborted => $"中止: {_runner.AbortReason}",
                _ => "-",
            };
            return;
        }

        string phase = _runner.IsHeld ? "一時停止"
            : _runner.State == RunnerState.Dwelling
                ? string.Create(CultureInfo.InvariantCulture, $"計測中 {_runner.DwellElapsed:0}/{step.DwellSeconds:0} s")
                : "整定中";
        _seqLabel.Text = $"ステップ {step.Number}/{_runner.Profile.Steps.Count}  {step.SpeedRpm} rpm, {step.LoadPct} %  {phase}";
    }

    private void SetRowStatus(int stepNumber, string text)
    {
        int index = stepNumber - 1;
        if (index >= 0 && index < _stepsGrid.RowCount)
            _stepsGrid.Rows[index].Cells[ColStatus].Value = text;
    }

    // ---- misc -------------------------------------------------------------

    private void UpdateControls()
    {
        bool connected = IsConnected;
        bool sequence = _runner.IsActive;
        var state = _last?.State;

        _connectButton.Text = connected ? "切断" : "接続";
        _hostBox.Enabled = !connected;
        _portBox.Enabled = !connected;

        _speedSpBox.Enabled = connected && !sequence;
        _loadSpBox.Enabled = connected && !sequence;
        _applyButton.Enabled = connected && !sequence;
        _startButton.Enabled = connected && !sequence && state is BenchState.Idle or BenchState.Stopping;
        _stopButton.Enabled = connected; // always available
        _resetButton.Enabled = connected && state == BenchState.Fault;
        _estopCheck.Enabled = connected;
        _runawayCheck.Enabled = connected;

        _loadProfileButton.Enabled = !sequence;
        _runSeqButton.Enabled = connected && !sequence && _profile is not null
            && state is BenchState.Idle or BenchState.Running;
        _holdButton.Enabled = sequence;
        _holdButton.Text = _runner.IsHeld ? "再開" : "一時停止";
        _abortButton.Enabled = sequence;
    }

    private void LogEvent(string kind, string message)
    {
        var item = new ListViewItem(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        item.SubItems.Add(kind);
        item.SubItems.Add(message);
        if (kind is "アラーム" or "エラー")
            item.ForeColor = Color.Firebrick;
        else if (kind == "警告")
            item.ForeColor = Color.DarkOrange;

        _eventList.Items.Insert(0, item);
        while (_eventList.Items.Count > MaxEvents)
            _eventList.Items.RemoveAt(_eventList.Items.Count - 1);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        bool running = IsConnected && _last?.State == BenchState.Running;
        if (_closeConfirmed || !running)
        {
            _pollCts?.Cancel();
            _modbus.Dispose();
            _logger?.Dispose();
            return;
        }

        e.Cancel = true;
        var answer = MessageBox.Show(this, "ベンチが運転中です。停止して終了しますか?", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            await _plc.StopAsync();
        }
        catch (Exception ex) when (IsCommError(ex))
        {
            // the PLC watchdog will stop it within 2 s anyway
        }

        _closeConfirmed = true;
        Close();
    }
}
