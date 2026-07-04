using IamZhuli.Core;

namespace IamZhuli.Simulation.AI;

/// <summary>AI 主力的状态。</summary>
public enum AIState
{
    /// <summary>观察:无明确信号,偶尔小单试探。</summary>
    Observe,
    /// <summary>护盘:价格跌破自身成本区,在下方挂大买单托住。</summary>
    Defend,
    /// <summary>洗盘:识别玩家建仓/跟风盘多,主动打压逼止损。</summary>
    Wash,
    /// <summary>出货:自身仓位重且价格有利,分批卖出。</summary>
    Distribute,
    /// <summary>跟风:识别玩家在拉升且方向确定,顺势加一把火。</summary>
    Follow,
    /// <summary>反杀:高置信度识破玩家意图,反向操作。</summary>
    Counter
}

/// <summary>AI 状态机的输入快照。</summary>
public readonly record struct AIContext(
    IntentAssessment Intent,
    AIState CurrentState,
    Price? LastPrice,
    Price SelfCost,         // AI 自身持仓成本
    int SelfHolding,        // AI 总持仓(手)
    int SelfAvailable,      // AI 可卖持仓
    Price IntrinsicValue,
    decimal UpperLimit,
    decimal LowerLimit,
    int TickOfDay);

/// <summary>一次状态决策的结果。</summary>
public readonly record struct AIDecision(
    AIState NewState,
    string Reason);         // 切换理由(内心独白)

/// <summary>
/// AI 主力状态机。根据意图识别 + 自身持仓 + 价格水平决定下一个状态。
/// 转换逻辑集中在 Transition,便于测试和调参。
/// </summary>
public sealed class AIStateMachine
{
    public AIState State { get; private set; } = AIState.Observe;
    public int TicksInState { get; private set; }
    /// <summary>反应灵敏度 0~1,越高越快切换、越激进。难度关卡提升此值。</summary>
    public double Sensitivity { get; set; } = 0.5;
    /// <summary>策略偏好。</summary>
    public StrategyProfile Profile { get; set; } = StrategyProfile.Balanced;

    public void Reset() { State = AIState.Observe; TicksInState = 0; }

    /// <summary>评估并切换状态。返回决策(含新状态和理由)。</summary>
    public AIDecision Transition(AIContext ctx)
    {
        TicksInState++;
        var prev = State;
        var next = Decide(ctx);

        if (next != prev)
        {
            var reason = DescribeTransition(prev, next, ctx);
            State = next;
            TicksInState = 0;
            return new AIDecision(next, reason);
        }
        return new AIDecision(State, $"维持{State}({TicksInState}tick)");
    }

    private AIState Decide(AIContext c)
    {
        var intent = c.Intent;
        double conf = intent.Confidence * Sensitivity;

        // —— 自身持仓重 + 价格高位 → 出货优先(落袋为安) ——
        if (c.SelfHolding > 5000 && c.LastPrice is { } lp && lp.Value > c.SelfCost.Value * 1.05m)
            return AIState.Distribute;

        // —— 价格跌破自身成本 → 护盘(保命) ——
        if (c.SelfHolding > 1000 && c.LastPrice is { } lp2 && lp2.Value < c.SelfCost.Value * 0.97m)
            return AIState.Defend;

        // —— 高置信度识破玩家意图 → 反杀 ——
        if (conf > 0.65)
        {
            // 玩家在拉升 → 如果我仓位轻,跟风搭车;仓位重则在高位出货
            if (intent.Primary == PlayerIntent.PushingUp)
                return c.SelfHolding > 3000 ? AIState.Distribute : AIState.Follow;
            // 玩家在吸筹 → 反向洗盘,逼他成本抬高或洗出跟风盘
            if (intent.Primary == PlayerIntent.Accumulating)
                return Profile == StrategyProfile.Aggressive ? AIState.Wash : AIState.Observe;
            // 玩家在出货 → 反杀砸盘,趁火打劫
            if (intent.Primary == PlayerIntent.Distributing && c.SelfAvailable > 0)
                return AIState.Counter;
        }

        // —— 中等置信度:温和跟随或观察 ——
        if (conf > 0.35)
        {
            if (intent.Primary == PlayerIntent.PushingUp) return AIState.Follow;
            if (intent.Primary == PlayerIntent.WashTrading && c.SelfHolding > 2000) return AIState.Defend;
        }

        // —— 默认观察 ——
        // 已在非观察态且理由仍存在时,维持一会(避免抖动),超过耐心阈值才回 Observe
        if (State != AIState.Observe && TicksInState < PatienceTicks) return State;
        return AIState.Observe;
    }

    private int PatienceTicks => Profile == StrategyPatient() ? 40 : 15;
    private static StrategyProfile StrategyPatient() => StrategyProfile.Conservative;

    private static string DescribeTransition(AIState from, AIState to, AIContext c)
        => to switch
        {
            AIState.Defend => $"→护盘:现价{c.LastPrice}跌破我成本{c.SelfCost}的3%,我得托住",
            AIState.Distribute => $"→出货:我有{c.SelfHolding}手且现价{c.LastPrice}高于成本5%,落袋为安",
            AIState.Wash => $"→洗盘:识别到{c.Intent.Primary}(置信{c.Intent.Confidence:P0}),打压逼止损",
            AIState.Follow => $"→跟风:玩家在拉升({c.Intent.Reason}),我搭个便车",
            AIState.Counter => $"→反杀:识破玩家{c.Intent.Primary},趁火打劫",
            AIState.Observe => $"→观察:无明显机会",
            _ => $"→{to}"
        };
}

/// <summary>AI 策略偏好。</summary>
public enum StrategyProfile
{
    Conservative,   // 保守:慢、稳、多观察
    Balanced,       // 平衡
    Aggressive      // 激进:快、爱洗盘反杀
}
