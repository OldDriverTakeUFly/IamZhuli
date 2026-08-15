using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Cards;
using IamZhuli.Simulation.Levels;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Preplay;
using IamZhuli.Simulation.Regulators;
using IamZhuli.Simulation.Scenarios;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Presenter;

/// <summary>
/// 游戏大脑(引擎无关):关卡构建、tick 推进、卡牌回合与集中撮合、消息战、结算、快照构建。
/// Web(GameSingleton)与 Unity(GameHost)共用,消除双份逻辑漂移。
/// 线程模型:单线程亲和,自身不加锁——Web 用信号量包,Unity 主线程直调。
/// </summary>
public sealed class GameCore
{
    private static readonly ParticipantId Player = new("Player");

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
    private NewsSystem _newsSystem = null!;
    private WaterArmyNetwork _waterArmy = null!;
    private BlockTradeSignal _blockSignal = null!;
    private CardEngine? _cardEngine;
    /// <summary>上回合揭牌结果(集中撮合价/量/逐笔,随快照推送)。</summary>
    private AuctionResultDto? _lastAuction;

    /// <summary>是否启用卡牌模式(完全替换实时操作)。</summary>
    public bool CardMode { get; set; } = true;

    public bool IsInitialized { get; private set; }
    public LevelResult? LastResult { get; private set; }
    public bool IsLevelOver { get; private set; }
    /// <summary>盘前准备态:LoadLevel 后进入,玩家研究K线/筹码后点"开始操盘"才正式开盘。</summary>
    public bool IsPreMarket { get; private set; } = true;
    /// <summary>上回合揭牌结果(只读,供表现层直接读取)。</summary>
    public AuctionResultDto? LastAuction => _lastAuction;
    /// <summary>交易会话(下单/撤单/查询的直接入口)。</summary>
    public TradingSession Session => _loop.Session;
    /// <summary>模拟主循环(时钟/暂停/日切)。</summary>
    public SimulationLoop Loop => _loop;

    /// <summary>每笔成交触发(表现层订阅推送;关卡未加载时订阅安全,无触发)。</summary>
    public event Action<TradeDto>? TradePushed;

    // ══════════════════════════════════════
    // 关卡加载与生命周期
    // ══════════════════════════════════════

    /// <summary>按 ID 加载关卡。支持 tutorial/accumulate/wash_pump/adversarial/pump_dump。</summary>
    public void LoadLevelById(string id)
    {
        var level = id.ToLowerInvariant() switch
        {
            "tutorial" => LevelDefinition.Tutorial(),
            "accumulate" => LevelDefinition.Accumulate(),
            "wash_pump" => LevelDefinition.WashAndPump(),
            "adversarial" => LevelDefinition.Adversarial(),
            _ => LevelDefinition.PumpAndDump()
        };
        LoadLevel(level);
    }

    /// <summary>当前关卡信息(供表现层展示;未加载关卡时为 null)。</summary>
    public LevelInfoDto? CurrentLevel => _level == null ? null
        : new LevelInfoDto(_level.Id, _level.Name, _level.Briefing);

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
        var levelSeed = StableHash(level.Id);
        _loop = new SimulationLoop(engine, new SimulationClock(level.TicksPerDay, level.TotalDays),
            seed: levelSeed ^ 0x5EED);   // 全局确定性:参与者Act噪声/预演引导共用此随机流
        _loop.Session.InitShortablePool(level.FloatShares);   // 初始化可融券池(流通盘20%)
        _player = _loop.Session.GetOrCreateAccount(Player, level.PlayerCash);
        if (level.PlayerInitialHolding > 0) _player.Position.Seed(new Quantity(level.PlayerInitialHolding), intrinsic);

        // —— 预演:让市场参与者跑完历史K线,状态真实涌现 ——
        _scenario = new MarketScenario(level.Scenario, new Price(intrinsic.Value * 1.2m), intrinsic,
            seed: levelSeed);   // 剧本K线路径确定性(跨进程可复现)
        var preplay = new MarketPreplay();
        var preplayResult = preplay.Run(_loop.Session, _loop, _scenario, seed: StableHash(level.Id));

        // 机构B(做市+风险控制+操盘三合一)
        _institutionB = new InstitutionB(_loop.Session, new ParticipantId("机构B"), intrinsic,
            cash: 1_000_000_000m, initialHolding: level.MarketMakerHolding,
            baseDepthPerLevel: 80, levels: 20, seed: 88);   // 20档:五档之外藏深层挂单,模拟主力暗挂
        _loop.AddParticipant(_institutionB);

        // 散户画像池(情绪用预演产出的初始值)
        _retail = new RetailProfilePool(_loop.Session, new ParticipantId("散户池"), intrinsic, seed: 42);
        _loop.AddParticipant(_retail);
        // 监管(先于信号系统创建:BlockTradeSignal 持有监管引用)
        _regulator = new Regulator(Player);
        // 消息系统(盘后消息冲击,驱动多维情绪)
        _newsSystem = new NewsSystem(_retail.Sentiment);
        // 水军网络(持续舆论造势)+ 信号误导(大宗/龙虎榜造假)
        _waterArmy = new WaterArmyNetwork();
        _blockSignal = new BlockTradeSignal(_retail.Sentiment, _regulator);

        // 被动资金流(指数ETF/定投/养老金底盘):无视涨跌,每tick小额买入,保证阴跌不死锁
        _passive = new PassiveFlow(_loop.Session, new ParticipantId("被动资金"), level.FloatShares, seed: 77);
        _loop.AddParticipant(_passive);

        _ai = new AIMainForce(_loop.Session, new ParticipantId("AI主力"),
            intrinsic, cash: 100_000_000m, initialHolding: level.AiHolding, initialCost: intrinsic,
            sensitivity: level.AiSensitivity, profile: StrategyProfile.Balanced, seed: 99);
        _loop.AddParticipant(_ai);

        _collector = new MarketDataCollector(_loop, preplayResult.PreviousClose);
        _collector.PreloadHistory(preplayResult.HistoryCandles);
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
            _newsSystem.Tick();   // 消息系统每 tick 应用持续影响 + 过期清理
            // 监管爆表 → 关卡失败
            if (_regulator.GetStatus().IsFailed && !IsLevelOver) EndLevel();
            // 做空爆仓:担保比例<130% → 强制平仓
            if (cur is { } markPrice)
            {
                var marginRatio = _player.MaintenanceRatio(markPrice);
                if (marginRatio < 1.3m && _player.Position.HasShort && !IsLevelOver)
                    _loop.Session.ForceLiquidate(Player, out int _covered);
            }
        };
        // 成交事件 → 表现层推送(Web:SignalR;Unity:本地事件)
        _loop.Session.OnTrade += (p, q, s) =>
        {
            try { TradePushed?.Invoke(new TradeDto(p.Value, q.Value, s.ToString())); } catch { }
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
        _loop.Start();
        IsPreMarket = true;   // 进入盘前准备态,等玩家研究完点"开始操盘"
        // 卡牌引擎初始化(开始操盘时启动第一回合)
        if (CardMode) { _cardEngine = new CardEngine(seed: StableHash(level.Id) + 7); _lastAuction = null; }
        IsInitialized = true;
    }

    /// <summary>跨进程稳定的字符串哈希(FNV-1a)。string.GetHashCode() 每次进程随机化,
    /// 会导致预演/牌序每次运行不同,测试无法复现。</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    /// <summary>玩家结束盘前研究,正式开始交易(退出盘前态)。</summary>
    public void BeginTrading()
    {
        IsPreMarket = false;
        _loop.Resume();
        _cardEngine?.Init();   // 卡牌模式:启动第一回合(进入Play阶段)
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

    /// <summary>结束关卡并返回 DTO(表现层用)。</summary>
    public LevelResultDto EndLevelDto() => ToDto(EndLevel());

    private static LevelResultDto ToDto(LevelResult r) => new(r.IsVictory, r.Stars, r.CoachComment, r.FailureReason,
        r.Objectives.Select(o => new ObjectiveProgressDto(o.Description, o.Achieved, Math.Round(o.Progress, 2), o.Detail)).ToList());

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

    /// <summary>重试关卡(重置到初始)。事件订阅随旧 loop 一起丢弃,LoadLevel 重新挂载。</summary>
    public void Retry() => LoadLevel(_level);

    // ══════════════════════════════════════
    // tick 推进与快照
    // ══════════════════════════════════════

    /// <summary>推进一个 tick 并返回该 tick 后的快照;关卡结束返回 null。
    /// 盘前态/卡牌Play阶段不推进 tick,但仍返回快照(表现层刷新)。</summary>
    public MarketSnapshotDto? Step()
    {
        if (!IsInitialized) return BuildSnapshot();
        if (_loop.IsFinished || IsLevelOver) return null;
        // 盘前态:不推进 tick,但仍推快照(让玩家看K线/盘口)
        if (IsPreMarket) return BuildSnapshot();
        // 卡牌模式:Play阶段冻结(等玩家出牌)
        if (CardMode && _cardEngine is { } ce && ce.Phase == CardPhase.Play)
            return BuildSnapshot();
        _loop.Step();
        // 卡牌模式:每tick推进卡牌引擎(延迟效果+回合计时)
        if (CardMode && _cardEngine is { } ce2 && ce2.Phase == CardPhase.Resolve)
        {
            bool turnEnded = ce2.OnTick(pe =>
            {
                // 执行延迟效果(目前只有分批卖出)
                if (pe.Effect == CardEffect.ScheduledSell)
                {
                    try { _loop.Session.Submit(new OrderRequest(Player, Side.Sell, OrderType.Limit,
                        _loop.Session.Engine.View.BestAsk ?? new Price(10m),
                        new Quantity(pe.EffectValue))); } catch { }
                }
            });
            if (turnEnded) ce2.StartNewTurn();   // 进入下一回合Play阶段
        }
        // 关卡时间结束 → 自动结算
        if (_loop.IsFinished && !IsLevelOver) EndLevel();
        return BuildSnapshot();
    }

    /// <summary>构建完整快照(未初始化时返回空快照,表现层据此显示选关界面)。</summary>
    public MarketSnapshotDto BuildSnapshot()
    {
        if (!IsInitialized)
        {
            return new MarketSnapshotDto(
                0, 0, 0, 0, "Waiting", false, false, false,
                null, null, null, 0m, 0m, 0m, 0m,
                new(), new(), new AccountDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                new(), null, new(), new(),
                0m, "", "", new(), false, 0m, 0, new(), 0m, 0m, 0m, new(), 0, false, 0, 0m, 0m, 0, 0, null);
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
            FloatingProfit: _player.Position.FloatingProfit(mark),
            ShortQty: _player.Position.ShortQty.Value,
            ShortCost: _player.Position.ShortCost.Value,
            MarginFrozen: _player.MarginFrozen,
            MaintenanceRatio: Math.Round(_player.MaintenanceRatio(mark), 2));
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
                    o.TotalQty.Value, o.FilledQty.Value, o.RemainingQty.Value)).ToList(),
            Confidence: Math.Round(_retail.Sentiment.Confidence * 100m, 0),
            HerdMood: Math.Round(_retail.Sentiment.HerdMood * 100m, 0),
            NewsBias: Math.Round(_retail.Sentiment.NewsBias, 2),
            ActiveNews: _newsSystem.ActiveNews.Select(n => new NewsItemDto(
                n.Type.ToString(), n.Headline, n.DurationTicks)).ToList(),
            WaterArmyLevel: _waterArmy.Level,
            WaterArmyActive: _waterArmy.IsActive,
            WaterArmyDays: _waterArmy.ActiveDays,
            WaterArmyDailyCost: _waterArmy.DailyCost,
            InfoHeat: Math.Round(_regulator.InfoHeat, 1),
            ShortablePool: _loop.Session.ShortablePool,
            TotalShortable: _loop.Session.TotalShortable,
            CardState: GetCardState());
    }

    /// <summary>构建卡牌状态 DTO(卡牌模式才有值)。</summary>
    private CardStateDto? GetCardState()
    {
        if (!CardMode || _cardEngine == null) return null;
        return new CardStateDto(
            Turn: _cardEngine.Turn,
            Energy: _cardEngine.Energy,
            Phase: _cardEngine.Phase.ToString(),
            CardsPlayed: _cardEngine.CardsPlayedThisTurn,
            MaxCardsPerTurn: 2,
            Hand: _cardEngine.Hand.Select(c => new CardInstanceDto(
                c.UniqueId, c.Definition.Name, c.Definition.Category.ToString(),
                c.Definition.EnergyCost, c.Definition.CashCost, c.Definition.Description)).ToList(),
            LastCombo: _cardEngine.LastCombo,
            LastAuction: _lastAuction);
    }

    // ══════════════════════════════════════
    // 下单/撤单/时钟
    // ══════════════════════════════════════

    /// <summary>提交下单。返回订单结果;失败返回带 Error 的 DTO。</summary>
    public OrderResultDto Submit(OrderRequestDto dto)
    {
        var side = dto.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
        var type = dto.Type.Equals("market", StringComparison.OrdinalIgnoreCase) ? OrderType.Market : OrderType.Limit;
        var price = type == OrderType.Limit ? new Price(dto.Price ?? 0m) : Price.Zero;
        var req = new OrderRequest(Player, side, type, price, new Quantity(dto.Qty), IsShort: dto.IsShort);
        var result = _loop.Session.Submit(req);
        return new OrderResultDto(
            result.OrderId.Value, result.FinalStatus.ToString(),
            result.AverageFillPrice.Value, result.TotalFilled.Value, result.RemainingQty.Value, null);
    }

    /// <summary>提交下单;异常转为 Failed DTO(不抛)。</summary>
    public OrderResultDto SubmitSafe(OrderRequestDto dto)
    {
        try { return Submit(dto); }
        catch (Exception ex) { return new OrderResultDto(0, "Failed", 0m, 0, 0, ex.Message); }
    }

    public bool Cancel(long orderId) => _loop.Session.Cancel(Player, new OrderId(orderId));

    /// <summary>撤销玩家全部挂单(按方向分别撤,确保买单冻结被正确释放)。</summary>
    public int CancelAll()
    {
        int n = _loop.Session.CancelAllBySide(Player, Side.Buy);
        n += _loop.Session.CancelAllBySide(Player, Side.Sell);
        return n;
    }

    /// <summary>撤销玩家某方向(买/卖)的全部挂单。</summary>
    public int CancelBySide(string side)
    {
        var s = side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
        return _loop.Session.CancelAllBySide(Player, s);
    }

    public void Pause() => _loop.Pause();
    public void Resume() => _loop.Resume();
    public void SkipDay() => _loop.SkipToNextDay();

    /// <summary>开始下一交易日(日终暂停后显式触发;T+1解锁、消息日切在此发生)。</summary>
    public void StartNextDay()
    {
        // 盘后→开盘前:消息系统日切 + 情绪衰减 + 水军网络日切
        _newsSystem.OnNewDay();
        _retail.Sentiment.DailyDecay();
        _waterArmy.OnNewDay(_player, _newsSystem);   // 扣维持费+自动注入Pump
        _cardEngine?.OnNewDay();   // 卡牌引擎日切重置
        _loop.StartNextDay();
    }

    // ══════════════════════════════════════
    // 盘后操作(消息战)
    // ══════════════════════════════════════

    /// <summary>玩家盘后发布消息(影响次日开盘情绪)。扣除成本,注入 NewsSystem。</summary>
    public bool PublishNews(string newsType, out string error)
    {
        error = "";
        if (!IsLevelOver && (_loop.IsDayClosed || IsPreMarket))   // 日终收盘后 或 盘前态可发消息
        {
            var type = newsType.ToLowerInvariant() switch
            {
                "positive" => NewsType.Positive,
                "negative" => NewsType.Negative,
                "rumor" => NewsType.Rumor,
                "pump" => NewsType.Pump,
                _ => NewsType.Positive
            };
            var cost = NewsSystem.GetCost(type);
            if (_player.AvailableCash < cost)
            {
                error = $"资金不足,需要{cost / 10000:F1}万";
                return false;
            }
            var (impact, duration) = NewsSystem.GetDefaults(type);
            string headline = type switch
            {
                NewsType.Positive => "利好消息:机构研报看好,目标价上调",
                NewsType.Negative => "利空消息:业绩不及预期,下调评级",
                NewsType.Rumor => "市场传闻:有大资金进场/退场迹象",
                NewsType.Pump => "水军造势:股吧/论坛出现大量看多帖子",
                _ => "消息"
            };
            if (cost > 0) _player.DebitCash(cost);   // 扣成本
            _newsSystem.Publish(type, headline, impact, duration);
            // 消息监管联动:水军/传闻触发信息操纵关注值
            _regulator.OnNewsPublished(type.ToString());
            return true;
        }
        error = "只能在收盘后或盘前发布消息";
        return false;
    }

    /// <summary>升级水军网络(一次性投入)。</summary>
    public bool UpgradeWaterArmy(out string error)
    {
        error = "";
        if (!_waterArmy.TryUpgrade(out var cost)) { error = "水军网络已满级"; return false; }
        if (_player.AvailableCash < cost) { error = $"资金不足,需要{cost / 10000:F0}万"; return false; }
        _player.DebitCash(cost);
        return true;
    }

    /// <summary>启动/停止水军运营。</summary>
    public bool ToggleWaterArmy(out string error)
    {
        error = "";
        if (_waterArmy.IsActive) { _waterArmy.Stop(); return true; }
        if (!_waterArmy.HasNetwork) { error = "请先升级水军网络"; return false; }
        return _waterArmy.Start();
    }

    /// <summary>发布信号误导(假大宗/龙虎榜)。</summary>
    public bool PublishSignal(string signalType, out string error)
    {
        error = "";
        if (!IsLevelOver && !_loop.IsFinished)
        {
            var type = signalType.ToLowerInvariant() switch
            {
                "fakebigsell" => SignalType.FakeBigSell,
                "fakebigbuy" => SignalType.FakeBigBuy,
                "dragonlist" => SignalType.DragonList,
                _ => SignalType.DragonList
            };
            return _blockSignal.Publish(type, _player, _loop.Clock.CurrentDay, out error);
        }
        error = "关卡已结束";
        return false;
    }

    // ══════════════════════════════════════
    // 卡牌模式
    // ══════════════════════════════════════

    /// <summary>卡牌模式:出牌。handIndex=手牌索引。</summary>
    public bool PlayCard(int handIndex, out string error)
    {
        error = "";
        if (_cardEngine == null) { error = "卡牌模式未启用"; return false; }
        return _cardEngine.PlayCard(handIndex, (def, seq) => ExecuteCardEffect(def), out error);
    }

    /// <summary>卡牌模式:结束回合 → 揭牌集中撮合 → 进入执行阶段。</summary>
    public void EndCardTurn()
    {
        if (_cardEngine == null) return;

        var engine = _loop.Session.Engine;
        var session = _loop.Session;

        // 1. AI 产出意图(与玩家意图一起进入拍卖)
        if (_ai != null)
            _cardEngine.PendingIntents.AddRange(_ai.ProduceIntents(session));
        if (_institutionB != null)
            _cardEngine.PendingIntents.AddRange(_institutionB.ProduceIntents(session));

        // 1.5 连招加成(同回合按序打出触发,揭牌时消费)
        string? combo = _cardEngine.ConsumeTurnCombo();
        if (combo == "诱多陷阱")
        {
            // 市价买量翻倍 + 散户群体热度上升(墙+急拉的假突破引来跟风盘)
            for (int i = 0; i < _cardEngine.PendingIntents.Count; i++)
            {
                var it = _cardEngine.PendingIntents[i];
                if (it.Participant.Equals(Player) && it.Side == Side.Buy && it.Type == OrderType.Market)
                    _cardEngine.PendingIntents[i] = it with { Qty = it.Qty * 2 };
            }
            _retail.Sentiment.ApplyNewsEffect(0, 0.15m, 0);
        }
        else if (combo == "砸盘收割")
        {
            // 市价卖量翻倍 + 散户信心下挫(假大宗铺垫后的真砸盘引发恐慌)
            for (int i = 0; i < _cardEngine.PendingIntents.Count; i++)
            {
                var it = _cardEngine.PendingIntents[i];
                if (it.Participant.Equals(Player) && it.Side == Side.Sell && it.Type == OrderType.Market)
                    _cardEngine.PendingIntents[i] = it with { Qty = it.Qty * 2 };
            }
            _retail.Sentiment.ApplyNewsEffect(-0.15m, 0, 0);
        }
        // 完美出货:分批出货量翻倍已在 PlayCard 注册时生效

        // 2. 清场:清空订单簿,释放上一回合挂单的冻结资金/预扣券池
        session.ClearBookAndRefund();

        // 3. 限价意图校验后挂入(资金/券池/持仓不足的意图作废,不透支)
        foreach (var intent in _cardEngine.PendingIntents.Where(i => i.Type == OrderType.Limit))
        {
            session.TryRestForAuction(new OrderRequest(
                intent.Participant, intent.Side, OrderType.Limit,
                new Price(intent.Price), new Quantity(intent.Qty), IsShort: intent.IsShort));
        }

        // 4. 市价意图按集合竞价市价单语义挂入:买=涨停价/卖=跌停价的限价单,
        //    参与竞价定价后按统一竞价价成交(无价格上限的市价单,而非被动挂对手价)
        foreach (var intent in _cardEngine.PendingIntents.Where(i => i.Type == OrderType.Market))
        {
            decimal price = intent.Side == Side.Buy
                ? engine.Rules.UpperLimit.Value
                : engine.Rules.LowerLimit.Value;
            session.TryRestForAuction(new OrderRequest(
                intent.Participant, intent.Side, OrderType.Limit,
                new Price(price), new Quantity(intent.Qty), IsShort: intent.IsShort));
        }

        // 5. 集合竞价定价 → 集中撮合(成交价统一为竞价价)→ 统一结算
        if (engine.CallAuction() is { } auction)
        {
            engine.SetLastPrice(auction.Price);
            var trades = engine.SweepAtPrice(auction.Price);
            session.SettleAuctionTrades(trades);

            // 记录揭牌结果(随快照推送,表现层展示"谁在什么价买卖多少")
            int turn = _cardEngine.Turn;
            _lastAuction = new AuctionResultDto(turn, auction.Price.Value,
                trades.Sum(t => t.Quantity.Value),
                trades.Select(t => new AuctionTradeDto(
                    DisplayName(t.TakerId), DisplayName(t.MakerId),
                    t.Price.Value, t.Quantity.Value,
                    session.IsShortOrder(t.TakerOrderId), session.IsShortOrder(t.MakerOrderId))).ToList());
        }
        else
        {
            _lastAuction = new AuctionResultDto(_cardEngine.Turn, 0m, 0, new());
        }

        // 6. 清空意图,进入执行阶段
        _cardEngine.PendingIntents.Clear();
        _cardEngine.EndTurn();
    }

    /// <summary>参与者显示名(Player→你,其余用原始名)。</summary>
    private static string DisplayName(ParticipantId id) => id.ToString() == "Player" ? "你" : id.ToString();

    /// <summary>卡牌模式:设置自配牌组。cardNames=牌名列表(可重复)。</summary>
    public bool SetDeck(List<string> cardNames, out string error)
    {
        error = "";
        if (_cardEngine == null) { error = "卡牌模式未启用"; return false; }
        if (cardNames.Count == 0) { error = "牌组不能为空"; return false; }
        if (cardNames.Count > 30) { error = "牌组最多30张"; return false; }

        var deck = new List<CardDefinition>();
        foreach (var name in cardNames)
        {
            var def = CardDefinition.Library.FirstOrDefault(c => c.Name == name);
            if (def == null) { error = $"未知卡牌: {name}"; return false; }
            deck.Add(def);
        }
        _cardEngine.SetDeck(deck);
        return true;
    }

    /// <summary>执行卡牌效果(由 CardEngine.PlayCard 调用)。
    /// 操盘牌→收集 OrderIntent(揭牌时撮合);舆论/信号牌→立即生效。</summary>
    private bool ExecuteCardEffect(CardDefinition def)
    {
        var session = _loop.Session;
        var view = session.Engine.View;
        try
        {
            switch (def.Effect)
            {
                // —— 操盘牌:收集为 OrderIntent(不立即撮合) ——
                case CardEffect.LimitBuy:
                    decimal bid = view.BestBid?.Value ?? 10m;
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Buy, OrderType.Limit, bid - 0.01m, def.EffectValue));
                    break;
                case CardEffect.MarketBuy:
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Buy, OrderType.Market, 0, def.EffectValue));
                    break;
                case CardEffect.MarketSell:
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Sell, OrderType.Market, 0, def.EffectValue));
                    break;
                case CardEffect.LimitWall:
                    decimal p = view.LastPrice?.Value ?? 10m;
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Buy, OrderType.Limit, p - 0.03m, def.EffectValue));
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Sell, OrderType.Limit, p + 0.03m, def.EffectValue));
                    break;
                case CardEffect.ScheduledSell:
                    // 延迟效果,由 OnTick 中 CardEngine.PendingEffects 执行
                    break;
                case CardEffect.ShortSell:
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Sell, OrderType.Market, 0, def.EffectValue, IsShort: true));
                    break;
                case CardEffect.ShortCover:
                    int coverQty = _player.Position.ShortQty.Value;
                    if (coverQty <= 0) return false;
                    _cardEngine!.PendingIntents.Add(new OrderIntent(
                        Player, Side.Buy, OrderType.Market, 0, coverQty, IsShort: true));
                    break;
                case CardEffect.NewsPositive:
                    _retail.Sentiment.NewsShock(true, 0.15m);
                    break;
                case CardEffect.NewsNegative:
                    _retail.Sentiment.NewsShock(false, 0.15m);
                    break;
                case CardEffect.NewsRumor:
                    PublishNews("rumor", out _);
                    break;
                case CardEffect.NewsPump:
                    PublishNews("pump", out _);
                    break;
                case CardEffect.SignalFakeSell:
                    PublishSignal("fakebigsell", out _);
                    break;
                case CardEffect.SignalFakeBuy:
                    PublishSignal("fakebigbuy", out _);
                    break;
                case CardEffect.SignalDragonList:
                    PublishSignal("dragonlist", out _);
                    break;
                case CardEffect.CancelAll:
                    session.CancelAllBySide(Player, Side.Buy);
                    session.CancelAllBySide(Player, Side.Sell);
                    break;
                case CardEffect.LayLow:
                    break;   // 蛰伏冷却:效果由 CardEngine(IsLayingLow) 处理
            }
            return true;
        }
        catch { return false; }
    }

    // ══════════════════════════════════════
    // 复盘与查询
    // ══════════════════════════════════════

    /// <summary>获取筹码分布历史(每日收盘的价位分布)。day=null 返回全部,否则返回指定日。</summary>
    public List<DayChipDto> GetChipHistory(int? day = null)
    {
        var history = _chipCollector.History;
        if (day.HasValue)
        {
            int idx = day.Value - 1;
            if (idx < 0 || idx >= history.Count) return new();
            return new() { ToChipDto(history[idx], idx) };
        }
        return history.Select((h, i) => ToChipDto(h, i)).ToList();
    }

    private DayChipDto ToChipDto(DayChipDistribution snap, int idx)
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
        var chips = _chipCollector.History.Select((h, i) => ToChipDto(h, i)).ToList();
        var candles = _collector.DailyCandles
            .Select(c => new DailyCandleDto(c.Day, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        return new ReplayDataDto(
            _loop.Clock.TotalDays * _loop.Clock.TicksPerDay, _loop.Clock.TotalDays,
            snapshots, trades, events, chips, candles);
    }

    /// <summary>获取 AI 主力最近的内心独白(调试/复盘用;盘中 AI 持仓仍不可见)。</summary>
    public List<AIDto> GetAIThoughts(int count = 20)
        => _ai.Thoughts
            .TakeLast(count)
            .Select(t => new AIDto(t.Day, t.TickOfDay, t.State.ToString(),
                                   t.DetectedIntent.ToString(), t.Confidence, t.Reason))
            .Reverse().ToList();
}
