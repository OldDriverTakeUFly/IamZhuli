using IamZhuli.Core;

namespace IamZhuli.Simulation.Time;

/// <summary>日内阶段。</summary>
public enum SessionPhase
{
    /// <summary>盘前(POC 空白,二期信息战)。</summary>
    PreOpen,
    /// <summary>连续竞价(上午)。</summary>
    Morning,
    /// <summary>午间休市。</summary>
    LunchBreak,
    /// <summary>连续竞价(下午)。</summary>
    Afternoon,
    /// <summary>收盘/盘后。</summary>
    Closed
}

/// <summary>
/// 时间系统。tick 为最小推进单位;一个交易日 = TicksPerDay 个 tick(POC 默认 300)。
/// 30 个交易日 = 一个关卡。tick 预算制:玩家用暂停/加速控制推进速度。
/// </summary>
public sealed class SimulationClock
{
    /// <summary>每个交易日的 tick 数。</summary>
    public int TicksPerDay { get; }
    /// <summary>关卡总交易日数。</summary>
    public int TotalDays { get; }
    /// <summary>上午 tick 数(剩余归下午)。默认 55%(约对应 9:30-11:30 比 13:00-15:00)。</summary>
    public int MorningTicks { get; }

    public int CurrentDay { get; private set; } = 1;       // 1-based
    public int CurrentTickOfDay { get; private set; }      // 0..TicksPerDay-1
    public long TotalTicksElapsed { get; private set; }
    public SessionPhase Phase { get; private set; } = SessionPhase.PreOpen;

    public bool IsLastDay => CurrentDay >= TotalDays;
    public bool IsTradingFinished => CurrentDay > TotalDays;

    public SimulationClock(int ticksPerDay = 300, int totalDays = 30)
    {
        if (ticksPerDay <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerDay));
        if (totalDays <= 0) throw new ArgumentOutOfRangeException(nameof(totalDays));
        TicksPerDay = ticksPerDay;
        TotalDays = totalDays;
        MorningTicks = (int)(ticksPerDay * 0.55);
    }

    public SessionPhase PhaseAt(int tickOfDay) => tickOfDay switch
    {
        _ when tickOfDay < 0 => SessionPhase.PreOpen,
        _ when tickOfDay < MorningTicks => SessionPhase.Morning,
        _ when tickOfDay < MorningTicks + TicksPerDay / 20 => SessionPhase.LunchBreak, // 5% 午休
        _ when tickOfDay < TicksPerDay => SessionPhase.Afternoon,
        _ => SessionPhase.Closed
    };

    /// <summary>进入开盘(从盘前→上午第 0 tick)。</summary>
    public void Open()
    {
        CurrentTickOfDay = 0;
        Phase = PhaseAt(0);
    }

    /// <summary>推进一个 tick。返回是否仍在当日交易时段(否则需日切)。</summary>
    public bool AdvanceTick()
    {
        TotalTicksElapsed++;
        CurrentTickOfDay++;
        Phase = PhaseAt(CurrentTickOfDay);
        return CurrentTickOfDay < TicksPerDay;
    }

    /// <summary>日切:进入下一交易日盘前,CurrentDay+1。</summary>
    public void AdvanceDay()
    {
        CurrentDay++;
        CurrentTickOfDay = 0;
        Phase = SessionPhase.PreOpen;
    }

    public override string ToString() => $"第{CurrentDay}/{TotalDays}日 tick{CurrentTickOfDay}/{TicksPerDay} {Phase}";
}
