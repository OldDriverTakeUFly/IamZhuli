using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.AI;

/// <summary>
/// AI 主力:玩家真正的对手。
/// 组合 IntentRecognizer(看盘)+ AIStateMachine(决策)+ 各状态行为模板(执行)。
/// 持仓与成本对玩家不可见(像真实市场);每步决策记录"内心独白"供复盘。
/// </summary>
public sealed class AIMainForce : IParticipant
{
    public ParticipantId Id { get; }
    private readonly Account _account;
    private readonly IntentRecognizer _recognizer;
    private readonly AIStateMachine _brain;
    private readonly Price _intrinsic;
    private readonly int _strength;
    private readonly Random _rng;

    /// <summary>内心独白日志(每步决策的理由),供 M6 复盘。</summary>
    public List<AILogEntry> Thoughts { get; } = new();
    public AIState CurrentState => _brain.State;
    public Account Account => _account;

    public AIMainForce(TradingSession session, ParticipantId id, Price intrinsicValue,
                       decimal cash, int initialHolding, Price initialCost,
                       double sensitivity = 0.5, StrategyProfile profile = StrategyProfile.Balanced,
                       int? seed = null)
    {
        Id = id;
        _intrinsic = intrinsicValue;
        _account = session.GetOrCreateAccount(id, cash);
        if (initialHolding > 0) _account.Position.Seed(new Quantity(initialHolding), initialCost);
        _recognizer = new IntentRecognizer();
        // 订阅成交事件,把真实成交量喂给识别器(让放量识别准确)
        session.OnTrade += (p, q, s) => _recognizer.RecordTrade(q.Value);
        _brain = new AIStateMachine { Sensitivity = sensitivity, Profile = profile };
        _strength = 1500;   // 单次操作量级基准
        _rng = new Random(seed ?? Environment.TickCount);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        // 1. 观察
        _recognizer.Observe(session);

        // 2. 识别意图
        var intent = _recognizer.Assess();

        // 3. 状态机决策
        var ctx = new AIContext(
            Intent: intent,
            CurrentState: _brain.State,
            LastPrice: session.Engine.View.LastPrice,
            SelfCost: _account.Position.AverageCost,
            SelfHolding: _account.Position.Total.Value,
            SelfAvailable: _account.Position.Available.Value,
            IntrinsicValue: _intrinsic,
            UpperLimit: session.Engine.Rules.UpperLimit.Value,
            LowerLimit: session.Engine.Rules.LowerLimit.Value,
            TickOfDay: clock.CurrentTickOfDay);
        var decision = _brain.Transition(ctx);

        // 4. 记录内心独白(每隔几 tick 或状态切换时记)
        if (decision.NewState != ctx.CurrentState || clock.CurrentTickOfDay % 10 == 0)
        {
            Thoughts.Add(new AILogEntry(clock.TotalTicksElapsed, clock.CurrentDay, clock.CurrentTickOfDay,
                decision.NewState, intent.Primary, intent.Confidence, decision.Reason));
        }

        // 5. 执行行为
        try { ExecuteState(decision.NewState, session, clock); }
        catch { /* 异常不影响整体 */ }
    }

    /// <summary>各状态的行为模板。</summary>
    private void ExecuteState(AIState state, TradingSession session, SimulationClock clock)
    {
        var view = session.Engine.View;
        var price = view.LastPrice ?? view.BestBid ?? view.BestAsk ?? _intrinsic;
        switch (state)
        {
            case AIState.Observe:   // 偶尔小单试探
                if (_rng.NextDouble() < 0.1) Probe(session, price);
                break;
            case AIState.Defend:    // 下方挂大买单托住
                DefendPrice(session, price);
                break;
            case AIState.Wash:      // 主动打压,逼止损
                WashOut(session, price);
                break;
            case AIState.Distribute: // 分批卖出
                Distribute(session, price);
                break;
            case AIState.Follow:    // 顺势加把火
                FollowTrend(session, price);
                break;
            case AIState.Counter:   // 反向砸盘
                CounterAttack(session, price);
                break;
        }
    }

    // —— 行为模板 ——
    private void Probe(TradingSession s, Price price)
    {
        // 小单挂在买一或卖一,探测盘口深浅
        bool buy = _rng.NextDouble() > 0.5;
        int qty = _rng.Next(1, 5) * 10;
        var p = buy ? (s.Engine.View.BestBid ?? price) : (s.Engine.View.BestAsk ?? price);
        Submit(s, buy, p, qty);
    }

    private void DefendPrice(TradingSession s, Price price)
    {
        // 在现价下方1-2档挂大买单(护盘墙)
        var bid = s.Engine.View.BestBid ?? price;
        for (int i = 1; i <= 2; i++)
        {
            var p = new Price(Math.Max(bid.Value - i * 0.02m, s.Engine.Rules.LowerLimit.Value));
            Submit(s, true, p, _strength / 3);
        }
    }

    private void WashOut(TradingSession s, Price price)
    {
        // 用市价或低价卖单打压,逼散户止损;有持仓才砸得动
        if (_account.Position.Available.Value < 50) return;
        if (_rng.NextDouble() < 0.4)
        {
            int qty = Math.Min(_account.Position.Available.Value / 4, _strength / 2);
            qty = Math.Max(10, (qty / 10) * 10);
            // 市价卖,直接砸
            s.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty)));
        }
    }

    private void Distribute(TradingSession s, Price price)
    {
        // 高位分批卖出,挂在卖一上方,慢慢出
        if (_account.Position.Available.Value < 50) return;
        var ask = s.Engine.View.BestAsk ?? price;
        if (_rng.NextDouble() < 0.3)
        {
            int qty = Math.Min(_account.Position.Available.Value / 6, _strength / 3);
            qty = Math.Max(10, (qty / 10) * 10);
            Submit(s, false, ask, qty);
        }
    }

    private void FollowTrend(TradingSession s, Price price)
    {
        // 顺势买入,搭玩家拉升的便车
        if (_rng.NextDouble() < 0.25)
        {
            var ask = s.Engine.View.BestAsk ?? price;
            int qty = _rng.Next(2, 6) * 10;
            Submit(s, true, ask, qty);
        }
    }

    private void CounterAttack(TradingSession s, Price price)
    {
        // 识破玩家出货 → 砸盘,趁火打劫
        if (_account.Position.Available.Value < 100) return;
        if (_rng.NextDouble() < 0.5)
        {
            int qty = Math.Min(_account.Position.Available.Value / 3, _strength);
            qty = Math.Max(20, (qty / 10) * 10);
            s.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty)));
        }
    }

    private void Submit(TradingSession s, bool buy, Price price, int qty)
    {
        if (qty <= 0) return;
        var p = new Price(Math.Round(price.Value, 2));
        var side = buy ? Side.Buy : Side.Sell;
        try { s.Submit(new OrderRequest(Id, side, OrderType.Limit, p, new Quantity(qty))); }
        catch { /* 资金/持仓不足,忽略 */ }
    }

    public void OnNewDay() { /* T+1 解锁由 Session 统一处理 */ }
}

/// <summary>AI 内心独白的一条记录。</summary>
public readonly record struct AILogEntry(
    long TotalTick, int Day, int TickOfDay,
    AIState State, PlayerIntent DetectedIntent, double Confidence, string Reason);
