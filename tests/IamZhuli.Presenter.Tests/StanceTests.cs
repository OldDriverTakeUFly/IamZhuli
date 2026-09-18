using IamZhuli.Engine;
using IamZhuli.Core;
using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 架势牌变体测试:同种子配对对比(全局确定性保证下,唯一的变量是架势)。
/// 验证:不占出牌数/可替换/回合清空;激进加势、隐蔽降温、欺诈情绪反转、防御不追价。
/// </summary>
public sealed class StanceTests
{
    private readonly ITestOutputHelper _out;
    public StanceTests(ITestOutputHelper o) => _out = o;

    /// <summary>同种子开局(市场完全相同),返回 core。</summary>
    private static GameCore PairedGame() => new(seed: 4242);

    [Fact]
    public void Stance_DoesNotCountTowardCardLimit_AndReplaceable()
    {
        var core = new GameCore(seed: 77);
        core.LoadLevelById("accumulate");
        core.SetDeck(new List<string> { "激进架势", "隐蔽架势", "限价吸筹", "限价加仓" }, out _);   // 4张牌组=手牌确定
        core.BeginTrading();
        // 攒能量:架势(1)+双操盘(1+2) = 4
        Harness.NextTurn(core);
        Assert.True(Harness.Play(core, "激进架势"), Harness.LastPlayError);
        Assert.True(Harness.Play(core, "限价吸筹"), Harness.LastPlayError);
        Assert.True(Harness.Play(core, "限价加仓"), Harness.LastPlayError);   // 2张操盘满额
        var snap = core.BuildSnapshot();
        Assert.Equal(2, snap.CardState!.CardsPlayed);
        // 架势仍可替换(不占限额)
        Assert.True(Harness.Play(core, "隐蔽架势"), Harness.LastPlayError);
        Assert.Equal("StanceStealthy", core.BuildSnapshot().CardState!.ActiveStance);
        // 第3张操盘被拦
        Assert.False(Harness.Play(core, "限价吸筹"));
    }

    [Fact]
    public void Stance_ClearedNextTurn()
    {
        var core = Harness.NewGame("accumulate", "激进架势", "限价吸筹");
        Assert.True(Harness.Play(core, "激进架势"));
        Assert.Equal("StanceAggressive", core.BuildSnapshot().CardState!.ActiveStance);
        Harness.NextTurn(core);
        Assert.Null(core.BuildSnapshot().CardState!.ActiveStance);
    }

    [Fact]
    public void Stance_DoesNotBreakCombo_Sequence()
    {
        // 挂墙 → 激进架势 → 急拉:架势不打断诱多陷阱序列
        var core = Harness.NewGame("accumulate", "限价挂墙", "市价急拉", "激进架势");
        Harness.NextTurn(core);   // 能量6: 挂墙1+架势1+急拉3
        Assert.True(Harness.Play(core, "限价挂墙"));
        Assert.True(Harness.Play(core, "激进架势"));
        Assert.True(Harness.Play(core, "市价急拉"));
        core.EndCardTurn();
        Assert.True(core.LastAuction!.Volume > 0, "诱多陷阱连招下的急拉应有成交");
    }

    [Fact]
    public void Aggressive_AddsHeat_VsBaseline()
    {
        // 同种子双局:唯一差异是激进架势 → 交易热更高
        var base_ = PairedGame();
        base_.LoadLevelById("accumulate");
        var aggr = PairedGame();
        aggr.LoadLevelById("accumulate");

        foreach (var (c, useStance) in new[] { (base_, false), (aggr, true) })
        {
            c.SetDeck(new List<string> { "激进架势", "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            if (useStance) Assert.True(Harness.Play(c, "激进架势"));
            Assert.True(Harness.Play(c, "市价急拉"));
            c.EndCardTurn();
            Harness.NextTurn(c);
        }
        decimal hBase = base_.BuildSnapshot().RegulatorHeat;
        decimal hAggr = aggr.BuildSnapshot().RegulatorHeat;
        _out.WriteLine($"交易热: 基线 {hBase} vs 激进 {hAggr}");
        Assert.True(hAggr > hBase, "激进架势应推高交易监管(量×2触发大单/波动检测+显式+5)");
    }

    [Fact]
    public void Stealthy_ReducesHeat_VsBaseline()
    {
        var base_ = PairedGame(); base_.LoadLevelById("accumulate");
        var stealth = PairedGame(); stealth.LoadLevelById("accumulate");
        foreach (var (c, useStance) in new[] { (base_, false), (stealth, true) })
        {
            c.SetDeck(new List<string> { "隐蔽架势", "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            // 连续3回合买入:基线积累大单/波动热度,隐蔽(量×6折,单笔低于大单阈值)应显著更低
            for (int turn = 0; turn < 3 && !c.Loop.IsFinished; turn++)
            {
                if (useStance) Harness.Play(c, "隐蔽架势");
                Assert.True(Harness.Play(c, "市价急拉"), Harness.LastPlayError);
                c.EndCardTurn();
                Harness.NextTurn(c);
            }
        }
        decimal hBase = base_.BuildSnapshot().RegulatorHeat;
        decimal hSt = stealth.BuildSnapshot().RegulatorHeat;
        _out.WriteLine($"交易热: 基线 {hBase} vs 隐蔽 {hSt}");
        Assert.True(hSt < hBase, "隐蔽架势应降低交易监管(量×6折+显式-4)");
    }

    [Fact]
    public void Deceptive_FlipsSentiment_OnBuy()
    {
        // 买入+欺诈 → 装跌:信心应低于普通买入(散户被误导抛售)
        var base_ = PairedGame(); base_.LoadLevelById("accumulate");
        var dec = PairedGame(); dec.LoadLevelById("accumulate");
        foreach (var (c, useStance) in new[] { (base_, false), (dec, true) })
        {
            c.SetDeck(new List<string> { "欺诈架势", "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            if (useStance) Assert.True(Harness.Play(c, "欺诈架势"));
            Assert.True(Harness.Play(c, "市价急拉"));
            c.EndCardTurn();
            Harness.NextTurn(c);
        }
        decimal cBase = base_.BuildSnapshot().Confidence;
        decimal cDec = dec.BuildSnapshot().Confidence;
        decimal iBase = base_.BuildSnapshot().InfoHeat;
        decimal iDec = dec.BuildSnapshot().InfoHeat;
        _out.WriteLine($"信心: 普通 {cBase} vs 欺诈 {cDec} | 信息热: {iBase} vs {iDec}");
        Assert.True(cDec < cBase, "欺诈买入应压低散户信心(反向信号)");
        Assert.True(iDec > iBase, "欺诈架势应推高信息监管");
    }

    [Fact]
    public void Defensive_DoesNotChasePrice_VsBaseline()
    {
        // 市价急拉+防御 → 只吃对手价一档:竞价价不应高于普通市价
        var base_ = PairedGame(); base_.LoadLevelById("accumulate");
        var def = PairedGame(); def.LoadLevelById("accumulate");
        decimal priceBase = 0, priceDef = 0;
        foreach (var (c, useStance) in new[] { (base_, false), (def, true) })
        {
            c.SetDeck(new List<string> { "防御架势", "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            if (useStance) Assert.True(Harness.Play(c, "防御架势"));
            Assert.True(Harness.Play(c, "市价急拉"));
            c.EndCardTurn();
        }
        priceBase = base_.LastAuction!.Volume > 0 ? base_.LastAuction.Price : (base_.BuildSnapshot().LastPrice ?? 0);
        priceDef = def.LastAuction!.Volume > 0 ? def.LastAuction.Price : (def.BuildSnapshot().LastPrice ?? 0);
        _out.WriteLine($"竞价价: 普通 {priceBase} vs 防御 {priceDef}");
        Assert.True(priceDef <= priceBase + 0.001m, "防御架势不应追价(≤普通市价的竞价价)");
    }

    [Fact]
    public void Deceptive_DecoyWall_AnchorsAuctionPrice_Down_ForBuys()
    {
        // 买入+欺诈:下方3000手假买墙参与竞价定价 → 竞价价被拉低(同种子配对,唯一差异是架势)
        var base_ = PairedGame(); base_.LoadLevelById("accumulate");
        var dec = PairedGame(); dec.LoadLevelById("accumulate");
        foreach (var (c, useStance) in new[] { (base_, false), (dec, true) })
        {
            c.SetDeck(new List<string> { "欺诈架势", "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            Harness.NextTurn(c);
            if (useStance) Assert.True(Harness.Play(c, "欺诈架势"));
            Assert.True(Harness.Play(c, "市价急拉"));
            c.EndCardTurn();
        }
        var raB = base_.LastAuction!;
        var raD = dec.LastAuction!;
        _out.WriteLine($"竞价价: 普通 {raB.Price} vs 欺诈 {raD.Price} | 量 {raB.Volume} vs {raD.Volume}");
        Assert.True(raD.Volume > 0, "欺诈对局应有成交");
        Assert.True(raD.Price <= raB.Price, $"假买墙应拉低竞价价({raD.Price} vs {raB.Price})");
        // 撤墙热度:欺诈局交易热更高(假墙+4,其他路径同种子一致)
        Harness.NextTurn(base_);
        Harness.NextTurn(dec);
        decimal hB = base_.BuildSnapshot().RegulatorHeat;
        decimal hD = dec.BuildSnapshot().RegulatorHeat;
        _out.WriteLine($"交易热: 普通 {hB} vs 欺诈 {hD}");
        Assert.True(hD > hB, "撤墙应推高交易监管(虚假挂单)");
    }

    [Fact]
    public void Deceptive_DecoyWall_NeverExecutes()
    {
        // 假墙撮合前撤除:不会成交(权益变化只来自真实意图),不会残留挂单
        var core = new GameCore(seed: 5150);
        core.LoadLevelById("accumulate");
        core.SetDeck(new List<string> { "欺诈架势", "市价急拉", "限价吸筹" }, out _);
        core.BeginTrading();
        Harness.NextTurn(core);
        Assert.True(Harness.Play(core, "欺诈架势"));
        Assert.True(Harness.Play(core, "市价急拉"));
        core.EndCardTurn();

        var ra = core.LastAuction!;
        // 假墙价格(现价-0.05附近)不应出现在玩家的成交对手记录里以墙价成交的异常巨量:
        // 假墙若被扫到,单笔成交量会≥3000手墙量级
        Assert.True(ra.Trades.All(x => x.Qty < 3000), "假墙永不成交(撮合前已撤)");
        // 撤墙后无残留:玩家挂单里不应有假墙价位的大单
        var snap = core.BuildSnapshot();
        Assert.True(snap.OpenOrders.All(o => o.TotalQty < 3000), "假墙不应残留为挂单");
    }

    [Fact]
    public void DeceptiveWall_AIRecognition_Escalates_WallDegradesThenIgnored()
    {
        var core = new GameCore(seed: 6161);
        core.LoadLevelById("accumulate");
        core.SetDeck(new List<string> { "欺诈架势", "市价急拉", "限价吸筹" }, out _);
        core.BeginTrading();
        Harness.NextTurn(core);   // 攒能量(回合2起 1+3=4)

        var auctionPrices = new List<decimal>();
        bool frontRan = false;
        for (int turn = 0; turn < 3 && !core.Loop.IsFinished; turn++)
        {
            Assert.True(Harness.Play(core, "欺诈架势"), Harness.LastPlayError);
            Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
            core.EndCardTurn();
            var ra = core.LastAuction!;
            auctionPrices.Add(ra.Volume > 0 ? ra.Price : 0);
            // 识破后抢跑:AI在玩家买入方向抢先下买(拍卖里AI主力应作为买方出现)
            if (turn >= 2 && ra.Trades.Any(x => x.Buyer == "AI主力" && x.Qty > 0))
                frontRan = true;
            Harness.NextTurn(core);
        }

        // 识别升级:2次撤墙被识破(第3次墙上不了盘面→计数停在2)
        Assert.Equal(2, core.AI!.FakeWallSeen);
        Assert.Equal(Side.Buy, core.AI.LastFakeWallSide);
        // 事件流:恰好2次撤墙(全额3000 + 减半1500),第3回合无墙可撤
        var cancels = core.GetReplayData().Events
            .Where(e => e.Source == "监管" && e.Detail.Contains("盘口伪装撤单")).ToList();
        _out.WriteLine($"撤墙事件: {cancels.Count} 次 → {string.Join(" | ", cancels.Select(c => c.Detail))}");
        _out.WriteLine($"竞价价轨迹: {string.Join(" → ", auctionPrices)} | 抢跑出现={frontRan}");
        Assert.Equal(2, cancels.Count);
        Assert.Contains("3000", cancels[0].Detail);   // 第1次全额墙
        Assert.Contains("1500", cancels[1].Detail);   // 第2次减半墙
        Assert.True(frontRan, "识破后 AI 应反手抢跑(在玩家买入方向抢先买入)");
    }
}
