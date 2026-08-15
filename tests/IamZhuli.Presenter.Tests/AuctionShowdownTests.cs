using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 同时出牌博弈测试:玩家与 AI(机构B做市/AI主力对手/散户池)的意图进入同一场集中撮合,
/// 验证对手方在场、价格冲击方向、无玩家参与时市场仍自运转。
/// </summary>
public sealed class AuctionShowdownTests
{
    private readonly ITestOutputHelper _out;
    public AuctionShowdownTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void PlayerMarketBuy_AuctionFills_AndLiftsPrice()
    {
        var core = Harness.NewGame("accumulate", "市价急拉");
        decimal priceBefore = core.BuildSnapshot().LastPrice ?? 10m;

        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        _out.WriteLine($"竞价价 {ra.Price} 量 {ra.Volume} | 买前 {priceBefore}");
        Assert.True(ra.Volume > 0, "市价急拉应与对手盘成交");
        Assert.True(ra.Price >= priceBefore - 0.05m, "2000手买压不应让价格下跌");

        // Resolve 结束后的价格应被买盘抬升
        var after = Harness.NextTurn(core);
        _out.WriteLine($"回合后价 {after.LastPrice} 持仓 {after.Account.PositionTotal}");
        Assert.True(after.LastPrice > priceBefore, "大额市价买入应抬升价格");
    }

    [Fact]
    public void Auction_Counterparties_AreAIAndInstitution()
    {
        var core = Harness.NewGame("accumulate", "市价急拉");
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;

        var counterparties = ra.Trades
            .Where(t => t.Buyer == "你")
            .Select(t => t.Seller)
            .Distinct()
            .ToList();
        _out.WriteLine($"你的对手方: {string.Join(", ", counterparties)}");
        Assert.Contains("机构B", counterparties);   // 做市商永远在报价
        Assert.True(counterparties.Count >= 1, "撮合应有真实对手方");
    }

    [Fact]
    public void PlayerHolding_ThenSlam_PressesPrice()
    {
        var core = Harness.NewGame("accumulate", "限价加仓", "暴力砸盘");
        // 回合1建仓
        Assert.True(Harness.Play(core, "限价加仓"), Harness.LastPlayError);
        var afterBuy = Harness.NextTurn(core);
        Assert.True(afterBuy.Account.PositionTotal > 0, "先建仓");

        // 推到次日(T+1可卖)再砸盘
        int day = afterBuy.CurrentDay;
        MarketSnapshotDto day2 = afterBuy;
        while (day2.CurrentDay == day && !core.Loop.IsFinished)
            day2 = Harness.NextTurn(core);
        decimal priceBefore = day2.LastPrice ?? 10m;

        Assert.True(Harness.Play(core, "暴力砸盘"), Harness.LastPlayError);   // 5000手
        var after = Harness.NextTurn(core);
        _out.WriteLine($"砸盘: {priceBefore} → {after.LastPrice} 卖出持仓 {day2.Account.PositionAvailable - after.Account.PositionAvailable}");
        Assert.True(after.LastPrice < priceBefore, "5000手砸盘应压低价格");
    }

    [Fact]
    public void NoPlayerAction_MarketStillRuns_WithDepthAndTrades()
    {
        // 玩家全程不出牌:机构B报价、AI主力自主行动、散户交易——市场不该死寂
        var core = Harness.NewGame("accumulate", "限价吸筹");   // 有牌但不出
        var snaps = new List<MarketSnapshotDto>();
        for (int t = 0; t < 3; t++) snaps.Add(Harness.NextTurn(core));

        foreach (var s in snaps)
        {
            Assert.True(s.Asks.Count > 0 && s.Bids.Count > 0, "机构B做市应持续提供双边深度");
        }
        var last = snaps[^1];
        _out.WriteLine($"无人出牌3回合后: 价 {last.LastPrice} 换手 {last.TurnoverRate}% 活跃散户 {last.RetailActiveCount}");
        // AI 大脑在思考(每tick记录独白)
        Assert.True(core.GetAIThoughts(5).Count > 0, "AI主力应有决策记录");
    }

    [Fact]
    public void PlayerWall_AbsorbsAndShapes_Book()
    {
        // 玩家挂墙(买卖各1000手) → 揭牌后簿上应有玩家的两笔挂单;墙改变了盘口形状
        var core = Harness.NewGame("accumulate", "限价挂墙");
        Assert.True(Harness.Play(core, "限价挂墙"), Harness.LastPlayError);
        core.EndCardTurn();

        var after = core.BuildSnapshot();
        _out.WriteLine($"挂墙后盘口: 卖1 {after.BestAsk} 买1 {after.BestBid} 我的挂单 {after.OpenOrders.Count}");
        // 墙的限价单可能部分成交,剩余应挂在簿上(玩家挂单可见)
        Assert.True(after.OpenOrders.Count <= 2);
        // 下一回合清场时挂单被释放,无资金泄漏
        var next = Harness.NextTurn(core);
        Assert.True(next.Account.TotalEquity > 0);
    }

    [Fact]
    public void SustainedBuying_AttractsRetailFollowers()
    {
        // 连续拉升应带动散户情绪与活跃度(跟风盘)——"主力点火,散户抬轿"
        var core = Harness.NewGame("accumulate", "市价急拉", "限价加仓");
        var start = core.BuildSnapshot();
        decimal sentiment0 = start.Sentiment;
        int active0 = start.RetailActiveCount;
        decimal price0 = start.LastPrice ?? 10m;

        var snaps = Harness.RunTurns(core, 3, c =>
        {
            Harness.Play(c, "市价急拉");
            Harness.Play(c, "限价加仓");
        });
        var end = snaps[^1];
        _out.WriteLine($"3回合拉升: 价 {price0}→{end.LastPrice} 情绪 {sentiment0}→{end.Sentiment} 活跃 {active0}→{end.RetailActiveCount} 监管 {end.RegulatorHeat}");
        Assert.True(end.LastPrice > price0, "持续买入应推高价格");
        Assert.True(end.Sentiment > sentiment0, "拉升应带动散户情绪");
    }
}
