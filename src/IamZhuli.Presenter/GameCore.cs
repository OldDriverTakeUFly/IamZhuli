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
    /// <summary>信息操纵封口剩余 tick(立案调查期间禁止发布消息/信号)。</summary>
    private int _infoGagTicks;
    /// <summary>本日收盘竞价价(收盘静默的冻结价,日切时即收盘价)。</summary>
    private decimal? _closeAuctionPrice;

    /// <summary>是否启用卡牌模式(完全替换实时操作)。</summary>
    public bool CardMode { get; set; } = true;

    /// <summary>操盘意图(战略层):跨段持续,每段免费产生基础盘;卡牌是战术强化。</summary>
    public TurnIntent Intent { get; private set; } = TurnIntent.None;

    /// <summary>设置操盘意图(前端chips;None=清除)。仅出牌阶段可改。</summary>
    public bool SetIntent(string name, out string error)
    {
        error = "";
        if (_cardEngine is not { Phase: CardPhase.Play }) { error = "仅出牌阶段可调整意图"; return false; }
        Intent = name?.Trim().ToLowerInvariant() switch
        {
            "accumulate" or "吸筹" => TurnIntent.Accumulate,
            "push" or "拉升" => TurnIntent.Push,
            "wash" or "洗盘" => TurnIntent.Wash,
            "distribute" or "出货" => TurnIntent.Distribute,
            "defend" or "防御" => TurnIntent.Defend,
            _ => TurnIntent.None,
        };
        // 牌随势走: 抽牌偏置 + 手牌方向保底(立即生效,不等下段)
        _cardEngine!.DrawBias = Intent switch
        {
            TurnIntent.Accumulate or TurnIntent.Push => Side.Buy,
            TurnIntent.Distribute or TurnIntent.Wash => Side.Sell,
            _ => null,
        };
        _cardEngine.EnsureDirectionFloor();
        return true;
    }

    /// <summary>意图基础盘:无论手牌如何,方向永远可执行(庄家剧本不由抽卡决定)。</summary>
    private void AddIntentBaseline()
    {
        if (Intent == TurnIntent.None) return;
        var view = _loop.Session.Engine.View;
        decimal last = view.LastPrice?.Value ?? 10m;
        decimal bid = view.BestBid?.Value ?? last;
        decimal ask = view.BestAsk?.Value ?? last;
        var rules = _loop.Session.Engine.Rules;
        var intents = _cardEngine!.PendingIntents;
        void Add(Side side, OrderType type, decimal price, int qty) =>
            intents.Add(new OrderIntent(Player, side, type, price, qty));
        switch (Intent)
        {
            case TurnIntent.Accumulate:
                Add(Side.Buy, OrderType.Limit, Math.Max(rules.LowerLimit.Value, bid - 0.01m), 800);
                break;
            case TurnIntent.Push:
                Add(Side.Buy, OrderType.Market, 0, 800);
                break;
            case TurnIntent.Wash:   // 上压下托,双向夹出浮筹
                Add(Side.Sell, OrderType.Limit, Math.Min(rules.UpperLimit.Value, ask + 0.02m), 600);
                Add(Side.Buy, OrderType.Limit, Math.Max(rules.LowerLimit.Value, bid - 0.04m), 600);
                break;
            case TurnIntent.Distribute:
                Add(Side.Sell, OrderType.Limit, ask, 800);   // 挂卖一队尾,被动出货
                break;
            case TurnIntent.Defend:
                Add(Side.Buy, OrderType.Limit, Math.Max(rules.LowerLimit.Value, bid - 0.03m), 600);
                Add(Side.Sell, OrderType.Limit, Math.Min(rules.UpperLimit.Value, ask + 0.03m), 600);
                break;
        }
    }

    /// <summary>种子覆盖(测试/复现用):null=按关卡ID稳定哈希。</summary>
    private readonly int? _seedOverride;

    /// <summary>seed: 覆盖关卡种子,让同一关卡产生不同市场路径(性质测试需要);默认按关卡ID确定。</summary>
    public GameCore(int? seed = null) => _seedOverride = seed;

    public bool IsInitialized { get; private set; }
    public LevelResult? LastResult { get; private set; }
    public bool IsLevelOver { get; private set; }
    /// <summary>盘前准备态:LoadLevel 后进入,玩家研究K线/筹码后点"开始操盘"才正式开盘。</summary>
    public bool IsPreMarket { get; private set; } = true;
    /// <summary>上回合揭牌结果(只读,供表现层直接读取)。</summary>
    public AuctionResultDto? LastAuction => _lastAuction;
    /// <summary>交易会话(下单/撤单/查询的直接入口)。</summary>
    public TradingSession Session => _loop.Session;
    /// <summary>AI 主力实例(测试矩阵强制状态用)。</summary>
    public AIMainForce? AI => _ai;
    /// <summary>机构B实例(测试矩阵强制档位用)。</summary>
    public InstitutionB? Institution => _institutionB;
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
        _infoGagTicks = 0;
        Intent = TurnIntent.None;   // 新关卡新剧本

        var intrinsic = new Price(level.IntrinsicValue);
        var rules = new MarketRules
        {
            PreviousClose = intrinsic,
            PriceLimitRatio = 0.10m,
            TickSize = new Price(0.01m),
            FloatShares = new Quantity(level.FloatShares)
        };
        var engine = new MatchingEngine(rules);
        var levelSeed = _seedOverride ?? StableHash(level.Id);
        int ticksPerDay = CardMode ? CardEngine.TotalTicksPerDay : level.TicksPerDay;   // 卡牌模式:时钟=段时长总和
        _loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, level.TotalDays),
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

        // 信息操纵惩罚阶梯:警示(60)=辟谣反弹+水军捣毁+罚款;立案(80)=强辟谣+重罚+封口1天
        _regulator.InfoPenaltyEscalated += lvl =>
        {
            bool investigation = lvl == PenaltyLevel.Investigation;
            decimal shock = investigation ? 0.30m : 0.15m;
            _retail.Sentiment.NewsShock(positive: false, shock);   // 辟谣:证伪反转(信心/消息偏差/贪婪齐挫)
            if (_waterArmy.IsActive)
            {
                _waterArmy.Stop();   // 水军网络被监管曝光捣毁
                _regulator.RecordExternalEvent($"[辟谣] 水军网络被曝光捣毁(等级{_waterArmy.Level}清零运营)");
            }
            decimal fine = investigation ? 5_000_000m : 500_000m;
            _player.DebitCash(fine);
            if (investigation) _infoGagTicks = level.TicksPerDay;   // 封口一整天(5回合)
            _regulator.RecordExternalEvent(investigation
                ? $"[立案调查] 信息操纵罚款{fine / 10000:F0}万,辟谣澄清,禁止发布消息1天"
                : $"[警示] 信息操纵罚款{fine / 10000:F0}万,监管公开辟谣");
        };

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
            if (_infoGagTicks > 0) _infoGagTicks--;   // 封口期倒计时
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
        // 日切态(收盘后/盘后操作窗口):不推进,推快照
        if (_loop.IsDayClosed) return BuildSnapshot();
        // 盘前态:不推进 tick,但仍推快照(让玩家看K线/盘口)
        if (IsPreMarket) return BuildSnapshot();
        // 卡牌模式:Play阶段冻结(等玩家出牌)
        if (CardMode && _cardEngine is { } ce && ce.Phase == CardPhase.Play)
            return BuildSnapshot();
        // 收盘静默:收盘竞价的Resolve不再交易(价格冻结在竞价价),tick走完直接日切
        // 申报静默:盘前/尾盘申报段的Resolve不连续交易(集合竞价前无连续成交)
        bool closingSilence = CardMode && _cardEngine is { Turn: 11 } && _closeAuctionPrice.HasValue;
        bool declareSilence = CardMode && _cardEngine is { Phase: CardPhase.Resolve, Turn: 0 or 10 };
        if (closingSilence || declareSilence)
            _loop.StepClockOnly();   // 静默段:时间照流(监管衰减/日切),但无连续交易
        else
            _loop.Step();
        // 卡牌模式:每tick推进卡牌引擎(延迟效果+回合计时)
        if (CardMode && _cardEngine is { } ce2 && ce2.Phase == CardPhase.Resolve)
        {
            bool turnEnded = ce2.OnTick(pe =>
            {
                // 执行延迟效果(目前只有分批卖出;静默段[收盘/申报]无连续交易不下单)
                if (pe.Effect == CardEffect.ScheduledSell && !closingSilence && !declareSilence)
                {
                    try { _loop.Session.Submit(new OrderRequest(Player, Side.Sell, OrderType.Limit,
                        _loop.Session.Engine.View.BestAsk ?? new Price(10m),
                        new Quantity(pe.EffectValue))); } catch { }
                }
            });
            if (turnEnded)
            {
                _closeAuctionPrice = null;   // 静默结束
                if (closingSilence) _loop.FinishDay();   // 收盘竞价完成:直接日切(收盘价=竞价价)
                else ce2.StartNewTurn();   // 进入下一回合Play阶段
            }
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

    /// <summary>段→交易日阶段(12段:0盘前申报/1盘前竞价/2-9盘中/10尾盘申报/11收盘竞价)。</summary>
    private static string TurnPhaseName(int turn) => turn switch
    {
        0 => "盘前申报",
        1 => "盘前竞价",
        10 => "尾盘申报",
        11 => "收盘竞价",
        _ => "盘中",
    };

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
                c.Definition.EnergyCost, c.Definition.CashCost, c.Definition.Description,
                c.Definition.Effect.ToString())).ToList(),
            LastCombo: _cardEngine.LastCombo,
            LastAuction: _lastAuction,
            ActiveStance: _cardEngine.ActiveStance == CardEffect.None ? null : _cardEngine.ActiveStance.ToString(),
            PhaseName: TurnPhaseName(_cardEngine.Turn),
            MaxEnergy: 12,
            Intent: Intent == TurnIntent.None ? null : Intent.ToString());
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
        _closeAuctionPrice = null;   // 新一天
        _cardEngine?.OnNewDay();   // 卡牌引擎日切重置(回合归0=次日盘前竞价)
        _loop.StartNextDay(runOpeningAuction: !CardMode);   // 卡牌模式:开盘价由回合0卡牌拍卖确立
    }

    // ══════════════════════════════════════
    // 盘后操作(消息战)
    // ══════════════════════════════════════

    /// <summary>玩家盘后发布消息(影响次日开盘情绪)。扣除成本,注入 NewsSystem。</summary>
    public bool PublishNews(string newsType, out string error)
        => PublishNewsInternal(newsType, out error, checkTiming: true);

    /// <summary>卡牌舆论牌发布消息:不受"收盘后/盘前"时机限制(出牌即生效),仍扣成本+触发监管。</summary>
    private bool PublishNewsFromCard(string newsType, out string error)
        => PublishNewsInternal(newsType, out error, checkTiming: false);

    private bool PublishNewsInternal(string newsType, out string error, bool checkTiming)
    {
        error = "";
        if (_infoGagTicks > 0) { error = "监管问询期间,禁止发布消息"; return false; }
        // 时机闸门:盘后面板操作要求收盘后/盘前;卡牌舆论牌出牌即生效,跳过
        if (checkTiming && !(IsPreMarket || _loop.IsDayClosed || IsLevelOver))
        {
            error = "只能在收盘后或盘前发布消息";
            return false;
        }
        if (IsLevelOver && _loop.IsFinished) { error = "关卡已结束"; return false; }
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
        if (_infoGagTicks > 0) { error = "监管问询期间,禁止发布信号"; return false; }
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
        bool isDeclare = _cardEngine.Turn is 0 or 10;    // 申报段(盘前1/尾盘1):只挂单不撮合
        bool isOpenFire = _cardEngine.Turn == 1;         // 盘前锁单竞价:出开盘价
        bool isCloseFire = _cardEngine.Turn == TurnsPerDayLast;   // 收盘竞价:锁收盘价

        // 1. AI 产出意图——申报段不出(AI在锁单段看到玩家申报挂单后应对)
        if (!isDeclare)
        {
            if (_ai != null)
                _cardEngine.PendingIntents.AddRange(_ai.ProduceIntents(session));
            if (_institutionB != null)
                _cardEngine.PendingIntents.AddRange(_institutionB.ProduceIntents(session));
        }

        // 1.05 操盘意图基础盘(战略层:先于架势注入,架势/连招可放大它)
        AddIntentBaseline();

        // 1.1 盘前锁单竞价(回合1):散户隔夜情绪化意向单——消息偏差/贪婪驱动的小额市价单,
        //     打通"盘后发消息→次日高开/低开"通道(散户是价格接受者,但集合竞价会排队表态)
        if (isOpenFire)
        {
            var sent = _retail.Sentiment;
            // 隔夜情绪=消息偏差(已衰减)+活跃新闻净方向(盘后发的消息,次日盘前散户排队表态)+贪婪偏移
            decimal newsNet = _newsSystem.ActiveNews.Sum(n =>
                n.Type is NewsType.Positive or NewsType.Pump ? 1 : -1);
            decimal bias = sent.NewsBias + newsNet * 0.12m + (sent.Value - 0.5m) * 0.5m;
            int retailQty = Math.Min(3000, (int)(Math.Abs(bias) * _retail.ActiveCount * 20));
            if (retailQty >= 100)
            {
                var side = bias > 0 ? Side.Buy : Side.Sell;
                _cardEngine.PendingIntents.Add(new OrderIntent(_retail.Id, side, OrderType.Market, 0, retailQty));
            }
        }

        // 1.5 连招加成(同段按序打出,撮合段消费;申报段无撮合不生效——连招要在撮合段打出)
        string? combo = isDeclare ? null : _cardEngine.ConsumeTurnCombo();
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

        // 1.7 架势变体:修改玩家操盘意图 + 情绪/监管副作用(申报段不消费,留待下段重设)
        var decoys = isDeclare ? new List<DecoyOrder>() : ApplyStance(engine);

        // 1.8 反手抢跑(AI应对的一环,申报段不出):AI已识破假墙(≥2次)且玩家仍延续该方向 → AI先下同向市价单,
        //     买入场景推高竞价价(让被识破的操纵者买贵),卖出场景压低(让他卖便宜)
        if (!isDeclare && _ai is { FakeWallSeen: >= 2, LastFakeWallSide: { } fw })
        {
            bool pb = _cardEngine.PendingIntents.Any(x => x.Participant.Equals(Player) && x.Side == Side.Buy);
            bool ps = _cardEngine.PendingIntents.Any(x => x.Participant.Equals(Player) && x.Side == Side.Sell && !x.IsShort);
            if ((fw == Side.Buy && pb) || (fw == Side.Sell && ps))
                _cardEngine.PendingIntents.Insert(0, new OrderIntent(_ai.Id, fw, OrderType.Market, 0, 800));   // 插队最前:竞价时间优先真正"抢先"
        }

        // 2. 清场:盘中撮合段清掉上段残余;申报段与开盘/收盘撮合段保留簿上申报单
        //    (盘前/收盘的申报挂单要延续到竞价;申报段本身无旧单可清也无需清)
        if (!isDeclare && !isOpenFire && !isCloseFire)
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

        // 4.5 盘口伪装:假墙挂入(绕过资产校验的纯盘面幻影,参与竞价定价)。
        //     AI 识破升级:见过1次→墙效减半(市场对重复出现的墙打折扣),2次起→墙失效
        var decoyOrders = new List<IamZhuli.Engine.Order>();
        foreach (var d in decoys)
        {
            int wallQty = _ai?.FakeWallSeen switch
            {
                >= 2 => 0,
                >= 1 => d.Qty / 2,
                _ => d.Qty,
            };
            if (wallQty <= 0) continue;   // 已被识破:墙上不了盘面
            var o = engine.CreateOrder(Player, d.Side, OrderType.Limit, new Price(d.Price), new Quantity(wallQty));
            engine.RestOrder(o);
            decoyOrders.Add(o);
        }

        // 5. 集合竞价定价(含假墙锚定) → 撤墙(虚假挂单热度) → 集中撮合(假墙永不成交)
        decimal preAuctionPrice = engine.View.LastPrice?.Value ?? 0m;
        if (engine.CallAuction() is { } auction)
        {
            foreach (var d in decoyOrders)
            {
                engine.Cancel(d.Id, out _);
                _regulator.ApplyTradeHeat(4m, $"盘口伪装撤单: {d.RemainingQty}@{d.Price}");
                _ai?.NoteFakeWall(d.Side, d.Price.Value, d.RemainingQty.Value);
            }
            engine.SetLastPrice(auction.Price);
            var trades = engine.SweepAtPrice(auction.Price);
            session.SettleAuctionTrades(trades);

            // 收盘竞价(末段):记录竞价价,进入收盘静默(Resolve不交易,日切时即收盘价)
            if (isCloseFire)
                _closeAuctionPrice = auction.Price.Value;

            // 收盘竞价特护:竞价价大幅偏离前价=尾盘操纵特征(画日K锁收盘价),
            // 监管重点监控,热度+6——高收益(收盘价锚定情绪/目标判定)与高风险并存
            if (isCloseFire && preAuctionPrice > 0)
            {
                decimal move = Math.Abs(auction.Price.Value - preAuctionPrice) / preAuctionPrice;
                if (move >= 0.02m)
                    _regulator.ApplyTradeHeat(6m, $"尾盘异常波动: 收盘竞价{(auction.Price.Value > preAuctionPrice ? "拉升" : "砸落")}{move:P1}");
            }

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
            foreach (var d in decoyOrders)
            {
                engine.Cancel(d.Id, out _);
                _regulator.ApplyTradeHeat(4m, $"盘口伪装撤单: {d.RemainingQty}@{d.Price}");
                _ai?.NoteFakeWall(d.Side, d.Price.Value, d.RemainingQty.Value);
            }
            _lastAuction = new AuctionResultDto(_cardEngine.Turn, 0m, 0, new());
        }

        // 6. 申报段:意图已挂簿,无撮合直接进入执行阶段
        if (isDeclare)
        {
            _cardEngine.PendingIntents.Clear();
            _lastAuction = new AuctionResultDto(_cardEngine.Turn, 0m, 0, new());   // 申报段不揭牌
            _cardEngine.EndTurn();
            return;
        }

        // 7. 清空意图,进入执行阶段
        _cardEngine.PendingIntents.Clear();
        _cardEngine.EndTurn();
    }

    private static int TurnsPerDayLast => CardEngine.TurnsPerDay - 1;

    /// <summary>假墙规格(盘口伪装:参与竞价定价,撮合前撤除,永不成交)。</summary>
    private readonly record struct DecoyOrder(Side Side, decimal Price, int Qty);

    /// <summary>应用架势变体到玩家意图(激进×2/隐蔽×6折+深挂/防御市价转限价/欺诈情绪反转+盘口伪装)。
    /// 返回欺诈架势要挂的假墙(由 EndCardTurn 在清场后挂入)。</summary>
    private List<DecoyOrder> ApplyStance(IamZhuli.Engine.MatchingEngine engine)
    {
        var decoys = new List<DecoyOrder>();
        var stance = _cardEngine!.ActiveStance;
        if (stance == CardEffect.None) return decoys;
        _cardEngine.ActiveStance = CardEffect.None;   // 消费

        var view = engine.View;
        var intents = _cardEngine.PendingIntents;
        switch (stance)
        {
            case CardEffect.StanceAggressive:
                for (int i = 0; i < intents.Count; i++)
                {
                    var it = intents[i];
                    if (it.Participant.Equals(Player) && it.Side is Side.Buy or Side.Sell && !it.IsShort)
                        intents[i] = it with { Qty = it.Qty * 2 };
                }
                _regulator.ApplyTradeHeat(5m, "激进架势:放大操作");
                break;

            case CardEffect.StanceStealthy:
                for (int i = 0; i < intents.Count; i++)
                {
                    var it = intents[i];
                    if (!it.Participant.Equals(Player) || it.IsShort) continue;
                    var qty = Math.Max(100, it.Qty * 6 / 10);
                    decimal price = it.Price;
                    if (it.Side == Side.Buy && it.Type == OrderType.Limit)
                        price -= 0.03m;   // 挂得更深,不暴露在买一
                    intents[i] = it with { Qty = qty, Price = price };
                }
                _regulator.ApplyTradeHeat(-4m, "隐蔽架势:降温");
                break;

            case CardEffect.StanceDefensive:
                for (int i = 0; i < intents.Count; i++)
                {
                    var it = intents[i];
                    if (!it.Participant.Equals(Player) || it.Type != OrderType.Market) continue;
                    // 市价→对手价限价:只吃一档,不追价
                    decimal price = it.Side == Side.Buy
                        ? (view.BestAsk?.Value ?? engine.Rules.UpperLimit.Value)
                        : (view.BestBid?.Value ?? engine.Rules.LowerLimit.Value);
                    intents[i] = it with { Type = OrderType.Limit, Price = price };
                }
                break;

            case CardEffect.StanceDeceptive:
                // 反向情绪信号:买入时装跌(散户抛售→更便宜的货),卖出时装涨(散户接盘→更好的价)
                bool hasBuy = intents.Any(x => x.Participant.Equals(Player) && x.Side == Side.Buy);
                bool hasSell = intents.Any(x => x.Participant.Equals(Player) && x.Side == Side.Sell && !x.IsShort);
                if (hasBuy) _retail.Sentiment.ApplyNewsEffect(-0.08m, 0, -0.05m);
                if (hasSell) _retail.Sentiment.ApplyNewsEffect(0.08m, 0, 0.05m);
                _regulator.ApplyInfoHeat(4m, "欺诈架势:反向信号");
                // 盘口伪装:同侧假墙挂在更远处,参与竞价定价把成交价锚定到有利方向
                // (买入→下方假买墙拉低竞价价;卖出→上方假卖墙抬高竞价价),撮合前撤墙
                decimal anchor = view.LastPrice?.Value ?? 10m;
                if (hasBuy) decoys.Add(new DecoyOrder(Side.Buy, Math.Round(anchor - 0.05m, 2), 3000));
                if (hasSell) decoys.Add(new DecoyOrder(Side.Sell, Math.Round(anchor + 0.05m, 2), 3000));
                break;
        }
        return decoys;
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
                    PublishNewsFromCard("rumor", out _);
                    break;
                case CardEffect.NewsPump:
                    PublishNewsFromCard("pump", out _);
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
                    if (_cardEngine.Turn is 1 or 11) return false;   // 锁单段不可撤(9:20-9:25/收盘竞价)
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
    // 存档(日切检查点):导出纯数据,宿主决定存储;导入=确定性重建+状态回填
    // ══════════════════════════════════════

    /// <summary>能否存档(仅日切边界:盘前/日切暂停/当日段0起点——此时订单簿为空,状态可完整捕获)。</summary>
    public bool CanSave => IsPreMarket || _loop.IsDayClosed ||
        (_loop.Clock.CurrentTickOfDay == 0 && _cardEngine is { Turn: 0, Phase: CardPhase.Play });

    /// <summary>导出存档(须在 CanSave 状态调用,否则抛异常——盘中订单簿/意图无法完整捕获)。</summary>
    public SaveData ExportSave()
    {
        if (!CanSave) throw new InvalidOperationException("仅日切边界可存档(盘前/收盘后/段0起点)");
        var (hand, draw, discard) = _cardEngine!.PilesSnapshot();
        return new SaveData
        {
            LevelId = _level.Id,
            Day = IsPreMarket ? _loop.Clock.CurrentDay : _loop.Clock.CurrentDay + (_loop.IsDayClosed ? 1 : 0),
            LastPrice = _loop.Session.Engine.LastPrice?.Value ?? _collector.PreviousClose,
            PreviousClose = _collector.PreviousClose,
            Player = Acct(_player), Ai = Acct(_ai.Account), Institution = Acct(_institutionB.Account),
            AiState = _ai.CurrentState.ToString(),
            InstRiskLevel = _institutionB.CurrentRiskLevel.ToString(),
            Cards = new CardEngineSave(hand.Select(c => c.Name).ToList(),
                draw.Select(c => c.Name).ToList(), discard.Select(c => c.Name).ToList(),
                _cardEngine.Energy, _cardEngine.Turn, _cardEngine.HasCustomDeck),
            Intent = Intent == TurnIntent.None ? null : Intent.ToString(),
            Heat = _regulator.Heat, InfoHeat = _regulator.InfoHeat,
            ShortablePool = _loop.Session.ShortablePool, TotalShortable = _loop.Session.TotalShortable,
            Sentiment = new SentimentSave(_retail.Sentiment.Value, _retail.Sentiment.TargetSnapshot,
                _retail.Sentiment.Confidence, _retail.Sentiment.HerdMood, _retail.Sentiment.NewsBias),
            WaterArmy = new WaterArmySave(_waterArmy.Level, _waterArmy.IsActive, _waterArmy.ActiveDays),
            News = _newsSystem.ActiveNews.Select(n => new SavedNewsDto(n.Type.ToString(), n.Headline, n.Impact, n.DurationTicks)).ToList(),
            Candles = _collector.DailyCandles.Select(c => new CandleDto(c.Day, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
        };
    }

    private static AccountSave Acct(Account a) => new(a.Cash, a.Position.Available.Value,
        a.Position.T1Locked.Value, a.Position.AverageCost.Value, a.Position.ShortQty.Value,
        a.Position.ShortCost.Value, a.MarginFrozen, a.TotalBoughtQty, a.TotalSoldQty);

    /// <summary>从存档恢复:确定性重建(同种子预演)→状态回填→跳到存档日开盘。返回是否成功。</summary>
    public bool LoadFromSave(SaveData s)
    {
        try
        {
            // 1. 确定性重建(预演/参与者与原局一致)
            LoadLevelById(s.LevelId);
            // 2. 时钟跳到存档日
            _loop.Clock.ForceDayStart(s.Day);
            // 3. 价格基准
            _loop.Session.Engine.SetLastPrice(new Price(s.LastPrice));
            _loop.Session.Engine.Rules.PreviousClose = new Price(s.PreviousClose);
            _collector.RestoreHistory(s.Candles.Select(c => new DailyCandle(
                c.Day, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(), s.PreviousClose, s.LastPrice);
            // 4. 账户(玩家/AI/机构)
            Restore(_player, s.Player); Restore(_ai.Account, s.Ai); Restore(_institutionB.Account, s.Institution);
            // 5. AI状态/机构档位
            _ai.ForceStateForTest(Enum.Parse<AIState>(s.AiState));
            _institutionB.ForceRiskLevelForTest(Enum.Parse<RiskLevel>(s.InstRiskLevel));
            // 6. 卡牌引擎(牌组以牌名还原)
            var lib = Simulation.Cards.CardDefinition.Library;
            _cardEngine!.RestoreState(
                s.Cards.Hand.Select(n => lib.First(c => c.Name == n)).ToList(),
                new Queue<Simulation.Cards.CardDefinition>(s.Cards.Draw.Select(n => lib.First(c => c.Name == n))),
                s.Cards.Discard.Select(n => lib.First(c => c.Name == n)).ToList(),
                s.Cards.Energy, s.Cards.Turn, Simulation.Cards.CardPhase.Play);
            // 7. 监管/券池/情绪/水军/消息/意图
            _regulator.RestoreHeats(s.Heat, s.InfoHeat);
            _loop.Session.RestoreShortable(s.ShortablePool, s.TotalShortable);
            _retail.Sentiment.Restore(s.Sentiment.Value, s.Sentiment.Target, s.Sentiment.Confidence, s.Sentiment.Herd, s.Sentiment.Bias);
            _waterArmy.Restore(s.WaterArmy.Level, s.WaterArmy.Active, s.WaterArmy.ActiveDays);
            _newsSystem.RestoreActive(s.News.Select(n => new NewsItem(
                0, Enum.Parse<NewsType>(n.Type), n.Headline, n.Impact, n.RemainingTicks)));
            if (s.Intent is { } it) SetIntent(it, out _);
            // 8. 开盘态(与 BeginTrading 等效但跳过卡牌Init)
            IsPreMarket = false;
            _loop.Resume();
            return true;
        }
        catch { return false; }
    }

    private static void Restore(Account a, AccountSave s)
        => a.Restore(s.Cash, s.Available, s.T1Locked, s.AvgCost, s.ShortQty, s.ShortCost, s.Margin, s.Bought, s.Sold);

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

    /// <summary>获取 AI 主力最近的内心独白(调试/复盘用;盘中 AI 持仓仍不可见)。
    /// 未加载关卡时返回空列表(Agent冷启动盲玩踩到的NRE)。</summary>
    public List<AIDto> GetAIThoughts(int count = 20)
        => _ai?.Thoughts
            .TakeLast(count)
            .Select(t => new AIDto(t.Day, t.TickOfDay, t.State.ToString(),
                                   t.DetectedIntent.ToString(), t.Confidence, t.Reason))
            .Reverse().ToList();
}
