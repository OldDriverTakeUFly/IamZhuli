using Xunit;
using IamZhuli.Simulation.Regulators;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 信息战监管深化测试:InfoHeat 专属惩罚阶梯(警示60/立案80,滞回触发)。
/// 警示=辟谣反弹+水军捣毁+罚款50万;立案=强辟谣+重罚500万+封口1天(禁发消息/信号)。
/// 回答:纯舆论流为什么有真实风险曲线。
/// </summary>
public sealed class InfoWarTests
{
    private readonly ITestOutputHelper _out;
    public InfoWarTests(ITestOutputHelper o) => _out = o;

    // ── 单元级:Regulator 阶梯本身 ──

    [Fact]
    public void Regulator_InfoEscalation_FiresOncePerLevel()
    {
        var reg = new Regulator(new IamZhuli.Core.ParticipantId("Player"));
        var fired = new List<PenaltyLevel>();
        reg.InfoPenaltyEscalated += lvl => fired.Add(lvl);

        // 假大宗+8/次:8次=64 → 警示;再2次=80 → 立案
        for (int i = 0; i < 10; i++) reg.OnSignalPublished("FakeBigBuy");
        Assert.Equal(new[] { PenaltyLevel.Warning, PenaltyLevel.Investigation }, fired);

        // 衰减回落后再加热跨入同级:不重复触发(滞回)
        for (int i = 0; i < 5; i++) reg.OnSignalPublished("FakeBigBuy");   // 100 封顶
        Assert.Equal(2, fired.Count);
    }

    // ── 集成级:GameCore 内的辟谣/捣毁/封口 ──

    [Fact]
    public void SustainedInfoWar_WarningDebunks_WaterArmyDismantled()
    {
        var core = Harness.NewGame("accumulate", "水军造势", "制造传闻", "假大宗买入");
        // 建立并启动水军(被捣毁的对象)
        Assert.True(core.UpgradeWaterArmy(out _));
        Assert.True(core.ToggleWaterArmy(out _));

        var start = core.BuildSnapshot();
        decimal cashBefore = start.Account.Cash;
        bool debunked = false;
        MarketSnapshotDto? peak = null;

        var snaps = Harness.RunTurns(core, 12, c =>
            Harness.PlayByPriority(c, "假大宗买入", "水军造势", "制造传闻"));
        decimal maxInfoHeat = 0;
        foreach (var s in snaps)
        {
            if (peak == null || s.Confidence > peak.Confidence) peak = s;
            maxInfoHeat = Math.Max(maxInfoHeat, s.InfoHeat);
            if (s.LatestRegulatorEvent.Contains("辟谣") || s.LatestRegulatorEvent.Contains("警示")) debunked = true;
        }
        var end = snaps[^1];
        _out.WriteLine($"信息热 峰值{maxInfoHeat} 期末{end.InfoHeat} | 水军运营 {start.WaterArmyActive}→{end.WaterArmyActive}");
        _out.WriteLine($"现金 {cashBefore / 10000:F0}万→{end.Account.Cash / 10000:F0}万 | 最新监管: {end.LatestRegulatorEvent}");

        Assert.True(maxInfoHeat >= 60 || debunked, "重信息牌组应把信息关注值推过警示线");
        // 辟谣落地的三重证据:水军被捣毁(只有升级回调会停它)+罚款真实扣款+事件流留痕
        Assert.False(end.WaterArmyActive, "水军网络应被监管曝光捣毁");
        Assert.True(end.Account.Cash < cashBefore - 500_000m, "罚款应真实扣款(≥警示档50万)");
        Assert.True(debunked || !end.WaterArmyActive, "辟谣动作应可观测");
    }

    [Fact]
    public void Investigation_GagsNewsAndSignal_UntilCooldown()
    {
        // 假大宗+8/张,单牌组保证每回合稳定出2张(回合2起能量4)
        var core = Harness.NewGame("accumulate", "假大宗买入");
        // 2费牌×2=4>每回合3能量:隔回合双出,8回合≈12次信号(96-衰减≈89,过立案线不触顶)
        var snaps = Harness.RunTurns(core, 8, c =>
        {
            Harness.Play(c, "假大宗买入");
            Harness.Play(c, "假大宗买入");
        });
        var end = snaps[^1];
        _out.WriteLine($"信息热 {end.InfoHeat} | 监管: {end.LatestRegulatorEvent}");

        Assert.True(end.InfoHeat >= 80, "双假大宗应触发立案");

        // 封口期:盘后发消息被拒
        bool ok = core.PublishNews("rumor", out var newsError);
        _out.WriteLine($"封口发消息: ok={ok} err={newsError}");
        Assert.False(ok);
        Assert.Contains("禁止", newsError);

        bool ok2 = core.PublishSignal("dragonlist", out var sigError);
        Assert.False(ok2);
        Assert.Contains("禁止", sigError);

        // 推进一整天以上(封口=150tick=5回合,NextTurn每次30tick)
        for (int t = 0; t < 7 && !core.Loop.IsFinished; t++)
            Harness.NextTurn(core);
        var after = core.BuildSnapshot();
        _out.WriteLine($"次日 监管: {after.LatestRegulatorEvent}");
        // 封口1天=level.TicksPerDay tick;跑了至少一天后应解除(可再发,或因其它前置被拒但非'禁止')
        bool retry = core.PublishSignal("dragonlist", out var retryError);
        _out.WriteLine($"次日发信号: ok={retry} err={retryError}");
        Assert.True(!retry || !retryError.Contains("禁止"), "封口应到期解除");
    }

    [Fact]
    public void Debunk_ReversesSentiment_Gains()
    {
        // 舆论吹起来的信心,辟谣时被证伪反转(信心显著回落)
        var core = Harness.NewGame("accumulate", "假大宗买入", "制造传闻");
        decimal peakConfidence = 0;
        MarketSnapshotDto? afterDebunk = null;

        var snaps = Harness.RunTurns(core, 7, c =>
            Harness.PlayByPriority(c, "假大宗买入", "制造传闻"));
        foreach (var s in snaps)
        {
            peakConfidence = Math.Max(peakConfidence, s.Confidence);
            if (s.LatestRegulatorEvent.Contains("辟谣") || s.LatestRegulatorEvent.Contains("警示"))
                afterDebunk ??= s;
        }
        _out.WriteLine($"信心峰值 {peakConfidence} → 辟谣后 {afterDebunk?.Confidence}");
        Assert.NotNull(afterDebunk);
        Assert.True(afterDebunk!.Confidence < peakConfidence - 5,
            $"辟谣应显著挫伤信心(峰值{peakConfidence}→{afterDebunk.Confidence})");
    }
}
