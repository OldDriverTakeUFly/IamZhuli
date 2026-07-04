using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Levels;
using IamZhuli.Simulation.Regulators;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 监管系统 + 关卡判定测试。
/// 验证:对倒/异常波动增加关注值、惩罚阶梯、衰减;目标判定与三星评分。
/// </summary>
public class RegulatorAndLevelTests
{
    private static readonly ParticipantId Player = new("Player");

    [Fact]
    public void Regulator_WashTrade_IncreasesHeat()
    {
        // 玩家自买自卖(对倒)应大幅增加关注值
        var reg = new Regulator(Player);
        var trade = new Trade(default, 0, new Price(10m), new Quantity(100), Side.Buy, Player, default, Player, default);
        reg.OnTrade(trade, isPlayerInvolved: true);
        Assert.True(reg.Heat >= reg.Config.WashTradeHeat, $"对倒应增加关注值,实际{reg.Heat}");
    }

    [Fact]
    public void Regulator_Volatility_IncreasesHeat()
    {
        var reg = new Regulator(Player);
        reg.OnTick(0.04m);   // 4% 波动 > 3% 阈值
        Assert.True(reg.Heat > 0, "异常波动应增加关注值");
    }

    [Fact]
    public void Regulator_HeatDecaysOverTime()
    {
        var reg = new Regulator(Player);
        // 制造一波关注值
        var trade = new Trade(default, 0, new Price(10m), new Quantity(100), Side.Buy, Player, default, Player, default);
        reg.OnTrade(trade, true);
        decimal heatAfter = reg.Heat;
        // 跑足够多 tick 让衰减生效(Config.DecayIntervalTicks=20)
        for (int i = 0; i < 100; i++) reg.OnTick(0);
        Assert.True(reg.Heat < heatAfter, $"关注值应衰减,初始{heatAfter}后续{reg.Heat}");
    }

    [Fact]
    public void Regulator_PenaltyEscalation()
    {
        var reg = new Regulator(Player) { Config = new RegulatorConfig { WashTradeHeat = 100m } };
        var trade = new Trade(default, 0, new Price(10m), new Quantity(100), Side.Buy, Player, default, Player, default);
        reg.OnTrade(trade, true);
        Assert.Equal(PenaltyLevel.ForcedLiquidation, reg.CurrentPenalty);
        Assert.True(reg.GetStatus().IsFailed);
    }

    [Fact]
    public void LevelJudge_ReachPrice_Achieved()
    {
        var level = LevelDefinition.Tutorial();
        var judge = new LevelJudge(level);
        var acc = new Account(Player, 100_000_000m);
        var progress = judge.EvaluateProgress(new Price(12.5m), acc, 200000, 30m);
        Assert.Contains(progress, p => p.Achieved && p.Description.Contains("12元"));
    }

    [Fact]
    public void LevelJudge_ReachPrice_NotAchieved()
    {
        var level = LevelDefinition.Tutorial();
        var judge = new LevelJudge(level);
        var acc = new Account(Player, 100_000_000m);
        var progress = judge.EvaluateProgress(new Price(11m), acc, 200000, 30m);
        Assert.DoesNotContain(progress, p => p.Achieved);
    }

    [Fact]
    public void LevelJudge_Settle_VictoryWithStars()
    {
        // 达成目标 + 盈利 + 低关注值 → 3星
        var level = LevelDefinition.Tutorial();
        var judge = new LevelJudge(level);
        var acc = new Account(Player, 100_000_000m);
        // 给玩家盈利(模拟)
        acc.Position.Seed(new Quantity(1000), new Price(10m));
        var result = judge.Settle(new Price(12.5m), acc, 200000, maxHeatReached: 20m,
            initialCash: 100_000_000m, failedByRegulator: false);
        Assert.True(result.IsVictory);
        Assert.True(result.Stars >= 1, $"应至少1星,实际{result.Stars}");
    }

    [Fact]
    public void LevelJudge_Settle_FailedByRegulator()
    {
        var level = LevelDefinition.Tutorial();
        var judge = new LevelJudge(level);
        var acc = new Account(Player, 100_000_000m);
        var result = judge.Settle(new Price(12.5m), acc, 200000, maxHeatReached: 100m,
            initialCash: 100_000_000m, failedByRegulator: true);
        Assert.False(result.IsVictory);
        Assert.Contains("强制平仓", result.FailureReason);
        Assert.Contains("激进", result.CoachComment);
    }

    [Fact]
    public void LevelJudge_AccumulateObjective_ChecksHeat()
    {
        // 吸筹目标:持仓15%且关注值<50
        var level = LevelDefinition.Accumulate();
        var judge = new LevelJudge(level);
        var acc = new Account(Player, 150_000_000m);
        acc.Position.Seed(new Quantity(30000), new Price(10m));   // 3万手/20万=15%
        // 关注值未超限 → 达成
        var progress = judge.EvaluateProgress(null, acc, 200000, maxHeatReached: 30m);
        Assert.Contains(progress, p => p.Achieved);
        // 关注值超限 → 未达成
        var progress2 = judge.EvaluateProgress(null, acc, 200000, maxHeatReached: 60m);
        Assert.DoesNotContain(progress2, p => p.Achieved);
    }
}
