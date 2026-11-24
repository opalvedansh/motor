using MotorBench.App.Controls;

namespace MotorBench.App;

// Controls and layout. Built in code rather than with the designer so the
// layout is easy to read and diff.
partial class MainForm
{
    private readonly TextBox _hostBox = new() { Text = "127.0.0.1", Width = 120 };
    private readonly NumericUpDown _portBox = new() { Minimum = 1, Maximum = 65535, Value = 502, Width = 70 };
    private readonly Button _connectButton = new() { Text = "接続", Width = 80 };
    private readonly Label _connLabel = new() { Text = "未接続", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(10, 7, 3, 3) };

    private readonly Label _stateLabel = ValueLabel();
    private readonly Label _speedLabel = ValueLabel();
    private readonly Label _torqueLabel = ValueLabel();
    private readonly Label _tempLabel = ValueLabel();
    private readonly Label _alarmLabel = new() { AutoSize = true, Text = "-", Margin = new Padding(3, 8, 3, 3) };
    private readonly Lamp _driveLamp = new("駆動", Color.LimeGreen);
    private readonly Lamp _brakeLamp = new("ブレーキ開", Color.Gold);
    private readonly Lamp _runLamp = new("運転", Color.LimeGreen);
    private readonly Lamp _faultLamp = new("異常", Color.Red);

    private readonly NumericUpDown _speedSpBox = new() { Minimum = 0, Maximum = 3000, Increment = 100, Width = 90 };
    private readonly NumericUpDown _loadSpBox = new() { Minimum = 0, Maximum = 100, Increment = 5, Width = 90 };
    private readonly Button _applyButton = new() { Text = "指令送信", Width = 90 };
    private readonly Button _startButton = new() { Text = "運転", Width = 90, Height = 32 };
    private readonly Button _stopButton = new() { Text = "停止", Width = 90, Height = 32 };
    private readonly Button _resetButton = new() { Text = "リセット", Width = 90, Height = 32 };
    private readonly CheckBox _estopCheck = new() { Text = "非常停止 (シミュレーション)", AutoSize = true };
    private readonly CheckBox _runawayCheck = new() { Text = "暴走 (故障注入)", AutoSize = true };

    private readonly Button _loadProfileButton = new() { Text = "試験パターン読込...", Width = 150 };
    private readonly Label _profileLabel = new() { Text = "未読込", AutoSize = true, MaximumSize = new Size(290, 0) };
    private readonly Button _runSeqButton = new() { Text = "自動試験開始", Width = 100 };
    private readonly Button _holdButton = new() { Text = "一時停止", Width = 90 };
    private readonly Button _abortButton = new() { Text = "中止", Width = 90 };
    private readonly Label _seqLabel = new() { Text = "-", AutoSize = true, MaximumSize = new Size(290, 0) };

    private readonly TrendChart _speedChart = new() { Title = "回転速度 [rpm]  直近60秒", YMin = 0, YMax = 3600 };
    private readonly TrendChart _loadChart = new() { Title = "トルク / 温度", YMin = 0, YMax = 150 };
    private readonly DataGridView _stepsGrid = new();
    private readonly ListView _eventList = new();

    private void BuildLayout()
    {
        Text = "Motor Bench HMI";
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        Controls.Add(root);

        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        bar.Controls.AddRange([Caption("PLC"), _hostBox, Caption("ポート"), _portBox, _connectButton, _connLabel]);
        root.Controls.Add(bar, 0, 0);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 345));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(main, 0, 1);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        left.Controls.Add(BuildStatusGroup());
        left.Controls.Add(BuildManualGroup());
        left.Controls.Add(BuildAutoGroup());
        main.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        _speedChart.Dock = DockStyle.Fill;
        _loadChart.Dock = DockStyle.Fill;
        _stepsGrid.Dock = DockStyle.Fill;
        right.Controls.Add(_speedChart, 0, 0);
        right.Controls.Add(_loadChart, 0, 1);
        right.Controls.Add(_stepsGrid, 0, 2);
        main.Controls.Add(right, 1, 0);

        SetUpStepsGrid();
        SetUpEventList();
        _eventList.Dock = DockStyle.Fill;
        root.Controls.Add(_eventList, 0, 2);
    }

    private GroupBox BuildStatusGroup()
    {
        var grid = Grid();
        AddRow(grid, "状態", _stateLabel);
        AddRow(grid, "回転速度", _speedLabel);
        AddRow(grid, "トルク", _torqueLabel);
        AddRow(grid, "モーター温度", _tempLabel);
        AddRow(grid, "アラーム", _alarmLabel);
        AddSpanning(grid, Flow(_driveLamp, _brakeLamp, _runLamp, _faultLamp));
        return Group("運転状態", grid);
    }

    private GroupBox BuildManualGroup()
    {
        var grid = Grid();
        AddRow(grid, "速度指令 [rpm]", _speedSpBox);
        AddRow(grid, "負荷指令 [%]", _loadSpBox);
        AddSpanning(grid, Flow(_applyButton));
        AddSpanning(grid, Flow(_startButton, _stopButton, _resetButton));
        AddSpanning(grid, _estopCheck);
        AddSpanning(grid, _runawayCheck);
        return Group("手動操作", grid);
    }

    private GroupBox BuildAutoGroup()
    {
        var grid = Grid();
        AddSpanning(grid, _loadProfileButton);
        AddSpanning(grid, _profileLabel);
        AddSpanning(grid, Flow(_runSeqButton, _holdButton, _abortButton));
        AddSpanning(grid, _seqLabel);
        return Group("自動試験", grid);
    }

    private void SetUpStepsGrid()
    {
        _stepsGrid.AllowUserToAddRows = false;
        _stepsGrid.AllowUserToDeleteRows = false;
        _stepsGrid.AllowUserToResizeRows = false;
        _stepsGrid.ReadOnly = true;
        _stepsGrid.RowHeadersVisible = false;
        _stepsGrid.MultiSelect = false;
        _stepsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _stepsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _stepsGrid.BackgroundColor = SystemColors.Window;

        _stepsGrid.Columns.Add(ColNo, "No");
        _stepsGrid.Columns.Add(ColSpeed, "速度 [rpm]");
        _stepsGrid.Columns.Add(ColLoad, "負荷 [%]");
        _stepsGrid.Columns.Add(ColDwell, "計測 [s]");
        _stepsGrid.Columns.Add(ColSettle, "整定 [s]");
        _stepsGrid.Columns.Add(ColAvgSpeed, "平均速度");
        _stepsGrid.Columns.Add(ColAvgTorque, "平均トルク");
        _stepsGrid.Columns.Add(ColMaxTemp, "最高温度");
        _stepsGrid.Columns.Add(ColStatus, "状態");
        _stepsGrid.Columns[ColNo]!.FillWeight = 40;
    }

    private void SetUpEventList()
    {
        _eventList.View = View.Details;
        _eventList.FullRowSelect = true;
        _eventList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _eventList.Columns.Add("時刻", 150);
        _eventList.Columns.Add("種別", 90);
        _eventList.Columns.Add("内容", 800);
    }

    private const string ColNo = "no";
    private const string ColSpeed = "speed";
    private const string ColLoad = "load";
    private const string ColDwell = "dwell";
    private const string ColSettle = "settle";
    private const string ColAvgSpeed = "avgSpeed";
    private const string ColAvgTorque = "avgTorque";
    private const string ColMaxTemp = "maxTemp";
    private const string ColStatus = "status";

    private static Label ValueLabel() => new()
    {
        AutoSize = true,
        Text = "-",
        Font = new Font(DefaultFont.FontFamily, 12f, FontStyle.Bold),
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 7, 3, 3),
    };

    private static TableLayoutPanel Grid() => new()
    {
        ColumnCount = 2,
        AutoSize = true,
        Dock = DockStyle.Top,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
    };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static void AddRow(TableLayoutPanel grid, string caption, Control value)
    {
        grid.Controls.Add(Caption(caption));
        grid.Controls.Add(value);
    }

    private static void AddSpanning(TableLayoutPanel grid, Control control)
    {
        grid.Controls.Add(control);
        grid.SetColumnSpan(control, 2);
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(325, 0),
            Padding = new Padding(8),
        };
        box.Controls.Add(content);
        return box;
    }
}
