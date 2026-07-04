using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;
using Microsoft.AspNetCore.SignalR;

namespace IamZhuli.Web;

/// <summary>
/// 游戏大脑的托管单例。持有 SimulationLoop + 全局锁,所有读写(含读盘口)串行化。
/// 初始化逻辑照搬 SimCli:做市商预挂五档、玩家账户1亿、loop.Start()。
/// </summary>
public sealed class GameSingleton
{
    private static readonly ParticipantId Player = new("Player");
    private static readonly ParticipantId MarketMaker = new("做市商");

    private readonly SimulationLoop _loop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Account _player;

    public IHubContext<GameHub> Hub { get; }
    public bool IsInitialized { get; private set; }

    public GameSingleton(IHubContext<GameHub> hub)
    {
        Hub = hub;
        var rules = new MarketRules
        {
            PreviousClose = new Price(10.00m),
            PriceLimitRatio = 0.10m,
            TickSize = new Price(0.01m)
        };
        var engine = new MatchingEngine(rules);
        // 网页版用较小的 ticksPerDay(30)便于观察,POC 可调
        _loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay: 60, totalDays: 30));
        _player = _loop.Session.GetOrCreateAccount(Player, 100_000_000m);
        var mm = _loop.Session.GetOrCreateAccount(MarketMaker, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10.00m));
        SeedMarket(_loop.Session);
        _loop.Start();
        IsInitialized = true;
    }

    private static void SeedMarket(TradingSession s)
    {
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.05m), new Quantity(500)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.04m), new Quantity(300)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.03m), new Quantity(200)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.02m), new Quantity(100)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(50)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.99m), new Quantity(50)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.98m), new Quantity(100)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.97m), new Quantity(200)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.96m), new Quantity(300)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.95m), new Quantity(500)));
    }

    /// <summary>推进一个 tick 并返回该 tick 后的快照(由 GameHostService 调用)。
    /// 一次持锁完成 Step + 构建快照,避免事件回调里重新加锁导致死锁。</summary>
    public async Task<MarketSnapshotDto?> StepAsync()
    {
        if (_loop.IsFinished) return null;
        await _gate.WaitAsync();
        try
        {
            _loop.Step();
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
            Asks: asks, Bids: bids, Account: acc);
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
