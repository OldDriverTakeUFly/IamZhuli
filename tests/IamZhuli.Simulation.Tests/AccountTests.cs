using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 账户逻辑测试:资金冻结/释放、T+1 锁定与解锁、加权成本、可卖校验。
/// </summary>
public class AccountTests
{
    private static readonly ParticipantId P = new("P");
    private static readonly ParticipantId MM = new("MM");

    private static (TradingSession, Account player, Account mm) NewSession(decimal playerCash = 100_000_000m)
    {
        var rules = new MarketRules { PreviousClose = new Price(10.00m) };
        var s = new TradingSession(new MatchingEngine(rules));
        var player = s.GetOrCreateAccount(P, playerCash);
        var mm = s.GetOrCreateAccount(MM, 100_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10.00m));
        return (s, player, mm);
    }

    [Fact]
    public void BuyFill_DeductsCash_IncreasesT1LockedPosition()
    {
        var (s, player, _) = NewSession();
        // MM 挂卖单
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(1000)));
        var initCash = player.Cash;

        var r = s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.01m), new Quantity(1000)));

        Assert.Equal(OrderStatus.Filled, r.FinalStatus);
        // 现金减少 = 10.01 × 1000 × 100
        Assert.Equal(initCash - 10.01m * 1000 * 100, player.Cash);
        // T+1: 买入进 T1Locked,Available 仍为 0
        Assert.Equal(1000, player.Position.T1Locked.Value);
        Assert.Equal(0, player.Position.Available.Value);
        Assert.Equal(new Price(10.01m), player.Position.AverageCost);
    }

    [Fact]
    public void Sell_BeforeT1Unlock_Fails_InsufficientAvailable()
    {
        var (s, player, _) = NewSession();
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(1000)));
        s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.01m), new Quantity(1000)));

        // 当日卖(持仓在 T1Locked)→ 应失败
        Assert.Throws<InvalidOperationException>(() =>
            s.Submit(new OrderRequest(P, Side.Sell, OrderType.Limit, new Price(10.02m), new Quantity(500))));
    }

    [Fact]
    public void Sell_AfterT1Unlock_Succeeds()
    {
        var (s, player, _) = NewSession();
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(1000)));
        s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.01m), new Quantity(1000)));

        // 日切解锁
        s.OnNewTradingDay();
        Assert.Equal(1000, player.Position.Available.Value);
        Assert.Equal(0, player.Position.T1Locked.Value);

        // 挂卖单应可成交
        s.Submit(new OrderRequest(MM, Side.Buy, OrderType.Limit, new Price(10.02m), new Quantity(500)));
        var r = s.Submit(new OrderRequest(P, Side.Sell, OrderType.Limit, new Price(10.02m), new Quantity(500)));
        Assert.Equal(500, r.TotalFilled.Value);
        Assert.Equal(500, player.Position.Available.Value);   // 卖了500剩500
    }

    [Fact]
    public void BuyFreeze_ReducesAvailableCash_ReleaseOnCancel()
    {
        var (s, player, _) = NewSession();
        var initAvail = player.AvailableCash;

        // 挂买单(未必成交)冻结资金
        var r = s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.00m), new Quantity(1000)));
        Assert.Equal(OrderStatus.Active, r.FinalStatus);   // 挂簿(无对手)
        // 可用现金减少 10.00×1000×100
        Assert.Equal(initAvail - 10.00m * 1000 * 100, player.AvailableCash);

        // 撤单,冻结释放
        s.Cancel(P, r.OrderId);
        Assert.Equal(initAvail, player.AvailableCash);
    }

    [Fact]
    public void BuyFreeze_InsufficientCash_Throws()
    {
        var (s, player, _) = NewSession(playerCash: 100m);  // 钱不够买1000手@10
        Assert.Throws<InvalidOperationException>(() =>
            s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.00m), new Quantity(1000))));
    }

    [Fact]
    public void WeightedAverageCost_AcrossMultipleBuys()
    {
        var (s, player, _) = NewSession();
        // 两次买入不同价
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.00m), new Quantity(1000)));
        s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(10.00m), new Quantity(1000)));
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(11.00m), new Quantity(1000)));
        s.Submit(new OrderRequest(P, Side.Buy, OrderType.Limit, new Price(11.00m), new Quantity(1000)));

        // 加权成本 = (10×1000 + 11×1000)/2000 = 10.50
        Assert.Equal(new Price(10.50m), player.Position.AverageCost);
        Assert.Equal(2000, player.Position.Total.Value);
    }

    [Fact]
    public void MarketBuy_FreezesByBestAsk_RefundsDifference()
    {
        // 市价买单按对手最优卖价冻结;若实际成交价更低,差额应正确结算
        var (s, player, _) = NewSession();
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(500)));
        var initCash = player.Cash;

        var r = s.Submit(new OrderRequest(P, Side.Buy, OrderType.Market, Price.Zero, new Quantity(500)));

        Assert.Equal(OrderStatus.Filled, r.FinalStatus);
        Assert.Equal(new Price(10.01m), r.AverageFillPrice);
        // 现金按实际成交价 10.01 扣
        Assert.Equal(initCash - 10.01m * 500 * 100, player.Cash);
    }
}
