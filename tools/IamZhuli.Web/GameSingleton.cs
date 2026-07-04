using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Levels;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Regulators;
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

    private readonly SemaphoreSlim _gate = new(1, 1);
    public IHubContext<GameHub> Hub { get; }
    public bool IsInitialized { get; private set; }
    public LevelResult? LastResult { get; private set; }
    public bool IsLevelOver { get; private set; }

    public GameSingleton(IHubContext<GameHub> hub)
    {
        Hub = hub;
        LoadLevel(LevelDefinition.PumpAndDump());   // 默认关卡
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

        // 机构B(做市+风险控制+操盘三合一):取代无限做市商
        // 正常时做市提供流动性,风险升高时收紧,盘口深度成动态稀缺资源
        _institutionB = new InstitutionB(_loop.Session, new ParticipantId("机构B"), intrinsic,
            cash: 1_000_000_000m, initialHolding: level.MarketMakerHolding,
            baseDepthPerLevel: 80, levels: 8, seed: 88);
        _loop.AddParticipant(_institutionB);

        // 散户画像池(动态进出,每画像独立账户)—— 取代旧的固定4群体
        _retail = new RetailProfilePool(_loop.Session, new ParticipantId("散户池"), intrinsic, seed: 42);
        _loop.AddParticipant(_retail);

        _ai = new AIMainForce(_loop.Session, new ParticipantId("AI主力"),
            intrinsic, cash: 100_000_000m, initialHolding: level.AiHolding, initialCost: intrinsic,
            sensitivity: level.AiSensitivity, profile: StrategyProfile.Balanced, seed: 99);
        _loop.AddParticipant(_ai);

        _collector = new MarketDataCollector(_loop, level.IntrinsicValue);
        // 生成历史K线作为背景(玩家进场即看到过去走势)
        var history = new MarketHistory(intrinsic);
        history.Generate(days: 30, dailyVolatility: 0.025m, seed: level.Id.GetHashCode());
        var historyCandles = history.Candles.Select((c, i) => new DailyCandle(
            i - 30, Math.Round(c.Open, 2), Math.Round(c.High, 2),
            Math.Round(c.Low, 2), Math.Round(c.Close, 2), c.Volume)).ToList();
        _collector.PreloadHistory(historyCandles);
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
        _loop.Start();
        IsInitialized = true;
    }

    /// <summary>结束关卡并结算。</summary>
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
        if (_loop.IsFinished || IsLevelOver) return null;
        await _gate.WaitAsync();
        try
        {
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
            Phase: _loop.Clock.Phase.ToString(), IsPaused: _loop.IsPaused, IsFinished: _loop.IsFinished,
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
            RetailActiveCount: _retail.ActiveCount);
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
    /// 快照推送改为在 StepAsync 返回后由 GameHostService 直接推送,避免回调里加锁死锁。</summary>
    public void WireEvents(Func<TradeDto, Task> pushTrade)
    {
        _loop.Session.OnTrade += (p, q, s) =>
        {
            try { _ = pushTrade(new TradeDto(p.Value, q.Value, s.ToString())); } catch { }
        };
    }
}
