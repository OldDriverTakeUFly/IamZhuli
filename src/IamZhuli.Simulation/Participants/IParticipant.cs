using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 市场参与者接口。每个 tick 由 SimulationLoop 调用 Act,参与者根据盘口/自身状态生成订单注入会话。
/// M3 的实现是散户群体;M4 将增加 AI 主力。
/// </summary>
public interface IParticipant
{
    ParticipantId Id { get; }

    /// <summary>每个 tick 的决策:观察盘口,生成订单注入会话。</summary>
    void Act(TradingSession session, SimulationClock clock, Random rng);

    /// <summary>日切回调:更新群体状态(如 T+1 解锁后重置成本基准)。</summary>
    void OnNewDay() { }
}

/// <summary>
/// 散户市场环境快照(参与者决策时读取,避免反复查引擎)。
/// </summary>
public readonly record struct MarketView(
    Price? LastPrice,
    Price? BestBid,
    Price? BestAsk,
    decimal UpperLimit,
    decimal LowerLimit,
    int TickOfDay,
    int TicksPerDay);
