using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 散户市场:4 群体的容器与协调者。
/// - 创建散户账户、注入初始持仓(让止损盘有货可止)
/// - 持有 SharedRetailState(成本/持仓/近期价格)
/// - 每 tick 先记录现价、更新散户整体成本,再驱动各群体 Act
/// - 日切时把散户 T+1 持仓转入可卖(由 Session.OnNewTradingDay 统一处理)
/// </summary>
public sealed class RetailMarket : IParticipant
{
    public ParticipantId Id { get; }
    private readonly List<RetailCrowd> _crowds = new();
    private readonly Account _account;
    private readonly SharedRetailState _state;
    private readonly Random _rng;
    private readonly Price _intrinsic;

    public IReadOnlyList<RetailCrowd> Crowds => _crowds;
    public SharedRetailState State => _state;

    /// <summary>创建散户市场并注册账户。intrinsicValue=内在价值,initialHolding=初始持仓手数。</summary>
    public RetailMarket(TradingSession session, ParticipantId id, Price intrinsicValue,
                        decimal cash, int initialHolding, int? seed = null)
    {
        Id = id;
        _intrinsic = intrinsicValue;
        _rng = new Random(seed ?? Environment.TickCount);
        _account = session.GetOrCreateAccount(id, cash);
        if (initialHolding > 0)
        {
            _account.Position.Seed(new Quantity(initialHolding), intrinsicValue);
        }
        _state = new SharedRetailState
        {
            AverageCost = initialHolding > 0 ? intrinsicValue : Price.Zero,
            TotalHolding = initialHolding
        };

        // 默认 4 群体配置(力量比例:跟风多、抄底中、止损中、价投少)
        _crowds.Add(new MomentumChaser(_account, _state, id, intrinsicValue, strength: 600));
        _crowds.Add(new BargainHunter(_account, _state, id, intrinsicValue, strength: 400));
        _crowds.Add(new StopLossSeller(_account, _state, id, intrinsicValue, strength: 400));
        _crowds.Add(new ValueInvestor(_account, _state, id, intrinsicValue, strength: 200));
    }

    /// <summary>自定义群体配置(测试/调参用)。</summary>
    public void ReplaceCrowds(IEnumerable<RetailCrowd> crowds)
    {
        _crowds.Clear();
        _crowds.AddRange(crowds);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        // 记录价格(现价为空时用兜底价,确保 Momentum 有数据)
        var view = session.Engine.View;
        var recPrice = view.LastPrice ?? view.BestBid ?? view.BestAsk ?? _intrinsic;
        _state.RecordPrice(recPrice);

        // 同步散户整体成本/持仓(从账户读,因为成交会改)
        _state.AverageCost = _account.Position.AverageCost;
        _state.TotalHolding = _account.Position.Total.Value;

        // —— 基础随机噪音:模拟散户日常随机小额买卖,避免盘口"冷启动"死寂 ——
        InjectNoise(session, rng);

        foreach (var c in _crowds)
        {
            try { c.Act(session, clock, _rng); }
            catch { /* 单群体异常不影响整体 */ }
        }
    }

    /// <summary>每 tick 注入少量随机买卖单,让价格有自然波动。
    /// 一部分单用市价直接吃对手盘(推动现价),一部分挂限价单增加深度。</summary>
    private void InjectNoise(TradingSession session, Random rng)
    {
        var view = session.Engine.View;
        // 现价可能为空(开盘未成交);用 BestBid/BestAsk 或内在价值兜底,确保冷启动也能交易
        decimal price = view.LastPrice?.Value
                        ?? view.BestAsk?.Value
                        ?? view.BestBid?.Value
                        ?? _intrinsic.Value;
        if (price <= 0) return;
        if (rng.NextDouble() > 0.75) return;   // 75% tick 有噪音单
        bool isBuy = rng.NextDouble() > 0.5;
        int qty = (rng.Next(1, 6)) * 10;   // 10~50 手
        bool aggressive = rng.NextDouble() > 0.5;   // 一半概率用市价吃对手盘

        try
        {
            if (isBuy)
            {
                if (aggressive)
                    session.Submit(new OrderRequest(Id, Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty)));
                else
                {
                    var bid = view.BestBid ?? new Price(price);
                    var raw = bid.Value - (decimal)rng.NextDouble() * 0.01m;
                    var p = new Price(Math.Max(Align(raw), session.Engine.Rules.LowerLimit.Value));
                    session.Submit(new OrderRequest(Id, Side.Buy, OrderType.Limit, p, new Quantity(qty)));
                }
            }
            else
            {
                if (_account.Position.Available.Value < qty) return;
                if (aggressive)
                    session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty)));
                else
                {
                    var ask = view.BestAsk ?? new Price(price);
                    var raw = ask.Value + (decimal)rng.NextDouble() * 0.01m;
                    var p = new Price(Math.Min(Align(raw), session.Engine.Rules.UpperLimit.Value));
                    session.Submit(new OrderRequest(Id, Side.Sell, OrderType.Limit, p, new Quantity(qty)));
                }
            }
        }
        catch { /* 资金/持仓不足等,忽略 */ }
    }

    private static decimal Align(decimal v)
    {
        const decimal tick = 0.01m;
        return Math.Round(Math.Round(v / tick) * tick, 2);
    }

    public void OnNewDay()
    {
        // T+1 解锁由 Session.OnNewTradingDay 统一处理;群体无需额外动作。
        // 可在此重置短期动量窗口等,POC 暂不需要。
    }
}
