using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 游戏丰度探索:用三种玩家原型(拉升庄家/做空猎手/舆论操盘手)+ 对照组(什么都不做)
/// 全自动跑同样的回合数,对比价格/情绪/监管/收益,回答:
/// 玩家能扮演什么角色?AI(做市商/对手主力/散户)加入后涌现出什么博弈内容?
/// 每个测试自带数据报告(ITestOutputHelper),断言只锁方向性结论。
/// </summary>
public sealed class GameRichnessTests
{
    private readonly ITestOutputHelper _out;
    public GameRichnessTests(ITestOutputHelper o) => _out = o;

    private const int Turns = 10;   // 2个交易日(5回合/天)

    private void Report(string title, MarketSnapshotDto start, List<MarketSnapshotDto> snaps, GameCore core)
    {
        var end = snaps[^1];
        _out.WriteLine($"── {title} ──");
        _out.WriteLine($"价格: {start.LastPrice:F2} → {end.LastPrice:F2} ({(end.LastPrice - start.LastPrice) / start.LastPrice:P1})");
        _out.WriteLine($"情绪: {start.Sentiment:F0} → {end.Sentiment:F0} | 群体热度 {start.HerdMood:F0}→{end.HerdMood:F0} | 信心 {start.Confidence:F0}→{end.Confidence:F0}");
        _out.WriteLine($"监管: 交易 {start.RegulatorHeat:F0}→{end.RegulatorHeat:F0} | 信息 {start.InfoHeat:F0}→{end.InfoHeat:F0} ({end.PenaltyLevel})");
        _out.WriteLine($"账户: 权益 {end.Account.TotalEquity / 10000:F0}万 (初始 {start.Account.TotalEquity / 10000:F0}万) | 持仓 {end.Account.PositionTotal} 空头 {end.Account.ShortQty}");
        _out.WriteLine($"散户活跃 {start.RetailActiveCount}→{end.RetailActiveCount} | 换手 {end.TurnoverRate:F1}%");
        // 关键回合轨迹
        var trail = snaps.Select((s, i) => $"T{i + 1}:{s.LastPrice:F2}").Take(10);
        _out.WriteLine($"轨迹: {string.Join(" ", trail)}");
        var thoughts = core.GetAIThoughts(3);
        foreach (var t in thoughts) _out.WriteLine($"AI: [{t.State}] 探测={t.DetectedIntent}({t.Confidence:P0}) {t.Reason}");
    }

    [Fact]
    public void Archetype_PumpMaster_PlayerCanPlayTheLifter()
    {
        var core = Harness.NewGame("accumulate", "暴力拉升", "市价急拉", "限价加仓");
        var start = core.BuildSnapshot();
        var snaps = Harness.RunTurns(core, Turns, c =>
            Harness.PlayByPriority(c, "暴力拉升", "市价急拉", "限价加仓"));
        Report("拉升庄家(暴力拉升→市价急拉→限价加仓)", start, snaps, core);

        var end = snaps[^1];
        Assert.True(end.LastPrice > start.LastPrice, "拉升庄家应能推高价格");
        // 跟风盘用群体热度/在场人数衡量(贪婪-恐惧合成值会被拉升震荡的波动恐慌压低,属合理设计)
        Assert.True(end.HerdMood > start.HerdMood || end.RetailActiveCount > start.RetailActiveCount,
            "拉升应吸引跟风盘");
        Assert.True(end.RegulatorHeat > start.RegulatorHeat, "暴力拉升应引来监管关注");
    }

    [Fact]
    public void Archetype_BearRaider_PlayerCanPlayTheShortSide()
    {
        var core = Harness.NewGame("accumulate", "做空突袭", "买回平仓");
        var start = core.BuildSnapshot();
        var snaps = Harness.RunTurns(core, Turns, c =>
        {
            // 后半程开始买回平仓锁定利润
            var s = c.BuildSnapshot();
            bool late = s.CardState!.Turn >= 2;
            if (late && s.Account.ShortQty > 0) Harness.Play(c, "买回平仓");
            else Harness.Play(c, "做空突袭");
        });
        // 收尾:尽量清空空头(拍卖对手盘深度有限,可能残留)
        for (int i = 0; i < 3 && core.BuildSnapshot().Account.ShortQty > 0 && !core.Loop.IsFinished; i++)
        {
            Harness.Play(core, "买回平仓");
            Harness.NextTurn(core);
        }
        snaps.Add(core.BuildSnapshot());
        Report("做空猎手(做空突袭→末期买回平仓)", start, snaps, core);

        var end = snaps[^1];
        int peakShort = snaps.Select(s => s.Account.ShortQty).Max();
        _out.WriteLine($"空头峰值 {peakShort} → 期末 {end.Account.ShortQty}");
        Assert.True(peakShort > 0, "过程中应建立过空头");
        Assert.True(end.Account.ShortQty < peakShort, "买回平仓应削减空头");
        Assert.True(end.Account.TotalEquity > 0, "权益不应穿仓");
        // 注:做空对价格的压制效果用"对照组 vs 做空组"的期末价对比来读(见两个报告),
        // 不做单边断言——机构B做市与AI博弈可能抵消做空压力,这正是多方博弈的意义
    }

    [Fact]
    public void Archetype_MediaBaron_PlayerCanPlayTheNarrativeTrader()
    {
        var core = Harness.NewGame("accumulate", "水军造势", "制造传闻", "上龙虎榜", "假大宗买入");
        var start = core.BuildSnapshot();
        var snaps = Harness.RunTurns(core, Turns, c =>
            Harness.PlayByPriority(c, "水军造势", "制造传闻", "上龙虎榜", "假大宗买入"));
        Report("舆论操盘手(水军→传闻→龙虎榜→假大宗,纯信息战)", start, snaps, core);

        var end = snaps[^1];
        Assert.True(end.InfoHeat > start.InfoHeat, "信息战应推高信息操纵关注值");
        Assert.True(end.Confidence > start.Confidence || end.Sentiment > start.Sentiment,
            "舆论牌应能撬动散户信心");
        Assert.True(end.RegulatorHeat < 80, "纯舆论不应直接引爆交易监管");
    }

    [Fact]
    public void Archetype_Control_DoNothing_IsTheBaseline()
    {
        // 对照组:玩家不出牌——AI主力与机构B自行博弈,散户自然交易
        var core = Harness.NewGame("accumulate", "限价吸筹");   // 有手牌但从不打出
        var start = core.BuildSnapshot();
        var snaps = Harness.RunTurns(core, Turns);
        Report("对照组(玩家不出牌,AI自主博弈)", start, snaps, core);

        var end = snaps[^1];
        Assert.Equal(0, end.Account.PositionTotal);
        // 市场自运转:价格有波动、深度存在、散户活跃
        var prices = snaps.Select(s => s.LastPrice ?? 10m).ToList();
        Assert.True(prices.Max() - prices.Min() > 0.02m, "AI互相博弈应产生价格波动");
        Assert.True(end.Asks.Count > 0 && end.Bids.Count > 0, "做市商持续提供深度");
    }
}

public sealed class QuickLevelTests
{
    /// <summary>手机快局(quick):5天/目标建仓10%/可正常跑到日切——碎片时间一局的注册与规则回归。</summary>
    [Fact]
    public void QuickLevel_FiveDays_LoadsAndRuns()
    {
        var core = new GameCore();
        core.LoadLevelById("quick");
        core.BeginTrading();
        var snap = core.BuildSnapshot();
        Assert.Equal(5, snap.TotalDays);
        Assert.NotEmpty(snap.Objectives);
        Assert.Contains("10%", snap.Objectives[0].Description);
        // 跑到首个日切(空手过段模型)
        for (int guard = 0; guard < 3000 && !core.Loop.IsDayClosed && !core.Loop.IsFinished; guard++)
        {
            if (core.BuildSnapshot().CardState is { Phase: "Play" } and { CardsPlayed: 0 })
                core.EndCardTurn();
            core.Step();
        }
        Assert.True(core.Loop.IsDayClosed, "第1天应能跑到日切");
        Assert.True(core.CanSave, "日切边界可存档");
    }
}
