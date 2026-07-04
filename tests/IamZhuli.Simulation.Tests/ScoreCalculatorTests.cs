using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Levels;
using IamZhuli.Simulation.MarketData;

namespace IamZhuli.Simulation.Tests;

/// <summary>积分系统测试:收益率/回撤/风险调整/三方排名。</summary>
public class ScoreCalculatorTests
{
    private static Account Acc(decimal cash, int holding, decimal cost)
    {
        var a = new Account(new ParticipantId("X"), cash);
        if (holding > 0) a.Position.Seed(new Quantity(holding), new Price(cost));
        return a;
    }

    [Fact]
    public void ProfitableAccount_GetsPositiveScore()
    {
        var calc = new ScoreCalculator();
        var acc = Acc(120_000_000m, 0, 10m);   // 赚了20%
        var curve = new List<decimal> { 100_000_000m, 110_000_000m, 120_000_000m };
        var r = calc.Calculate(acc, curve, 100_000_000m, new Price(10m), 0);
        Assert.True(r.ReturnRate > 0.1m, $"应正收益率,实际{r.ReturnRate}");
        Assert.True(r.RiskAdjustedScore > 0, "正收益应正得分");
    }

    [Fact]
    public void LargeDrawdown_ReducesScore()
    {
        var calc = new ScoreCalculator();
        var acc = Acc(105_000_000m, 0, 10m);
        // 大回撤:1亿→5千万→1.05亿
        var curve = new List<decimal> { 100_000_000m, 50_000_000m, 105_000_000m };
        var r = calc.Calculate(acc, curve, 100_000_000m, new Price(10m), 0);
        Assert.True(r.MaxDrawdown > 0.4m, $"大回撤应>40%,实际{r.MaxDrawdown}");
        // 虽然最终盈利5%,但回撤大,得分应被压低
        Assert.True(r.RiskAdjustedScore < r.ReturnRate, "回撤应压低得分");
    }

    [Fact]
    public void HighRegulatorHeat_ReducesScore()
    {
        var calc = new ScoreCalculator();
        var acc = Acc(110_000_000m, 0, 10m);
        var curve = new List<decimal> { 100_000_000m, 110_000_000m };
        var lowHeat = calc.Calculate(acc, curve, 100_000_000m, new Price(10m), maxRegulatorHeat: 10);
        var highHeat = calc.Calculate(acc, curve, 100_000_000m, new Price(10m), maxRegulatorHeat: 90);
        Assert.True(highHeat.RiskAdjustedScore < lowHeat.RiskAdjustedScore,
            "高监管关注应降低得分");
    }

    [Fact]
    public void Rank_OrdersByScore()
    {
        var calc = new ScoreCalculator();
        var player = ("玩家", new ScoreResult(0.2m, 0.1m, 0.18m, 0, 0, ""));
        var ai = ("AI", new ScoreResult(0.1m, 0.05m, 0.095m, 0, 0, ""));
        var instB = ("机构B", new ScoreResult(0.3m, 0.2m, 0.21m, 0, 0, ""));
        var ranked = calc.Rank(player, ai, instB);
        Assert.Equal("机构B", ranked[0].Name);   // 0.21最高
        Assert.Equal("玩家", ranked[1].Name);    // 0.18
        Assert.Equal("AI", ranked[2].Name);      // 0.095
        Assert.Equal(1, ranked[0].Result.Rank);
        Assert.Equal(3, ranked[0].Result.TotalPlayers);
    }

    [Fact]
    public void MaxDrawdown_CalculatedCorrectly()
    {
        var curve = new List<decimal> { 100, 120, 80, 90, 110 };
        // 峰120→谷80,回撤=(120-80)/120=33.3%
        decimal dd = EquityCurveCollector.MaxDrawdown(curve);
        Assert.True(dd > 0.33m && dd < 0.34m, $"回撤应~33.3%,实际{dd}");
    }
}
