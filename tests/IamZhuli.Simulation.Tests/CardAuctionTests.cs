using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 集中撮合(卡牌模式回合制)核心路径测试。
/// 验证:TryRestForAuction 的资金/券池校验(不透支)、拍卖结算的现金守恒、
/// 做空在拍卖中的完整生命周期(预扣券池→做空成交→平仓归还)、清簿退款无泄漏。
/// </summary>
public class CardAuctionTests
{
    private static readonly ParticipantId Buyer = new("Buyer");
    private static readonly ParticipantId Seller = new("Seller");
    private static readonly ParticipantId Shorter = new("Shorter");

    private static (MatchingEngine engine, TradingSession session) Setup()
    {
        var rules = new MarketRules
        {
            PreviousClose = new Price(10m),
            PriceLimitRatio = 0.10m,
            TickSize = new Price(0.01m),
            FloatShares = new Quantity(200000)
        };
        var engine = new MatchingEngine(rules);
        var session = new TradingSession(engine);
        session.InitShortablePool(200000);
        return (engine, session);
    }

    // ══════════════════════════════════════
    // TryRestForAuction 校验(不透支)
    // ══════════════════════════════════════

    [Fact]
    public void RestForAuction_RejectsBuyIntentWithoutCash()
    {
        var (engine, session) = Setup();
        var poor = session.GetOrCreateAccount(Buyer, 10_000m);   // 只够买10手@10元

        // 意图买100手@10元(需10万) → 资金不足,作废
        bool ok = session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(10m), new Quantity(100)));
        Assert.False(ok, "资金不足的买入意图应作废");
        // 账户不受污染:无冻结、无持仓、订单簿为空
        Assert.Equal(0m, poor.FrozenCash);
        Assert.Equal(10_000m, poor.AvailableCash);
        Assert.Null(engine.View.BestBid);
    }

    [Fact]
    public void RestForAuction_FreezesCashForBuyIntent()
    {
        var (_, session) = Setup();
        session.GetOrCreateAccount(Buyer, 1_000_000m);

        bool ok = session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(10m), new Quantity(100)));
        Assert.True(ok);
        var buyer = session.GetAccount(Buyer)!;
        // 冻结 10元×100手×100股 = 100,000
        Assert.Equal(100_000m, buyer.FrozenCash);
        Assert.Equal(900_000m, buyer.AvailableCash);
    }

    [Fact]
    public void RestForAuction_PoolExhaustion_FirstComeFirstServed()
    {
        var (_, session) = Setup();
        var shorter = session.GetOrCreateAccount(Shorter, 100_000_000m);

        // 券池下限1000:第一笔做空600手预扣成功,第二笔600手券池不足作废
        Assert.True(session.ShortablePool >= 1000);
        bool ok1 = session.TryRestForAuction(new OrderRequest(
            Shorter, Side.Sell, OrderType.Limit, new Price(10.5m), new Quantity(600), IsShort: true));
        Assert.True(ok1);
        int poolAfterFirst = session.ShortablePool;
        Assert.Equal(session.TotalShortable - 600, poolAfterFirst);

        bool ok2 = session.TryRestForAuction(new OrderRequest(
            Shorter, Side.Sell, OrderType.Limit, new Price(10.5m), new Quantity(poolAfterFirst + 1), IsShort: true));
        Assert.False(ok2, "券池不足的做空意图应作废");
        Assert.Equal(poolAfterFirst, session.ShortablePool);   // 不重复扣
        Assert.Equal(0, shorter.Position.ShortQty.Value);   // 未成交不开仓
    }

    [Fact]
    public void RestForAuction_RejectsSellWithoutPosition()
    {
        var (_, session) = Setup();
        session.GetOrCreateAccount(Seller, 1_000_000m);   // 无持仓

        bool ok = session.TryRestForAuction(new OrderRequest(
            Seller, Side.Sell, OrderType.Limit, new Price(10.5m), new Quantity(50)));
        Assert.False(ok, "无持仓的卖出意图应作废");
    }

    // ══════════════════════════════════════
    // 拍卖撮合端到端(现金守恒)
    // ══════════════════════════════════════

    [Fact]
    public void Auction_EndToEnd_CashAndPositionConserved()
    {
        var (engine, session) = Setup();
        var buyer = session.GetOrCreateAccount(Buyer, 1_000_000m);
        var seller = session.GetOrCreateAccount(Seller, 500_000m);
        seller.Position.Seed(new Quantity(1000), new Price(10m));
        decimal cashSumBefore = buyer.Cash + seller.Cash;

        // 双方意图挂入(买10.05×100 / 卖10.03×100,价格交叉)
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(10.05m), new Quantity(100))));
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Seller, Side.Sell, OrderType.Limit, new Price(10.03m), new Quantity(100))));

        // 集合竞价定价 + 集中撮合 + 结算
        var auction = engine.CallAuction();
        Assert.True(auction.HasValue, "价格交叉应产生竞价结果");
        Assert.Equal(100, auction!.Value.Volume);
        engine.SetLastPrice(auction.Value.Price);
        var trades = engine.SweepAtPrice(auction.Value.Price);
        Assert.Single(trades);
        Assert.Equal(100, trades[0].Quantity.Value);
        Assert.Equal(auction.Value.Price.Value, trades[0].Price.Value);   // 成交价统一为竞价价
        session.SettleAuctionTrades(trades);

        // 持仓转移
        Assert.Equal(100, buyer.Position.Total.Value);
        Assert.Equal(900, seller.Position.Total.Value);
        // 现金守恒:买方减少 = 卖方增加(冻结差额已退回)
        Assert.Equal(0m, buyer.FrozenCash);
        decimal buyerPaid = 1_000_000m - buyer.Cash;
        decimal sellerGot = seller.Cash - 500_000m;
        Assert.Equal(buyerPaid, sellerGot);
        Assert.Equal(cashSumBefore, buyer.Cash + seller.Cash);
        // 成交价应在双方限价之间(统一竞价价)
        Assert.InRange(trades[0].Price.Value, 10.03m, 10.05m);
    }

    [Fact]
    public void Auction_PartialFill_RemainderStaysOnBook()
    {
        var (engine, session) = Setup();
        var buyer = session.GetOrCreateAccount(Buyer, 1_000_000m);
        var seller = session.GetOrCreateAccount(Seller, 500_000m);
        seller.Position.Seed(new Quantity(1000), new Price(10m));

        // 买150手 vs 卖100手 → 成交100,买方剩50手挂簿
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(10.05m), new Quantity(150))));
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Seller, Side.Sell, OrderType.Limit, new Price(10.03m), new Quantity(100))));

        var auction = engine.CallAuction();
        Assert.True(auction.HasValue);
        Assert.Equal(100, auction!.Value.Volume);   // 最大成交量=100
        var trades = engine.SweepAtPrice(auction.Value.Price);
        session.SettleAuctionTrades(trades);

        Assert.Equal(100, buyer.Position.Total.Value);
        // 剩余50手买单仍在簿(下一回合/连续撮合的流动性)
        var open = session.GetOpenOrders(Buyer).ToList();
        Assert.Single(open);
        Assert.Equal(50, open[0].RemainingQty.Value);
        // 冻结只保留剩余50手部分
        Assert.Equal(10.05m * 50 * 100, buyer.FrozenCash);
    }

    // ══════════════════════════════════════
    // 做空的拍卖完整生命周期
    // ══════════════════════════════════════

    [Fact]
    public void Auction_ShortSellAndCover_FullCycle()
    {
        var (engine, session) = Setup();
        var shorter = session.GetOrCreateAccount(Shorter, 10_000_000m);
        var buyer = session.GetOrCreateAccount(Buyer, 1_000_000m);
        var seller = session.GetOrCreateAccount(Seller, 500_000m);
        seller.Position.Seed(new Quantity(1000), new Price(10m));
        int poolBefore = session.ShortablePool;
        decimal shorterCashBefore = shorter.Cash;
        decimal buyerCashBefore = buyer.Cash;
        decimal sellerCashBefore = seller.Cash;

        // —— 回合1:做空拍卖 ——
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Shorter, Side.Sell, OrderType.Limit, new Price(10.10m), new Quantity(50), IsShort: true)));
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(10.10m), new Quantity(50))));
        var r1 = engine.CallAuction();
        Assert.True(r1.HasValue);
        var trades1 = engine.SweepAtPrice(r1!.Value.Price);
        Assert.NotEmpty(trades1);
        session.SettleAuctionTrades(trades1);

        Assert.Equal(50, shorter.Position.ShortQty.Value);   // 空头开仓
        Assert.Equal(poolBefore - 50, session.ShortablePool);   // 券池扣减(借出)
        Assert.True(shorter.MarginFrozen > 0, "做空应冻结保证金");
        // 做空结算后权益守恒:现金+保证金占用不变,空头负债按成交价计
        decimal sellPrice = trades1[0].Price.Value;
        Assert.Equal(shorterCashBefore + sellPrice * 50 * 100, shorter.Cash);

        // —— 清场(模拟下一回合开始) ——
        session.ClearBookAndRefund();
        Assert.Equal(poolBefore - 50, session.ShortablePool);   // 已借出的不还

        // —— 回合2:平仓拍卖 ——
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Shorter, Side.Buy, OrderType.Limit, new Price(10.10m), new Quantity(50), IsShort: true)));
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Seller, Side.Sell, OrderType.Limit, new Price(10.05m), new Quantity(50))));
        var r2 = engine.CallAuction();
        Assert.True(r2.HasValue);
        var trades2 = engine.SweepAtPrice(r2!.Value.Price);
        Assert.NotEmpty(trades2);
        session.SettleAuctionTrades(trades2);

        Assert.Equal(0, shorter.Position.ShortQty.Value);   // 空头清零
        Assert.Equal(poolBefore, session.ShortablePool);   // 券池全额归还
        Assert.Equal(0m, shorter.MarginFrozen);   // 保证金释放
        // 现金守恒:做空者净现金变化 = 做空所得 - 平仓花费 = 每手价差×量(利润只计一次)
        decimal coverPrice = trades2[0].Price.Value;
        decimal expectedShorterCash = shorterCashBefore + (sellPrice - coverPrice) * 50 * 100;
        Assert.Equal(expectedShorterCash, shorter.Cash);
        // 三方现金守恒:shorter+buyer+seller 现金增量之和 = 0(零和,无凭空生钱)
        decimal buyerDelta = buyer.Cash - buyerCashBefore;
        decimal sellerDelta = seller.Cash - sellerCashBefore;
        Assert.Equal(expectedShorterCash - shorterCashBefore + buyerDelta + sellerDelta,
                     shorter.Cash - shorterCashBefore + buyerDelta + sellerDelta);
        Assert.Equal(0m, shorter.Cash - shorterCashBefore + buyerDelta + sellerDelta, 2);
    }

    // ══════════════════════════════════════
    // 清簿退款(无泄漏)
    // ══════════════════════════════════════

    [Fact]
    public void ClearBookAndRefund_ReleasesFreezeAndReturnsPool()
    {
        var (_, session) = Setup();
        var buyer = session.GetOrCreateAccount(Buyer, 1_000_000m);
        var shorter = session.GetOrCreateAccount(Shorter, 10_000_000m);
        int poolBefore = session.ShortablePool;

        // 挂入未成交的买单+做空卖单
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(9.50m), new Quantity(100))));
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Shorter, Side.Sell, OrderType.Limit, new Price(10.50m), new Quantity(80), IsShort: true)));
        Assert.Equal(9.50m * 100 * 100, buyer.FrozenCash);   // 冻结=限价×量
        Assert.Equal(poolBefore - 80, session.ShortablePool);

        // 清簿:冻结释放、券池归还
        session.ClearBookAndRefund();
        Assert.Equal(0m, buyer.FrozenCash);
        Assert.Equal(1_000_000m, buyer.AvailableCash);   // 无泄漏
        Assert.Equal(poolBefore, session.ShortablePool);   // 券池全额归还
        Assert.Equal(0, shorter.Position.ShortQty.Value);   // 未成交不开空头
        Assert.Empty(session.GetOpenOrders(Buyer).ToList());
        Assert.Empty(session.GetOpenOrders(Shorter).ToList());
    }

    [Fact]
    public void ClearBookAndRefund_EmptyBook_IsSafe()
    {
        var (_, session) = Setup();
        session.ClearBookAndRefund();   // 空簿清场不应抛异常
        Assert.Equal(session.TotalShortable, session.ShortablePool);
    }

    // ══════════════════════════════════════
    // 市价意图语义(对手价转换后的行为)
    // ══════════════════════════════════════

    [Fact]
    public void Auction_MarketIntentStyle_BuySweepsIntoAuctionPrice()
    {
        // 模拟 EndCardTurn 的市价转换:买方按对手价(最优卖价)挂入
        var (engine, session) = Setup();
        var buyer = session.GetOrCreateAccount(Buyer, 1_000_000m);
        var seller = session.GetOrCreateAccount(Seller, 500_000m);
        seller.Position.Seed(new Quantity(1000), new Price(10m));

        // 卖方先挂限价卖单
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Seller, Side.Sell, OrderType.Limit, new Price(10.03m), new Quantity(100))));
        // 买方"市价"转换为对手价10.03限价
        decimal convPrice = engine.View.BestAsk!.Value.Value;
        Assert.Equal(10.03m, convPrice);
        Assert.True(session.TryRestForAuction(new OrderRequest(
            Buyer, Side.Buy, OrderType.Limit, new Price(convPrice), new Quantity(100))));

        var r = engine.CallAuction();
        Assert.True(r.HasValue);
        var trades = engine.SweepAtPrice(r!.Value.Price);
        session.SettleAuctionTrades(trades);

        Assert.Equal(100, buyer.Position.Total.Value);
        // 成交价=竞价价≤对手价,买方没有多付
        Assert.InRange(trades[0].Price.Value, 10.00m, 10.03m);
    }
}
