using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants;
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
    /// <summary>当日已收盘(等待玩家开始下一日)。配合"日终自动暂停"机制。</summary>
    public bool IsDayClosed { get; private set; }

    private readonly List<IParticipant> _participants = new();
    private readonly Random _rng = new();

    /// <summary>注册一个参与者(散户群体、AI 主力等)。每 tick 会被驱动 Act。</summary>
    public void AddParticipant(IParticipant participant) => _participants.Add(participant);
    public IReadOnlyList<IParticipant> Participants => _participants;

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

        // —— 注入参与者订单(散户群体、AI 主力) ——
        foreach (var p in _participants)
        {
            try { p.Act(Session, Clock, _rng); }
            catch { /* 单参与者异常不影响整体推进 */ }
        }

        Price? before = Session.Engine.LastPrice;

        // 推进时钟
        bool stillInDay = Clock.AdvanceTick();

        // 发出成交事件(撮合在玩家下单时即时发生,这里收集当 tick 成交——M2 简化为即时撮合模型)
        EmitTradeAndPriceEvents(before);

        OnTick?.Invoke(Clock.TotalTicksElapsed);

        // 日切:tick 跑完后进入"已收盘"并暂停(等待玩家开始下一日)
        // 日内连续流动,日终暂停让玩家喘息/看盘/做盘后操作
        if (!stillInDay)
        {
            IsDayClosed = true;
            IsPaused = true;   // 暂停,等玩家显式 StartNextDay
        }
    }

    /// <summary>推进到下一交易日的内部逻辑(自动跨日用)。</summary>
    private void AdvanceToNextDay()
    {
        Clock.AdvanceDay();
        if (!IsFinished)
        {
            Session.OnNewTradingDay();   // T+1 解锁
            foreach (var p in _participants)
            {
                try { p.OnNewDay(); }
                catch { }
            }
            Clock.Open();
            IsDayClosed = false;
            OnNewDay?.Invoke(Clock.CurrentDay);
        }
    }

    /// <summary>开始下一交易日(玩家在日终暂停后显式触发)。
    /// 流程:挂单清零 → 情绪延续(参与者OnNewDay) → T+1解锁 → 开盘集合竞价 → 连续竞价。</summary>
    public void StartNextDay()
    {
        if (!IsDayClosed || IsFinished) return;
        Clock.AdvanceDay();
        if (!IsFinished)
        {
            // 1. 挂单清零(撤销所有隔夜挂单)
            var removed = Session.Engine.ClearBook();
            // 2. T+1 解锁
            Session.OnNewTradingDay();
            // 3. 参与者 OnNewDay(散户池内含情绪延续逻辑)
            foreach (var p in _participants)
            {
                try { p.OnNewDay(); }
                catch { }
            }
            // 4. 让参与者重新挂单(集合竞价前的意愿单)——清零后盘口空,必须让参与者Act一次
            Clock.Open();
            foreach (var p in _participants)
            {
                try { p.Act(Session, Clock, _rng); }
                catch { }
            }
            // 5. 开盘集合竞价:收集所有挂单,撮出开盘价
            var auction = Session.Engine.CallAuction();
            if (auction is { } result)
                Session.Engine.SetLastPrice(result.Price);   // 确立开盘价
            // 6. 进入连续竞价
            IsDayClosed = false;
            IsPaused = false;
            OnNewDay?.Invoke(Clock.CurrentDay);
        }
    }

    /// <summary>预演专用跨日:自动清零挂单→T+1解锁→情绪延续→参与者Act→集合竞价→开盘。
    /// 供 MarketPreplay 用(不暂停,自动连续跑完历史)。</summary>
    public void PreplayAdvanceDay()
    {
        if (!IsDayClosed) return;
        Session.Engine.ClearBook();
        Clock.AdvanceDay();
        if (!IsFinished)
        {
            Session.OnNewTradingDay();
            foreach (var p in _participants) { try { p.OnNewDay(); } catch { } }
            Clock.Open();
            foreach (var p in _participants) { try { p.Act(Session, Clock, _rng); } catch { } }
            var auction = Session.Engine.CallAuction();
            if (auction is { } r) Session.Engine.SetLastPrice(r.Price);
            IsDayClosed = false;
            IsPaused = false;
            OnNewDay?.Invoke(Clock.CurrentDay);
        }
    }

    /// <summary>一键跳到当日收盘(跑完当日剩余tick,停在收盘暂停状态)。</summary>
    public void SkipToNextDay()
    {
        if (IsFinished || IsDayClosed) return;
        while (!IsDayClosed && !IsFinished)
        {
            IsPaused = false;
            Step();
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
