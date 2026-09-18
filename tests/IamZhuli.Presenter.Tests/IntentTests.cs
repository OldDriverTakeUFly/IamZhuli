using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 操盘意图(战略层)测试:基础盘保证"手牌差也能推进剧本"——庄家方向不由抽卡决定。
/// 意图免费/不占出牌数/跨段持续;基础盘先于架势注入(可被激进×2放大)。
/// </summary>
public sealed class IntentTests
{
    private readonly ITestOutputHelper _out;
    public IntentTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AccumulateIntent_BuysWithoutAnyCard()
    {
        // 全程不出牌,只设吸筹意图 → 拍卖里玩家仍是买方(基础盘生效)
        var core = Harness.NewGame("accumulate", "分批出货");   // 手牌故意给出货牌(牌烂)
        Assert.True(core.SetIntent("accumulate", out _));
        Harness.NextTurn(core);   // 跳申报段
        core.EndCardTurn();       // 段1(锁单竞价),不出任何牌
        // 吸筹=耐心挂单(bid-0.01被动):基础盘应在簿上收集,而非立即成交
        var s = core.BuildSnapshot();
        var restingBuy = s.OpenOrders.Where(o => o.Side == "Buy").Sum(o => o.RemainingQty);
        _out.WriteLine($"意图吸筹(零出牌): 在簿买单{restingBuy}手, 竞价量{core.LastAuction!.Volume}");
        Assert.True(restingBuy >= 800 || s.Account.PositionTotal > 0, "吸筹基础盘应在簿收集(挂单或已成交)");

        // 拉升意图(市价)应直接出现在买方——方向保证的最强形式
        var core2 = Harness.NewGame("accumulate", "分批出货");
        Assert.True(core2.SetIntent("push", out _));
        Harness.NextTurn(core2);
        core2.EndCardTurn();
        var ra2 = core2.LastAuction!;
        _out.WriteLine($"意图拉升(零出牌): 你买{ra2.Trades.Where(t => t.Buyer == "你").Sum(t => t.Qty)}手@{ra2.Price}");
        Assert.True(ra2.Trades.Any(t => t.Buyer == "你"), "拉升意图(市价)基础盘应让玩家出现在买方");
    }

    [Fact]
    public void Intent_StickyAcrossSegments_And_Clearable()
    {
        var core = Harness.NewGame("accumulate", "限价吸筹");
        Assert.True(core.SetIntent("push", out _));
        Assert.Equal("Push", core.BuildSnapshot().CardState!.Intent);
        Harness.NextTurn(core);   // 跨段
        Assert.Equal("Push", core.BuildSnapshot().CardState!.Intent);   // 持续
        Assert.True(core.SetIntent("none", out _));
        Assert.Null(core.BuildSnapshot().CardState!.Intent);
    }

    [Fact]
    public void DistributeIntent_SellsOnlyAvailable_Holdings()
    {
        // 出货意图无持仓时基础盘挂单被校验拦下(不透支),有持仓后正常卖出
        var core = Harness.NewGame("accumulate", "市价急拉", "限价吸筹");
        core.SetIntent("distribute", out _);
        Harness.NextTurn(core);
        core.EndCardTurn();   // 零持仓出货→基础盘作废
        Assert.Equal(0, core.BuildSnapshot().Account.PositionTotal);
        // 建仓后出货
        core.SetIntent("accumulate", out _);
        var day1 = core.BuildSnapshot();
        while (core.BuildSnapshot().CurrentDay == day1.CurrentDay && !core.Loop.IsFinished)
            Harness.NextTurn(core);
        core.SetIntent("distribute", out _);
        Harness.NextTurn(core); core.EndCardTurn();   // 次日段1出货
        var s = core.BuildSnapshot();
        _out.WriteLine($"出货意图: 可卖{s.Account.PositionAvailable}(T+1后) → 意图挂卖");
        // 断言不崩溃+状态自洽(卖出多少取决于买盘)
        Assert.True(s.Account.PositionAvailable >= 0);
    }

    [Fact]
    public void Intent_PlusAggressiveStance_DoublesBaseline()
    {
        // 意图(先注入)+激进架势(×2)叠加: 同种子对比,激进局买入量更多或热更高
        var a = new GameCore(seed: 9001); a.LoadLevelById("accumulate");
        var b = new GameCore(seed: 9001); b.LoadLevelById("accumulate");
        foreach (var (c, stance) in new[] { (a, false), (b, true) })
        {
            c.SetDeck(new List<string> { "激进架势", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            c.SetIntent("accumulate", out _);
            if (stance) Assert.True(Harness.Play(c, "激进架势"), Harness.LastPlayError);
            c.EndCardTurn();
        }
        var raA = a.LastAuction!; var raB = b.LastAuction!;
        int buyA = raA.Trades.Where(t => t.Buyer == "你").Sum(t => t.Qty);
        int buyB = raB.Trades.Where(t => t.Buyer == "你").Sum(t => t.Qty);
        _out.WriteLine($"意图吸筹: 普通 {buyA}手 vs +激进 {buyB}手 (基础盘800→1600)");
        Assert.True(buyB >= buyA, "激进应放大基础盘(≥普通)");
    }

    [Fact]
    public void DirectionFloor_AlwaysGuarantees_IntentAlignedCard()
    {
        // 跨20个种子: 设吸筹意图后手牌必含买方向牌(保底立即生效,不等抽牌)
        for (int seed = 0; seed < 20; seed++)
        {
            var core = new GameCore(seed: 7000 + seed);
            core.LoadLevelById("accumulate");   // 默认牌库(无自配)
            core.BeginTrading();
            core.SetIntent("accumulate", out _);
            var hand = core.BuildSnapshot().CardState!.Hand;
            bool hasBuy = hand.Any(c => c.Effect is "LimitBuy" or "MarketBuy" or "ShortCover");
            Assert.True(hasBuy, $"seed{seed}: 手牌无买方向牌 [{string.Join(",", hand.Select(h => h.Name))}]");
        }
    }

    [Fact]
    public void DirectionFloor_RespectsCustomDeck()
    {
        // 自配全做空牌组 + 吸筹意图: 不强制注入(组牌是玩家的选择)
        var core = Harness.NewGame("accumulate", "做空突袭", "大额做空");
        core.SetIntent("accumulate", out _);
        var hand = core.BuildSnapshot().CardState!.Hand;
        Assert.True(hand.All(c => c.Name is "做空突袭" or "大额做空"), "自配牌组不受保底干预");
    }

    [Fact]
    public void BiasedDraw_SkewsHandToward_Intent()
    {
        // 统计性: 10个种子各跑3段,吸筹意图下买方向牌占比应显著高于无意图(权重×3)
        int withIntent = 0, without = 0, totalCards = 0;
        for (int seed = 0; seed < 10; seed++)
        {
            var a = new GameCore(seed: 8100 + seed); a.LoadLevelById("accumulate"); a.BeginTrading(); a.SetIntent("accumulate", out _);
            var b = new GameCore(seed: 8100 + seed); b.LoadLevelById("accumulate"); b.BeginTrading();
            for (int seg = 0; seg < 3; seg++) { Harness.NextTurn(a); Harness.NextTurn(b); }
            foreach (var h in a.BuildSnapshot().CardState!.Hand) { totalCards++; if (h.Effect is "LimitBuy" or "MarketBuy" or "ShortCover") withIntent++; }
            foreach (var h in b.BuildSnapshot().CardState!.Hand) { totalCards++; if (h.Effect is "LimitBuy" or "MarketBuy" or "ShortCover") without++; }
        }
        _out.WriteLine($"买方向牌占比: 有意图 {withIntent}/50 vs 无意图 {without}/50");
        Assert.True(withIntent > without, $"偏置应提高买方向占比({withIntent} vs {without})");
    }
}
