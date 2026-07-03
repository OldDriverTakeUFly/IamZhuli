using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation;

/// <summary>盘口成交事件(每 tick 撮合后发出,供 UI/SimCli 订阅)。</summary>
public readonly record struct TradeEvent(long Tick, Price Price, Quantity Quantity, Side TakerSide);

/// <summary>现价变动事件。</summary>
public readonly record struct PriceChangeEvent(long Tick, Price OldPrice, Price NewPrice);

/// <summary>
/// 仿真主循环。整合时钟、交易会话,驱动 tick 推进与日切。
/// 每个 tick:处理本 tick 内提交的订单 → 撮合 → 发出事件。
/// M2 阶段参与者只有玩家(手动);M3/M4 散户与 AI 由 SimulationLoop 在每 tick 注入。
/// </summary>
public sealed class SimulationLoop
{
    public SimulationClock Clock { get; }
    public TradingSession Session { get; }
    public bool IsPaused { get; private set; } = false;
    public bool IsFinished => Clock.IsTradingFinished;

    /// <summary>每个 tick 结束后触发(参数=当前 tick 序号)。</summary>
    public event Action<long>? OnTick;
    /// <summary>现价变动触发。</summary>
    public event Action<PriceChangeEvent>? OnPriceChange;
    /// <summary>进入新交易日触发(参数=第几日)。</summary>
    public event Action<int>? OnNewDay;

    private Price? _lastEmittedPrice;

    public SimulationLoop(MatchingEngine engine, SimulationClock? clock = null)
    {
        Clock = clock ?? new SimulationClock();
        Session = new TradingSession(engine);
    }

    /// <summary>开始关卡:进入第 1 日开盘。</summary>
    public void Start() => Clock.Open();

    /// <summary>暂停/恢复(暂停只是冻结推进,不影响已挂单)。</summary>
    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;

    /// <summary>推进一个 tick。撮合本 tick 待处理订单,发出事件,处理日切。</summary>
    public void Step()
    {
        if (IsFinished || IsPaused) return;

        // 注入点:M3 散户、M4 AI 在此注入订单(本 M2 无,由玩家在 tick 间手动下单)
        // InjectParticipants();

        Price? before = Session.Engine.LastPrice;

        // 推进时钟
        bool stillInDay = Clock.AdvanceTick();

        // 发出成交事件(撮合在玩家下单时即时发生,这里收集当 tick 成交——M2 简化为即时撮合模型)
        EmitTradeAndPriceEvents(before);

        OnTick?.Invoke(Clock.TotalTicksElapsed);

        // 日切
        if (!stillInDay)
        {
            Clock.AdvanceDay();
            if (!IsFinished)
            {
                Session.OnNewTradingDay();   // T+1 解锁
                Clock.Open();
                OnNewDay?.Invoke(Clock.CurrentDay);
            }
        }
    }

    /// <summary>一键跳到下一交易日(日间跳过)。</summary>
    public void SkipToNextDay()
    {
        if (IsFinished) return;
        Clock.AdvanceDay();
        if (!IsFinished)
        {
            Session.OnNewTradingDay();
            Clock.Open();
            OnNewDay?.Invoke(Clock.CurrentDay);
        }
    }

    private void EmitTradeAndPriceEvents(Price? before)
    {
        // M2 即时撮合模型:成交在下单时已发生,事件由 Session.Submit 内部触发更合适。
        // 这里只负责现价变动事件。
        var after = Session.Engine.LastPrice;
        if (after is { } newPrice && (_lastEmittedPrice == null || _lastEmittedPrice != newPrice))
        {
            if (before is { } old && old != newPrice)
                OnPriceChange?.Invoke(new PriceChangeEvent(Clock.TotalTicksElapsed, old, newPrice));
            _lastEmittedPrice = newPrice;
        }
    }
}
