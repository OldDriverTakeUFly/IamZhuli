using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Levels;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Preplay;
using IamZhuli.Simulation.Regulators;
using IamZhuli.Simulation.Scenarios;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;
using Microsoft.AspNetCore.SignalR;

namespace IamZhuli.Web;

/// <summary>
/// 游戏大脑的托管单例。基于关卡定义构建,持有 SimulationLoop + Regulator + 全局锁。
/// 支持加载关卡、监管事件接入、目标进度查询、结算、重试。
/// </summary>
public sealed class GameSingleton
{
    private static readonly ParticipantId Player = new("Player");
    private static readonly ParticipantId MarketMaker = new("做市商");

    private SimulationLoop _loop = null!;
    private Account _player = null!;
    private AIMainForce _ai = null!;
    private MarketDataCollector _collector = null!;
    private Regulator _regulator = null!;
    private LevelJudge _judge = null!;
    private LevelDefinition _level = null!;
    private decimal _maxHeatReached;
    private decimal _initialCash;
    private decimal? _prevPriceForVolatility;
    private RetailProfilePool _retail = null!;
    private InstitutionB _institutionB = null!;
    private PassiveFlow _passive = null!;
    private MarketScenario _scenario = null!;
    private EquityCurveCollector _equityCollector = null!;
    private ChipSnapshotCollector _chipCollector = null!;
    private ReplayCollector _replayCollector = null!;

    private readonly SemaphoreSlim _gate = new(1, 1);
    public IHubContext<GameHub> Hub { get; }
    public bool IsInitialized { get; private set; }
    public LevelResult? LastResult { get; private set; }
    public bool IsLevelOver { get; private set; }
    /// <summary>盘前准备态:LoadLevel 后进入,玩家研究K线/筹码后点"开始操盘"才正式开盘。</summary>
    public bool IsPreMarket { get; private set; } = true;

    public GameSingleton(IHubContext<GameHub> hub)
    {
        Hub = hub;
        // 不默认加载关卡:等玩家选择关卡后才开始预演
    }

    /// <summary>加载关卡:重建整个大脑。</summary>
    public void LoadLevel(LevelDefinition level)
    {
        _level = level;
        _initialCash = level.PlayerCash;
        _maxHeatReached = 0;
        LastResult = null;
        IsLevelOver = false;
        _prevPriceForVolatility = null;

        var intrinsic = new Price(level.IntrinsicValue);
        var rules = new MarketRules
        {
            PreviousClose = intrinsic,
            PriceLimitRatio = 0.10m,
            TickSize = new Price(0.01m),
            FloatShares = new Quantity(level.FloatShares)
        };
        var engine = new MatchingEngine(rules);
        _loop = new SimulationLoop(engine, new SimulationClock(level.TicksPerDay, level.TotalDays));
        _player = _loop.Session.GetOrCreateAccount(Player, level.PlayerCash);
        if (level.PlayerInitialHolding > 0) _player.Position.Seed(new Quantity(level.PlayerInitialHolding), intrinsic);

        // —— 预演:让市场参与者跑完历史K线,状态真实涌现 ——
        _scenario = new MarketScenario(ScenarioType.Decline, new Price(intrinsic.Value * 1.2m), intrinsic);
        var preplay = new MarketPreplay();
        var preplayResult = preplay.Run(_loop.Session, _loop, _scenario, seed: level.Id.GetHashCode());

        // 机构B(做市+风险控制+操盘三合一)
        _institutionB = new InstitutionB(_loop.Session, new ParticipantId("机构B"), intrinsic,
            cash: 1_000_000_000m, initialHolding: level.MarketMakerHolding,
            baseDepthPerLevel: 80, levels: 20, seed: 88);   // 20档:五档之外藏深层挂单,模拟主力暗挂
        _loop.AddParticipant(_institutionB);

        // 散户画像池(情绪用预演产出的初始值)
        _retail = new RetailProfilePool(_loop.Session, new ParticipantId("散户池"), intrinsic, seed: 42);
        _loop.AddParticipant(_retail);

        // 被动资金流(指数ETF/定投/养老金底盘):无视涨跌,每tick小额买入,保证阴跌不死锁
        _passive = new PassiveFlow(_loop.Session, new ParticipantId("被动资金"), level.FloatShares, seed: 77);
        _loop.AddParticipant(_passive);

        _ai = new AIMainForce(_loop.Session, new ParticipantId("AI主力"),
            intrinsic, cash: 100_000_000m, initialHolding: level.AiHolding, initialCost: intrinsic,
            sensitivity: level.AiSensitivity, profile: StrategyProfile.Balanced, seed: 99);
        _loop.AddParticipant(_ai);

        _collector = new MarketDataCollector(_loop, preplayResult.PreviousClose);
        _collector.PreloadHistory(preplayResult.HistoryCandles);
        _regulator = new Regulator(Player);
        _judge = new LevelJudge(level);

        // 接入监管事件
        _loop.Session.OnTradeDetailed += t => _regulator.OnTrade(t,
            t.TakerId.Equals(Player) || t.MakerId.Equals(Player));
        _loop.OnTick += _ =>
        {
            var cur = _loop.Session.Engine.LastPrice;
            decimal? ratio = (_prevPriceForVolatility is { } prev && cur is { } c && prev > 0)
                ? (c.Value - prev) / prev : (decimal?)null;
            _prevPriceForVolatility = cur?.Value;
            _regulator.OnTick(ratio);
            _maxHeatReached = Math.Max(_maxHeatReached, _regulator.Heat);
            // 监管爆表 → 关卡失败
            if (_regulator.GetStatus().IsFailed && !IsLevelOver) EndLevel();
        };
        // 权益曲线采集(供积分系统算回撤/波动率/三方排名)
        _equityCollector = new EquityCurveCollector(_loop, _player,
            () => _ai.Account, () => _institutionB.Account,
            () => _loop.Session.Engine.LastPrice);
        // 筹码快照采集(日终触发,记录各方持仓/成本/净流)
        _chipCollector = new ChipSnapshotCollector(_loop, _loop.Session);
        // 导入预演期间的逐日筹码历史(day重编为负数,与历史K线对齐)
        _chipCollector.ImportHistory(preplayResult.ChipHistory);
        // 复盘数据采集(关键帧快照+交易日志,结算后供回放)
        _replayCollector = new ReplayCollector(_loop, _loop.Session,
            () => new[] { ("玩家", _player), ("AI主力", _ai.Account), ("机构B", _institutionB.Account) },
            () => _regulator.Heat);
        // 挂载待处理的成交事件订阅(关卡选择前 WireEvents 已被调用)
        if (_pendingPushTrade != null)
        {
            var push = _pendingPushTrade;
            _loop.Session.OnTrade += (p, q, s) =>
            {
                try { _ = push(new TradeDto(p.Value, q.Value, s.ToString())); } catch { }
            };
            _pendingPushTrade = null;
        }
        _loop.Start();
        IsPreMarket = true;   // 进入盘前准备态,等玩家研究完点"开始操盘"
        IsInitialized = true;
    }

    /// <summary>玩家结束盘前研究,正式开始交易(退出盘前态)。</summary>
    public void BeginTrading()
    {
        IsPreMarket = false;
        _loop.Resume();
    }

    /// <summary>结束关卡并结算(积分制)。</summary>
    public LevelResult EndLevel()
    {
        if (IsLevelOver) return LastResult!;
        IsLevelOver = true;
        var failed = _regulator.GetStatus().IsFailed;
        var result = _judge.Settle(_loop.Session.Engine.LastPrice, _player,
            _level.FloatShares, _maxHeatReached, _initialCash, failed);
        LastResult = result;
        return result;
    }

    /// <summary>积分制结算:收益率+风险调整+三方排名。</summary>
    public ScoreSettlement SettleScore()
    {
        if (!IsLevelOver) EndLevel();
        var calc = new ScoreCalculator();
        var lastPrice = _loop.Session.Engine.LastPrice;
        var playerScore = calc.Calculate(_player, _equityCollector.PlayerEquity,
            _initialCash, lastPrice, _maxHeatReached);
        var aiScore = calc.Calculate(_ai.Account, _equityCollector.AiEquity,
            100_000_000m, lastPrice, 0);
        var instBScore = calc.Calculate(_institutionB.Account, _equityCollector.InstBEquity,
            1_000_000_000m, lastPrice, 0);
        var ranked = calc.Rank(
            ("玩家", playerScore), ("AI主力", aiScore), ("机构B", instBScore));
        return new ScoreSettlement(ranked.Select(r => new PartyScore(
            r.Name, r.Result.ReturnRate, r.Result.MaxDrawdown,
            r.Result.RiskAdjustedScore, r.Result.Rank, r.Result.Comment)).ToList());
    }

    /// <summary>获取筹码分布历史(每日收盘的价位分布)。day=null 返回全部,否则返回指定日。</summary>
    public List<DayChipDto> GetChipHistory(int? day = null)
    {
        var history = _chipCollector.History;
        if (day.HasValue)
        {
            int idx = day.Value - 1;
            if (idx < 0 || idx >= history.Count) return new();
            return new() { ToDto(history[idx], idx) };
        }
        return history.Select((h, i) => ToDto(h, i)).ToList();
    }

    private DayChipDto ToDto(DayChipDistribution snap, int idx)
    {
        var bands = snap.Bands.Select(b => new PriceBandDto(
            b.PriceLow, b.PriceHigh, b.Quantity,
            snap.TotalQuantity > 0 ? Math.Round((decimal)b.Quantity / snap.TotalQuantity, 4) : 0m)).ToList();
        return new DayChipDto(snap.Day, snap.ClosePrice, snap.TotalQuantity,
            Math.Round(_chipCollector.PeakConcentration(idx), 3), bands);
    }

    /// <summary>获取复盘数据(关键帧快照+交易日志+事件+筹码+K线)。结算后调用。</summary>
    public ReplayDataDto GetReplayData()
    {
        var snapshots = _replayCollector.Snapshots.Select(s => new ReplaySnapshotDto(
            s.TickIndex, s.Day, s.TickOfDay, s.Price, Math.Round(s.RegulatorHeat, 1),
            s.TopBids.Select(b => new PriceLevelDto(b.Price, b.Qty)).ToList(),
            s.TopAsks.Select(a => new PriceLevelDto(a.Price, a.Qty)).ToList(),
            s.Participants.Select(p => new ParticipantStateDto(p.Name, p.Holding, Math.Round(p.AvgCost, 2), Math.Round(p.Equity))).ToList()
        )).ToList();
        var trades = _replayCollector.Trades.Select(t => new ReplayTradeDto(
            t.TickIndex, t.Price, t.Qty, t.TakerSide.ToString(), t.TakerId, t.MakerId)).ToList();
        // 事件:AI内心独白 + 机构B独白 + 监管事件
        var events = new List<ReplayEventDto>();
        foreach (var t in _ai.Thoughts)
            events.Add(new ReplayEventDto((int)t.TotalTick, "AI主力", t.State.ToString(), $"{t.DetectedIntent}({t.Confidence:P0}) {t.Reason}"));
        foreach (var t in _institutionB.Thoughts)
            events.Add(new ReplayEventDto((int)t.Tick, "机构B", t.Level.ToString(), $"{t.Action}: {t.Detail}"));
        foreach (var e in _regulator.EventLog)
            events.Add(new ReplayEventDto(e.Tick, "监管", e.Penalty, $"关注{e.Heat:F0}% {e.Reason}"));
        events = events.OrderBy(e => e.Tick).ToList();
        // 筹码 + K线
        var chips = _chipCollector.History.Select((h, i) => ToDto(h, i)).ToList();
        var candles = _collector.DailyCandles
            .Select(c => new DailyCandleDto(c.Day, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        return new ReplayDataDto(
            _loop.Clock.TotalDays * _loop.Clock.TicksPerDay, _loop.Clock.TotalDays,
            snapshots, trades, events, chips, candles);
    }

    /// <summary>重试关卡(重置到初始)。</summary>
    public void Retry()
    {
        // 事件订阅会随旧 loop 一起丢弃,新 LoadLevel 重新挂载
        LoadLevel(_level);
    }

    /// <summary>推进一个 tick 并返回该 tick 后的快照(由 GameHostService 调用)。
    /// 一次持锁完成 Step + 构建快照,避免事件回调里重新加锁导致死锁。</summary>
    public async Task<MarketSnapshotDto?> StepAsync()
    {
        // 未选关卡:返回空快照(前端显示选关卡界面)
        if (!IsInitialized) return BuildSnapshotUnsafe();
        if (_loop.IsFinished || IsLevelOver) return null;
        await _gate.WaitAsync();
        try
        {
            // 盘前态:不推进 tick,但仍推快照(让玩家看K线/盘口)
            if (IsPreMarket) return BuildSnapshotUnsafe();
            _loop.Step();
            // 关卡时间结束 → 自动结算
            if (_loop.IsFinished && !IsLevelOver) EndLevel();
            return BuildSnapshotUnsafe();
        }
        finally { _gate.Release(); }
    }

    /// <summary>在锁内构建完整快照(供推送/初始加载)。</summary>
    public async Task<MarketSnapshotDto> BuildSnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return BuildSnapshotUnsafe(); }
        finally { _gate.Release(); }
    }

    private MarketSnapshotDto BuildSnapshotUnsafe()
    {
        // 未选关卡:返回空快照,前端据此显示关卡选择界面
        if (!IsInitialized)
        {
            return new MarketSnapshotDto(
                0, 0, 0, 0, "Waiting", false, false, false,
                null, null, null, 0m, 0m, 0m, 0m,
                new(), new(), new AccountDto(0,0,0,0,0,0,0,0),
                new(), null, new(), new(),
                0m, "", "", new(), false, 0m, 0, new());
        }
        var view = _loop.Session.Engine.View;
        var mark = view.LastPrice ?? new Price(10.00m);
        var asks = view.TopAsks(5).Select(t => new PriceLevelDto(t.Price.Value, t.TotalQty.Value)).Reverse().ToList();
        var bids = view.TopBids(5).Select(t => new PriceLevelDto(t.Price.Value, t.TotalQty.Value)).ToList();
        var rules = _loop.Session.Engine.Rules;
        var acc = new AccountDto(
            Cash: _player.Cash,
            AvailableCash: _player.AvailableCash,
            PositionTotal: _player.Position.Total.Value,
            PositionAvailable: _player.Position.Available.Value,
            PositionT1Locked: _player.Position.T1Locked.Value,
            AverageCost: _player.Position.AverageCost.Value,
            TotalEquity: _player.TotalEquity(mark),
            FloatingProfit: _player.Position.FloatingProfit(mark));
        return new MarketSnapshotDto(
            CurrentDay: _loop.Clock.CurrentDay, TotalDays: _loop.Clock.TotalDays,
            TickOfDay: _loop.Clock.CurrentTickOfDay, TicksPerDay: _loop.Clock.TicksPerDay,
            Phase: _loop.Clock.Phase.ToString(), IsPaused: _loop.IsPaused, IsFinished: _loop.IsFinished, IsPreMarket: IsPreMarket,
            LastPrice: view.LastPrice?.Value, BestBid: view.BestBid?.Value, BestAsk: view.BestAsk?.Value,
            UpperLimit: rules.UpperLimit.Value, LowerLimit: rules.LowerLimit.Value,
            PreviousClose: _collector.PreviousClose, TurnoverRate: Math.Round(_collector.TurnoverRate, 2),
            Asks: asks, Bids: bids, Account: acc,
            Timeshare: _collector.TodayTimeshare.Select(t => new TimesharePointDto(t.TickOfDay, t.Price, t.CumVolume)).ToList(),
            TodayCandle: _collector.TodayOpen.HasValue ? new DailyCandleDto(
                _loop.Clock.CurrentDay, Math.Round(_collector.TodayOpen.Value, 2),
                Math.Round(_collector.TodayHigh, 2), Math.Round(_collector.TodayLow, 2),
                Math.Round(_collector.LastPrice, 2), _collector.TodayVolume) : null,
            DailyCandles: _collector.DailyCandles.TakeLast(60)
                .Select(c => new DailyCandleDto(c.Day, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            Macd: _collector.MacdSeries.TakeLast(60)
                .Select(m => new MacdDto(m.Dif, m.Dea, m.Histogram)).ToList(),
            RegulatorHeat: Math.Round(_regulator.Heat, 1),
            PenaltyLevel: _regulator.CurrentPenalty.ToString(),
            LatestRegulatorEvent: _regulator.RecentEvents.Count > 0 ? _regulator.RecentEvents[0] : "",
            Objectives: _judge.EvaluateProgress(view.LastPrice, _player, _level.FloatShares, _maxHeatReached)
                .Select(o => new ObjectiveProgressDto(o.Description, o.Achieved, Math.Round(o.Progress, 2), o.Detail)).ToList(),
            IsLevelOver: IsLevelOver,
            Sentiment: Math.Round(_retail.Sentiment.Value * 100m, 0),     // 0~100 情绪温度计
            RetailActiveCount: _retail.ActiveCount,
            OpenOrders: _loop.Session.GetOpenOrders(Player)
                .Select(o => new OpenOrderDto(o.Id.Value, o.Side.ToString(), o.Price.Value,
                    o.TotalQty.Value, o.FilledQty.Value, o.RemainingQty.Value)).ToList());
    }

    /// <summary>提交下单。返回订单结果;失败返回带 Error 的 DTO。</summary>
    public async Task<OrderResultDto> SubmitAsync(OrderRequestDto dto)
    {
        await _gate.WaitAsync();
        try
        {
            var side = dto.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
            var type = dto.Type.Equals("market", StringComparison.OrdinalIgnoreCase) ? OrderType.Market : OrderType.Limit;
            var price = type == OrderType.Limit ? new Price(dto.Price ?? 0m) : Price.Zero;
            var req = new OrderRequest(Player, side, type, price, new Quantity(dto.Qty));
            var result = _loop.Session.Submit(req);
            return new OrderResultDto(
                result.OrderId.Value, result.FinalStatus.ToString(),
                result.AverageFillPrice.Value, result.TotalFilled.Value, result.RemainingQty.Value, null);
        }
        catch (Exception ex)
        {
            return new OrderResultDto(0, "Failed", 0m, 0, 0, ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CancelAsync(long orderId)
    {
        await _gate.WaitAsync();
        try { return _loop.Session.Cancel(Player, new OrderId(orderId)); }
        finally { _gate.Release(); }
    }

    /// <summary>撤销玩家全部挂单。</summary>
    public async Task<int> CancelAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // 按方向分别撤,确保买单冻结被正确释放
            int n = _loop.Session.CancelAllBySide(Player, Side.Buy);
            n += _loop.Session.CancelAllBySide(Player, Side.Sell);
            return n;
        }
        finally { _gate.Release(); }
    }

    /// <summary>撤销玩家某方向(买/卖)的全部挂单。</summary>
    public async Task<int> CancelBySideAsync(string side)
    {
        await _gate.WaitAsync();
        try
        {
            var s = side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
            return _loop.Session.CancelAllBySide(Player, s);
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try { _loop.Pause(); }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try { _loop.Resume(); }
        finally { _gate.Release(); }
    }

    public async Task SkipDayAsync()
    {
        await _gate.WaitAsync();
        try { _loop.SkipToNextDay(); }
        finally { _gate.Release(); }
    }

    /// <summary>开始下一交易日(日终暂停后,玩家显式触发;T+1解锁在此发生)。</summary>
    public async Task StartNextDayAsync()
    {
        await _gate.WaitAsync();
        try { _loop.StartNextDay(); }
        finally { _gate.Release(); }
    }

    public async Task<LevelResultDto> EndLevelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var r = EndLevel();
            return new LevelResultDto(r.IsVictory, r.Stars, r.CoachComment, r.FailureReason,
                r.Objectives.Select(o => new ObjectiveProgressDto(o.Description, o.Achieved, Math.Round(o.Progress, 2), o.Detail)).ToList());
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync()
    {
        await _gate.WaitAsync();
        try { Retry(); }
        finally { _gate.Release(); }
    }

    /// <summary>按 ID 切换关卡并重新加载。支持 tutorial/accumulate/pump_dump。</summary>
    public async Task LoadLevelAsync(string id)
    {
        var level = id.ToLowerInvariant() switch
        {
            "tutorial" => LevelDefinition.Tutorial(),
            "accumulate" => LevelDefinition.Accumulate(),
            _ => LevelDefinition.PumpAndDump()
        };
        await _gate.WaitAsync();
        try { LoadLevel(level); }
        finally { _gate.Release(); }
    }

    /// <summary>当前关卡信息(供前端展示关卡名/简报)。</summary>
    public object CurrentLevel => new { id = _level.Id, name = _level.Name, briefing = _level.Briefing };

    /// <summary>获取 AI 主力最近的内心独白(调试/复盘用;盘中 AI 持仓仍不可见)。</summary>
    public async Task<List<AIDto>> GetAIThoughtsAsync(int count = 20)
    {
        await _gate.WaitAsync();
        try
        {
            return _ai.Thoughts
                .TakeLast(count)
                .Select(t => new AIDto(t.Day, t.TickOfDay, t.State.ToString(),
                                       t.DetectedIntent.ToString(), t.Confidence, t.Reason))
                .Reverse().ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>订阅成交事件 → SignalR 推送(由 GameHostService 启动时调用一次)。
    /// 关卡未加载时先保存委托,LoadLevel 完成后再挂载(避免 _loop 为 null)。</summary>
    private Func<TradeDto, Task>? _pendingPushTrade;
    public void WireEvents(Func<TradeDto, Task> pushTrade)
    {
        if (_loop != null)
        {
            _loop.Session.OnTrade += (p, q, s) =>
            {
                try { _ = pushTrade(new TradeDto(p.Value, q.Value, s.ToString())); } catch { }
            };
        }
        else
        {
            _pendingPushTrade = pushTrade;   // 关卡加载后再挂
        }
    }
}
