using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.AI;

/// <summary>
/// 机构B(合三为一:做市+风险控制+操盘)。
/// 取代无限做市商。正常时做市提供流动性,风险升高时收紧做市,风险极高时转为方向操作。
/// 资金理论无限,但风险控制形成"软约束":盘口会变薄但不会突然断层。
/// </summary>
public sealed class InstitutionB : IParticipant
{
    public ParticipantId Id { get; }
    private readonly Account _account;
    private readonly Price _fairValue;
    private readonly MarketMakerRiskController _risk;
    private readonly IntentRecognizer _recognizer;
    private readonly SessionMarketDataSnapshot _snapshot;
    private readonly Random _rng;

    /// <summary>做市参数(正常状态下的挂单)。</summary>
    private readonly int _baseDepthPerLevel;   // 每档深度(手)
    private readonly int _levels;              // 维护档位数

    /// <summary>内心独白(供复盘)。</summary>
    public List<(long Tick, RiskLevel Level, string Action, string Detail)> Thoughts { get; } = new();
    public Account Account => _account;
    public RiskLevel CurrentRiskLevel { get; private set; } = RiskLevel.Low;

    public InstitutionB(TradingSession session, ParticipantId id, Price fairValue,
                        decimal cash, int initialHolding,
                        int baseDepthPerLevel = 300, int levels = 5, int? seed = null)
    {
        Id = id;
        _fairValue = fairValue;
        _account = session.GetOrCreateAccount(id, cash);
        if (initialHolding > 0) _account.Position.Seed(new Quantity(initialHolding), fairValue);
        _risk = new MarketMakerRiskController(fairValue, maxPositionValue: cash * 1.5m);
        _recognizer = new IntentRecognizer();
        _snapshot = new SessionMarketDataSnapshot(session);
        session.OnTrade += (p, q, s) => { _risk.OnTrade(q); _recognizer.RecordTrade(q.Value); };
        _baseDepthPerLevel = baseDepthPerLevel;
        _levels = levels;
        _rng = new Random(seed ?? Environment.TickCount);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        _risk.OnTick(session.Engine.LastPrice);
        _recognizer.Observe(_snapshot);

        var assessment = _risk.Assess(_account);
        CurrentRiskLevel = assessment.Level;

        // 记录内心独白(风险变化或定期)。无上限,供复盘完整查看。
        if (clock.CurrentTickOfDay % 15 == 0 || assessment.Level >= RiskLevel.High)
        {
            Thoughts.Add((clock.TotalTicksElapsed, assessment.Level,
                assessment.Level >= RiskLevel.Critical ? "转为操盘" : "做市",
                assessment.Detail));
        }

        // 根据风险等级执行
        switch (assessment.Level)
        {
            case RiskLevel.Critical:
                // 极高风险:停止做市,转为方向操作(减仓/反手)
                HandleCriticalRisk(session, assessment);
                break;
            case RiskLevel.High:
                // 高风险:大幅减少做市 + 开始管理敞口
                MakeMarket(session, depthFactor: 0.3m);
                ManageExposure(session, assessment);
                break;
            default:
                // 低/中风险:正常做市(深度随风险调整)
                MakeMarket(session, depthFactor: MarketMakerRiskController.DepthFactor(assessment.Level));
                break;
        }

        // 高估获利了结:无论风险等级,价格远超公允值时按概率减仓(形成拉升卖压)
        TakeProfitIfOvervalued(session);
        // 亏损止损:持仓浮亏超限时纪律性减仓(机构风控铁律,比散户更果断)
        CutLossIfUnderwater(session);
    }

    /// <summary>持仓浮亏止损:机构对亏损容忍度低,亏损超阈值时果断减仓。
    /// 机构的止损阈值比散户更统一(纪律性强),不会像散户那样死扛。</summary>
    private void CutLossIfUnderwater(TradingSession session)
    {
        int available = _account.Position.Available.Value;
        if (available < 50) return;
        decimal cost = _account.Position.AverageCost.Value;
        if (cost <= 0) return;
        var view = session.Engine.View;
        decimal price = view.LastPrice?.Value ?? _fairValue.Value;
        decimal loss = (cost - price) / cost;
        if (loss <= 0.05m) return;   // 亏<5%不止损(机构容忍小幅回撤)
        // 亏5%=15%概率,亏10%=35%概率,亏15%=55%概率
        double prob = Math.Min(0.6, (double)loss * 4.0 - 0.05);
        if (_rng.NextDouble() >= prob) return;
        // 止损量:亏损越大砍越多
        int ratio = loss > 0.12m ? 2 : 4;   // 亏>12%砍一半,否则砍1/4
        int qty = Math.Max(50, available / ratio);
        qty = (qty / 10) * 10;
        try { session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty))); }
        catch { }
    }

    /// <summary>价格远超公允值时获利了结:越被高估,减仓概率越高、量越大。
    /// 这是拉升时的结构性卖压——机构不会在高位还傻拿筹码。</summary>
    private void TakeProfitIfOvervalued(TradingSession session)
    {
        var view = session.Engine.View;
        decimal price = view.LastPrice?.Value ?? _fairValue.Value;
        decimal overvaluedRatio = price / _fairValue.Value - 1m;   // 高估幅度(>0=高估)
        if (overvaluedRatio <= 0.05m) return;   // 高估<5%不减仓
        int available = _account.Position.Available.Value;
        if (available < 50) return;
        // 高估5%=10%概率,高估20%=40%概率
        double prob = Math.Min(0.4, (double)overvaluedRatio * 2.0);
        if (_rng.NextDouble() >= prob) return;
        // 减仓量随高估幅度增大:高估5%卖1/8,高估20%卖1/3
        int ratio = overvaluedRatio > 0.15m ? 3 : 8;
        int qty = Math.Max(50, available / ratio);
        qty = (qty / 10) * 10;   // 凑整
        try { session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty))); }
        catch { }
    }

    /// <summary>做市:在现价附近双边挂单,深度/价差随风险调整。</summary>
    private void MakeMarket(TradingSession session, decimal depthFactor)
    {
        var view = session.Engine.View;
        decimal price = view.LastPrice?.Value ?? view.BestBid?.Value ?? view.BestAsk?.Value ?? _fairValue.Value;
        decimal spread = MarketMakerRiskController.SpreadFactor(CurrentRiskLevel);
        int depth = Math.Max(10, (int)(_baseDepthPerLevel * depthFactor));

        // 卖盘(上方各档)
        for (int i = 1; i <= _levels; i++)
        {
            decimal askPrice = Align(price + i * spread);
            if (!LevelHasEnough(view.TopAsks(_levels), askPrice, depth / 2))
                TryPlace(session, Side.Sell, askPrice, depth);
        }
        // 买盘(下方各档)
        for (int i = 1; i <= _levels; i++)
        {
            decimal bidPrice = Align(price - i * spread);
            if (bidPrice < session.Engine.Rules.LowerLimit.Value) break;
            if (!LevelHasEnough(view.TopBids(_levels), bidPrice, depth / 2))
                TryPlace(session, Side.Buy, bidPrice, depth);
        }
    }

    /// <summary>管理敞口:持仓偏离大时,适度减仓(卖一些)。</summary>
    private void ManageExposure(TradingSession session, RiskAssessment risk)
    {
        if (risk.PositionExposure > 0.7m && _account.Position.Available.Value > 100)
        {
            // 减仓:市价卖一部分
            int qty = Math.Min(_account.Position.Available.Value / 5, 500);
            qty = Math.Max(10, (qty / 10) * 10);
            if (_rng.NextDouble() < 0.4)
            {
                try { session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty))); }
                catch { }
            }
        }
    }

    /// <summary>极高风险:转为方向操作。
    /// 如果价格远高于公允值(做多被套风险)→ 卖出减仓;
    /// 如果价格远低于公允值(空头被套)→ 反向买入(抄底)。</summary>
    private void HandleCriticalRisk(TradingSession session, RiskAssessment risk)
    {
        var view = session.Engine.View;
        decimal price = view.LastPrice?.Value ?? _fairValue.Value;
        bool overvalued = price > _fairValue.Value * 1.05m;   // 价格偏高

        if (overvalued && _account.Position.Available.Value > 50)
        {
            // 减仓避险
            int qty = Math.Min(_account.Position.Available.Value / 3, 1000);
            qty = Math.Max(20, (qty / 10) * 10);
            if (_rng.NextDouble() < 0.5)
            {
                try { session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty))); }
                catch { }
            }
        }
        else if (!overvalued && price < _fairValue.Value * 0.95m)
        {
            // 价格偏低,小幅抄底(反手)
            int qty = _rng.Next(2, 6) * 10;
            if (_rng.NextDouble() < 0.3)
            {
                try { session.Submit(new OrderRequest(Id, Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty))); }
                catch { }
            }
        }
    }

    private bool LevelHasEnough(IReadOnlyList<(Price Price, Quantity TotalQty)> levels, decimal target, int threshold)
    {
        foreach (var (p, q) in levels)
            if (Math.Abs(p.Value - target) < 0.005m) return q.Value >= threshold;
        return false;
    }

    private void TryPlace(TradingSession s, Side side, decimal price, int qty)
    {
        try { s.Submit(new OrderRequest(Id, side, OrderType.Limit, new Price(price), new Quantity(qty))); }
        catch { }
    }

    private static decimal Align(decimal v) => Math.Round(Math.Round(v / 0.01m) * 0.01m, 2);
    public void OnNewDay() { }
}
