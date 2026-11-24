namespace MotorBench.Plc;

public enum BenchState
{
    Idle = 0,
    Running = 1,
    Stopping = 2,
    Fault = 3,
}

public enum AlarmCode
{
    None = 0,
    EStop = 1,
    Overspeed = 2,
    OverTemperature = 3,
    CommLoss = 4,
}

public static class BenchText
{
    public static string Describe(BenchState state) => state switch
    {
        BenchState.Idle => "待機",
        BenchState.Running => "運転中",
        BenchState.Stopping => "減速停止中",
        BenchState.Fault => "異常",
        _ => $"不明 ({(int)state})",
    };

    public static string Describe(AlarmCode alarm) => alarm switch
    {
        AlarmCode.None => "",
        AlarmCode.EStop => "非常停止",
        AlarmCode.Overspeed => "過速度",
        AlarmCode.OverTemperature => "モーター過温度",
        AlarmCode.CommLoss => "通信異常 (HMIウォッチドッグ)",
        _ => $"不明なアラーム ({(int)alarm})",
    };
}
