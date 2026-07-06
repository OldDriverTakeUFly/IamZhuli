using System.Collections;
using System.Collections.Generic;
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
using UnityEngine;

namespace IamZhuli.Unity
{
    /// <summary>
    /// Unity 主控制器:替代 Web 端的 GameSingleton。
    /// 驱动 SimulationLoop,管理参与者,提供下单/撤单/暂停接口供 UI 调用。
    /// 每个 tick 通过协程推进,UI 通过 OnTickEvent 回调刷新。
    /// </summary>
    public class GameHost : MonoBehaviour
    {
        [Header("时间设置")]
        public float tickInterval = 0.4f;   // 400ms/tick → 150tick = 1分钟/天

        // —— 游戏状态 ——
        private SimulationLoop _loop;
        private TradingSession _session;
        private Account _player;
        private AIMainForce _ai;
        private InstitutionB _institutionB;
        private RetailProfilePool _retail;
        private PassiveFlow _passive;
        private Regulator _regulator;
        private LevelJudge _judge;
        private LevelDefinition _level;
        private MarketDataCollector _collector;
        private EquityCurveCollector _equityCollector;
        private ChipSnapshotCollector _chipCollector;
        private NewsSystem _newsSystem;
        private WaterArmyNetwork _waterArmy;
        private BlockTradeSignal _blockSignal;

        private static readonly ParticipantId Player = new("Player");
        private decimal _initialCash;
        private decimal _maxHeatReached;
        private decimal? _prevPriceForVolatility;

        public bool IsPreMarket { get; private set; } = true;
        public bool IsLevelOver { get; private set; }

        /// <summary>每 tick 触发,UI 订阅此事件刷新。</summary>
        public event System.Action OnTickEvent;

        /// <summary>暴露 loop 供 UI 面板读取盘口数据。</summary>
        public SimulationLoop GetLoop() => _loop;
        /// <summary>暴露 player 账户供 UI 面板读取持仓。</summary>
        public Account GetPlayer() => _player;

        void Start()
        {
            LoadLevel(LevelDefinition.PumpAndDump());
            StartCoroutine(TickLoop());
        }

        IEnumerator TickLoop()
        {
            while (!IsLevelOver && !_loop.IsFinished)
            {
                if (!IsPreMarket && !_loop.IsPaused && !_loop.IsDayClosed)
                {
                    StepOnce();
                }
                yield return new WaitForSeconds(tickInterval);
            }
        }

        private void StepOnce()
        {
            _loop.Step();
            OnTickEvent?.Invoke();
        }

        // —— 关卡加载(移植自 GameSingleton.LoadLevel) ——
        private void LoadLevel(LevelDefinition level)
        {
            _level = level;
            _initialCash = level.PlayerCash;
            _maxHeatReached = 0;
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
            _session = _loop.Session;
            _player = _session.GetOrCreateAccount(Player, level.PlayerCash);
            if (level.PlayerInitialHolding > 0)
                _player.Position.Seed(new Quantity(level.PlayerInitialHolding), intrinsic);

            // 预演
            var scenario = new MarketScenario(ScenarioType.Decline, new Price(intrinsic.Value * 1.2m), intrinsic);
            var preplay = new MarketPreplay();
            var preplayResult = preplay.Run(_session, _loop, scenario, seed: level.Id.GetHashCode());

            // 参与者
            _institutionB = new InstitutionB(_session, new ParticipantId("机构B"), intrinsic,
                cash: 1_000_000_000m, initialHolding: level.MarketMakerHolding,
                baseDepthPerLevel: 80, levels: 20, seed: 88);
            _loop.AddParticipant(_institutionB);

            _retail = new RetailProfilePool(_session, new ParticipantId("散户池"), intrinsic, seed: 42);
            _loop.AddParticipant(_retail);

            _passive = new PassiveFlow(_session, new ParticipantId("被动资金"), level.FloatShares, seed: 77);
            _loop.AddParticipant(_passive);

            _ai = new AIMainForce(_session, new ParticipantId("AI主力"),
                intrinsic, cash: 100_000_000m, initialHolding: level.AiHolding, initialCost: intrinsic,
                sensitivity: level.AiSensitivity, profile: StrategyProfile.Balanced, seed: 99);
            _loop.AddParticipant(_ai);

            // 采集器
            _collector = new MarketDataCollector(_loop, preplayResult.PreviousClose);
            _collector.PreloadHistory(preplayResult.HistoryCandles);
            _regulator = new Regulator(Player);
            _judge = new LevelJudge(level);

            _loop.Session.OnTradeDetailed += t => _regulator.OnTrade(t,
                t.TakerId.Equals(Player) || t.MakerId.Equals(Player));
            _loop.OnTick += _ =>
            {
                var cur = _loop.Session.Engine.LastPrice;
                decimal? ratio = (_prevPriceForVolatility is { } prev && cur is { } c && prev > 0)
                    ? (c.Value - prev) / prev : (decimal?)null;
                _prevPriceForVolatility = cur?.Value;
                _regulator.OnTick(ratio);
                _maxHeatReached = System.Math.Max(_maxHeatReached, _regulator.Heat);
                _newsSystem?.Tick();
                if (_regulator.GetStatus().IsFailed && !IsLevelOver) EndLevel();
            };
            _equityCollector = new EquityCurveCollector(_loop, _player,
                () => _ai.Account, () => _institutionB.Account,
                () => _loop.Session.Engine.LastPrice);
            _chipCollector = new ChipSnapshotCollector(_loop, _session);
            _chipCollector.ImportHistory(preplayResult.ChipHistory);
            _newsSystem = new NewsSystem(_retail.Sentiment);
            _waterArmy = new WaterArmyNetwork();
            _blockSignal = new BlockTradeSignal(_retail.Sentiment, _regulator);

            _loop.Start();
            IsPreMarket = true;
        }

        // —— 公开接口(供 UI 调用) ——

        public void BeginTrading()
        {
            IsPreMarket = false;
        }

        public OrderResult SubmitOrder(string side, string type, decimal price, int qty)
        {
            var s = side == "buy" ? Side.Buy : Side.Sell;
            var t = type == "market" ? OrderType.Market : OrderType.Limit;
            var p = t == OrderType.Limit ? new Price(price) : Price.Zero;
            return _session.Submit(new OrderRequest(Player, s, t, p, new Quantity(qty)));
        }

        public bool CancelOrder(long orderId)
        {
            return _session.Cancel(Player, new OrderId(orderId));
        }

        public void Pause() => _loop.Pause();
        public void Resume() => _loop.Resume();

        public void StartNextDay()
        {
            if (!_loop.IsDayClosed) return;
            _newsSystem.OnNewDay();
            _retail.Sentiment.DailyDecay();
            _waterArmy.OnNewDay(_player, _newsSystem);
            _loop.StartNextDay();
        }

        public void SkipDay()
        {
            while (!_loop.IsDayClosed && !_loop.IsFinished) _loop.Step();
        }

        // —— 快照(供 UI 读取) ——

        public MarketSnapshot GetSnapshot() => new()
        {
            Day = _loop.Clock.CurrentDay,
            TotalDays = _loop.Clock.TotalDays,
            TickOfDay = _loop.Clock.CurrentTickOfDay,
            TicksPerDay = _loop.Clock.TicksPerDay,
            IsPaused = _loop.IsPaused,
            IsDayClosed = _loop.IsDayClosed,
            IsPreMarket = IsPreMarket,
            IsFinished = _loop.IsFinished,
            IsLevelOver = IsLevelOver,
            LastPrice = _loop.Session.Engine.LastPrice?.Value ?? 0m,
            BestBid = _loop.Session.Engine.View.BestBid?.Value ?? 0m,
            BestAsk = _loop.Session.Engine.View.BestAsk?.Value ?? 0m,
            RegulatorHeat = _regulator.Heat,
            InfoHeat = _regulator.InfoHeat,
            Sentiment = _retail.Sentiment.Value,
            Confidence = _retail.Sentiment.Confidence,
            NewsBias = _retail.Sentiment.NewsBias,
            PlayerCash = _player.Cash,
            PlayerAvailable = _player.AvailableCash,
            PlayerHolding = _player.Position.Total.Value,
            PlayerAvailableHolding = _player.Position.Available.Value,
            PlayerCost = _player.Position.AverageCost.Value,
            PlayerEquity = _player.TotalEquity(_loop.Session.Engine.LastPrice ?? new Price(10m)),
            WaterArmyLevel = _waterArmy.Level,
            WaterArmyActive = _waterArmy.IsActive,
        };
    }

    /// <summary>Unity 端的简化快照(供 UI 绑定)。</summary>
    public struct MarketSnapshot
    {
        public int Day, TotalDays, TickOfDay, TicksPerDay;
        public bool IsPaused, IsDayClosed, IsPreMarket, IsFinished, IsLevelOver;
        public decimal LastPrice, BestBid, BestAsk;
        public decimal RegulatorHeat, InfoHeat;
        public decimal Sentiment, Confidence, NewsBias;
        public decimal PlayerCash, PlayerAvailable, PlayerHolding, PlayerAvailableHolding, PlayerCost, PlayerEquity;
        public int WaterArmyLevel;
        public bool WaterArmyActive;
    }
}
