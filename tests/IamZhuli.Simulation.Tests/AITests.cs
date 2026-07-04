using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// AI 状态机与意图识别器测试。聚焦状态切换逻辑(给定条件进入正确状态)。
/// </summary>
public class AITests
{
    private static AIContext Ctx(PlayerIntent intent, double conf, AIState cur,
        decimal? lastPrice, decimal selfCost, int holding, int available,
        decimal intrinsic = 10m, decimal upper = 11m, decimal lower = 9m)
        => new(new IntentAssessment { Primary = intent, Confidence = conf }, cur,
               lastPrice.HasValue ? new Price(lastPrice.Value) : null,
               new Price(selfCost), holding, available, new Price(intrinsic), upper, lower, 10);

    [Fact]
    public void StateMachine_PriceBelowCost_TransitionsToDefend()
    {
        // AI 持仓2000手成本10,现价9.5(跌破3%)→ 护盘
        var sm = new AIStateMachine { Sensitivity = 0.5 };
        var d = sm.Transition(Ctx(PlayerIntent.None, 0, AIState.Observe, 9.5m, 10m, 2000, 2000));
        Assert.Equal(AIState.Defend, d.NewState);
    }

    [Fact]
    public void StateMachine_HighPriceWithHeavyHolding_TransitionsToDistribute()
    {
        // AI 持仓6000手成本10,现价10.8(高于成本5%)→ 出货
        var sm = new AIStateMachine { Sensitivity = 0.5 };
        var d = sm.Transition(Ctx(PlayerIntent.None, 0, AIState.Observe, 10.8m, 10m, 6000, 6000));
        Assert.Equal(AIState.Distribute, d.NewState);
    }

    [Fact]
    public void StateMachine_PlayerPushingUp_WithLightHolding_Follows()
    {
        // 玩家在拉升(高置信),AI 仓位轻 → 跟风
        var sm = new AIStateMachine { Sensitivity = 0.9 };
        var d = sm.Transition(Ctx(PlayerIntent.PushingUp, 0.85, AIState.Observe, 10.3m, 10m, 500, 500));
        Assert.Equal(AIState.Follow, d.NewState);
    }

    [Fact]
    public void StateMachine_PlayerPushingUp_WithHeavyHolding_Distributes()
    {
        // 玩家在拉升,AI 仓位重(>3000)→ 借机出货
        var sm = new AIStateMachine { Sensitivity = 0.9 };
        var d = sm.Transition(Ctx(PlayerIntent.PushingUp, 0.85, AIState.Observe, 10.3m, 10m, 4000, 4000));
        Assert.Equal(AIState.Distribute, d.NewState);
    }

    [Fact]
    public void StateMachine_PlayerAccumulating_AggressiveAI_Washes()
    {
        // 激进 AI 识别玩家吸筹(高置信)→ 洗盘
        var sm = new AIStateMachine { Sensitivity = 0.9, Profile = StrategyProfile.Aggressive };
        var d = sm.Transition(Ctx(PlayerIntent.Accumulating, 0.85, AIState.Observe, 10.1m, 10m, 1000, 1000));
        Assert.Equal(AIState.Wash, d.NewState);
    }

    [Fact]
    public void StateMachine_PlayerDistributing_WithHolding_Counters()
    {
        // 识别玩家出货(高置信),AI 有持仓 → 反杀
        var sm = new AIStateMachine { Sensitivity = 0.9 };
        var d = sm.Transition(Ctx(PlayerIntent.Distributing, 0.85, AIState.Observe, 10.5m, 10m, 3000, 3000));
        Assert.Equal(AIState.Counter, d.NewState);
    }

    [Fact]
    public void StateMachine_LowConfidence_NoAction_StaysOrObserve()
    {
        // 低置信度 → 不应该进入激进状态
        var sm = new AIStateMachine { Sensitivity = 0.3 };
        var d = sm.Transition(Ctx(PlayerIntent.PushingUp, 0.3, AIState.Observe, 10.1m, 10m, 500, 500));
        Assert.True(d.NewState is AIState.Observe or AIState.Follow, $"低置信度不应激进,实际{d.NewState}");
    }

    [Fact]
    public void IntentRecognizer_DetectsPriceSpike()
    {
        // 灌入快速上涨序列(直接喂数据给 tracker),应检测到正动量
        var rec = new IntentRecognizer(window: 20);
        // 模拟 20 tick:价格从 10 涨到 10.5,买盘厚于卖盘(失衡)
        for (int t = 0; t < 20; t++)
        {
            decimal p = 10m + t * 0.025m;
            rec.Tracker.RecordTick(new Price(p), new Price(p - 0.01m), new Price(p + 0.01m),
                                    bidDepth: 1000, askDepth: 200);
        }
        var a = rec.Assess();
        Assert.True(a.Momentum > 0.02m, $"应检测到上涨动量,实际{a.Momentum}");
        Assert.True(a.Primary != PlayerIntent.None, "应识别到某种意图");
    }

    [Fact]
    public void RecentMarketTracker_ComputesMomentum()
    {
        var t = new RecentMarketTracker(10);
        for (int i = 0; i < 10; i++) t.RecordTick(new Price(10m + i * 0.1m), null, null, 0, 0);
        var m = t.Momentum;
        Assert.True(m > 0.05m, $"10个tick涨了1元,动量应>5%,实际{m}");
    }

    [Fact]
    public void AIMainForce_Initializes_HoldingInvisible()
    {
        // AI 初始持仓存在,但只能通过 AI 自己的账户访问(玩家无法看到)
        var MM = new ParticipantId("MM");
        var AI = new ParticipantId("AI");
        var rules = new MarketRules { PreviousClose = new Price(10m) };
        var s = new TradingSession(new MatchingEngine(rules));
        s.GetOrCreateAccount(MM, 1_000_000_000m).Position.Seed(new Quantity(100000), new Price(10m));
        var ai = new AIMainForce(s, AI, new Price(10m), 100_000_000m, 10000, new Price(10m));
        Assert.Equal(10000, ai.Account.Position.Total.Value);
        Assert.Equal(AIState.Observe, ai.CurrentState);
        Assert.Empty(ai.Thoughts);   // 初始无决策记录
    }
}
