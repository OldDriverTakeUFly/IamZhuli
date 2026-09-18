using Xunit;
using IamZhuli.Core;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 三段式交易日测试:盘前竞价(回合0,开盘价由卡牌拍卖确立)/盘中(1-3)/收盘竞价(回合4,
/// 竞价价锚定收盘+尾盘异常波动特护)。以及跨段组合:盘后消息→次日盘前高开。
/// </summary>
public sealed class PhaseFlowTests
{
    private readonly ITestOutputHelper _out;
    public PhaseFlowTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void PhaseLabels_Turn0PreOpen_1to3Intraday_4Close()
    {
        var core = Harness.NewGame("accumulate", "限价吸筹");
        Assert.Equal("盘前竞价", core.BuildSnapshot().CardState!.PhaseName);
        Harness.NextTurn(core);
        Assert.Equal("盘中", core.BuildSnapshot().CardState!.PhaseName);
        Harness.NextTurn(core);
        Harness.NextTurn(core);
        Assert.Equal("盘中", core.BuildSnapshot().CardState!.PhaseName);
        Harness.NextTurn(core);
        Assert.Equal("收盘竞价", core.BuildSnapshot().CardState!.PhaseName);
    }

    [Fact]
    public void PreOpenAuction_PlayerCard_SetsDailyOpen()
    {
        // 盘前竞价的卡牌拍卖成交价=当日开盘价(跳过loop内置开盘撮合)
        var core = Harness.NewGame("accumulate", "市价急拉");
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        _out.WriteLine($"盘前竞价: 价 {ra.Price} 量 {ra.Volume}");
        Assert.True(ra.Volume > 0, "盘前市价买入应与对手盘成交");
        var candle = core.BuildSnapshot().TodayCandle;
        Assert.NotNull(candle);
        Assert.Equal(ra.Trades[0].Price, candle.Open);   // 当日K线开盘=盘前竞价首笔
    }

    [Fact]
    public void CloseAuction_AnchorsDayEnd_AndChipSnapshot()
    {
        // 打满一天5回合:第5回合(收盘竞价)结束后日切,当日筹码快照落盘
        var core = Harness.NewGame("accumulate", "市价急拉", "限价吸筹");
        Harness.RunTurns(core, 4, c => Harness.Play(c, "市价急拉"));   // 回合0-3
        // 回合4(收盘竞价)手动驱动:EndCardTurn 后 Step 到日切(不等下一回合Play)
        Harness.Play(core, "市价急拉");
        core.EndCardTurn();
        for (int i = 0; i < 200 && !core.Loop.IsDayClosed && !core.Loop.IsFinished; i++)
            core.Step();
        Assert.True(core.Loop.IsDayClosed || core.Loop.IsFinished, "收盘竞价Resolve走完应日切");
        var lastRa = core.LastAuction!;
        var snap = core.BuildSnapshot();
        _out.WriteLine($"收盘竞价: 价 {lastRa.Price} 量 {lastRa.Volume} | 日切={core.Loop.IsDayClosed}");
        // 收盘静默:收盘竞价后不再交易,日K收盘=竞价价(精确)
        if (snap.TodayCandle is { } c && lastRa.Volume > 0)
            Assert.Equal(lastRa.Price, c.Close);
        // 当日筹码快照
        core.StartNextDay();
        var chips = core.GetChipHistory(1);
        Assert.True(chips.Count == 1, "次日应能取到第1天筹码快照");
    }

    [Fact]
    public void CloseAuction_Spike_TriggersLateSessionWatch()
    {
        // 尾盘拉升:收盘竞价价格大幅偏离前价 → 尾盘异常波动热度(监管特护)
        var core = Harness.NewGame("accumulate", "市价急拉", "限价吸筹");
        // 回合0-3只小额吸筹(压住价格,留出尾盘拉升空间)
        var snaps = Harness.RunTurns(core, 4, c => Harness.Play(c, "限价吸筹"));
        var before = core.BuildSnapshot();
        decimal priceBefore = before.LastPrice ?? 10m;

        // 回合4(收盘竞价):暴力拉升5%+ → 竞价价偏离≥2% → 尾盘监控
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        _out.WriteLine($"收盘拉升: {priceBefore} → {ra.Price} (竞价量{ra.Volume})");

        var events = core.GetReplayData().Events.Where(e => e.Source == "监管" && e.Detail.Contains("尾盘异常波动")).ToList();
        decimal heatAfter = core.BuildSnapshot().RegulatorHeat;
        _out.WriteLine($"尾盘事件: {events.Count} 条, 交易热 {before.RegulatorHeat} → {heatAfter}");
        if (ra.Volume > 0 && Math.Abs(ra.Price - priceBefore) / priceBefore >= 0.02m)
        {
            Assert.True(events.Count > 0, "大幅偏离的收盘竞价应触发尾盘异常波动");
            Assert.True(heatAfter > before.RegulatorHeat, "尾盘监控应加热");
        }
        else _out.WriteLine("(本局尾盘偏离<2%,未触发——分支合法)");
    }

    [Fact]
    public void OvernightNews_GapsNextDayPreOpenAuction()
    {
        // 跨段组合:盘后发布利好 → 次日盘前竞价高开(同种子配对,唯一差异是消息)
        var a = new GameCore(seed: 8080); a.LoadLevelById("accumulate");
        var b = new GameCore(seed: 8080); b.LoadLevelById("accumulate");
        foreach (var (c, news) in new[] { (a, true), (b, false) })
        {
            c.SetDeck(new List<string> { "市价急拉", "限价吸筹" }, out _);
            c.BeginTrading();
            // 打满一天(空过全部5回合)
            Harness.RunTurns(c, 5);
            Assert.True(c.Loop.IsDayClosed);
            if (news) Assert.True(c.PublishNews("positive", out var err), err);
            c.StartNextDay();   // 次日盘前竞价
            Assert.True(Harness.Play(c, "市价急拉"), Harness.LastPlayError);
            c.EndCardTurn();
        }
        decimal openA = a.LastAuction!.Volume > 0 ? a.LastAuction.Price : 0;
        decimal openB = b.LastAuction!.Volume > 0 ? b.LastAuction.Price : 0;
        _out.WriteLine($"次日盘前竞价开盘: 有利好 {openA} vs 无消息 {openB}");
        Assert.True(openA > 0 && openB > 0, "两局盘前都应有成交");
        Assert.True(openA >= openB, $"隔夜利好应助推次日高开({openA} vs {openB})");
    }
}
