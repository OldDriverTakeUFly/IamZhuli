using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 机构B + 风险控制器测试。
/// 验证:四维风险评估、风险等级划分、做市深度随风险调整、高风险转操盘。
/// </summary>
public class InstitutionBTests
{
    private static Account NewAccount(decimal cash = 500_000_000m, int holding = 0, decimal cost = 10m)
    {
        var acc = new Account(new ParticipantId("B"), cash);
        if (holding > 0) acc.Position.Seed(new Quantity(holding), new Price(cost));
        return acc;
    }

    [Fact]
    public void RiskController_LowRisk_WhenPositionSmall()
    {
        // 持仓小、价格平稳、无冲击 → 低风险
        var rc = new MarketMakerRiskController(new Price(10m), maxPositionValue: 50_000_000m);
        var acc = NewAccount(holding: 1000);   // 1000手,市值小
        rc.OnTick(new Price(10.0m));
        var a = rc.Assess(acc);
        Assert.True(a.Score < 0.3, $"小持仓低风险,实际score={a.Score}");
        Assert.Equal(RiskLevel.Low, a.Level);
    }

    [Fact]
    public void RiskController_Rises_WhenPositionLarge()
    {
        // 持仓很大(接太多货)→ 持仓偏离度高 → 风险升高
        var rc = new MarketMakerRiskController(new Price(10m), maxPositionValue: 50_000_000m);
        var acc = NewAccount(holding: 60000);  // 6万手 × 10 × 100 = 6000万 > 5000万上限
        rc.OnTick(new Price(10.0m));
        var a = rc.Assess(acc);
        Assert.True(a.PositionExposure > 0.9m, $"大持仓应高偏离,实际{a.PositionExposure}");
        Assert.True(a.Score > 0.3, $"大持仓应升高风险,实际score={a.Score}");
    }

    [Fact]
    public void RiskController_Rises_WhenPriceFarFromFair()
    {
        // 价格远偏离公允价值 → 方向风险高
        var rc = new MarketMakerRiskController(new Price(10m));
        var acc = NewAccount(holding: 1000);
        rc.OnTick(new Price(11.5m));   // 偏离15%
        var a = rc.Assess(acc);
        Assert.True(a.DirectionRisk > 0.9m, $"大幅偏离应高方向风险,实际{a.DirectionRisk}");
    }

    [Fact]
    public void RiskController_Rises_OnHeavyImpact()
    {
        // 近期被持续大单吃 → 冲击风险高(察觉有人在操作)
        var rc = new MarketMakerRiskController(new Price(10m));
        var acc = NewAccount(holding: 1000);
        rc.OnTick(new Price(10m));
        // 模拟连续大单吃
        for (int i = 0; i < 20; i++) rc.OnTrade(new Quantity(600));
        var a = rc.Assess(acc);
        Assert.True(a.ImpactRisk > 0.9m, $"持续大单冲击应高冲击风险,实际{a.ImpactRisk}");
    }

    [Fact]
    public void DepthFactor_DecreasesWithRisk()
    {
        // 风险越高,做市深度系数越小
        Assert.Equal(1.0m, MarketMakerRiskController.DepthFactor(RiskLevel.Low));
        Assert.True(MarketMakerRiskController.DepthFactor(RiskLevel.High) < MarketMakerRiskController.DepthFactor(RiskLevel.Medium));
        Assert.True(MarketMakerRiskController.DepthFactor(RiskLevel.Critical) < 0.2m);
    }

    [Fact]
    public void SpreadFactor_IncreasesWithRisk()
    {
        // 风险越高,价差越大
        Assert.Equal(0.01m, MarketMakerRiskController.SpreadFactor(RiskLevel.Low));
        Assert.True(MarketMakerRiskController.SpreadFactor(RiskLevel.High) > MarketMakerRiskController.SpreadFactor(RiskLevel.Medium));
    }

    [Fact]
    public void InstitutionB_Initializes_AndMakesMarket()
    {
        // 机构B 初始化后,跑几个 tick 应在盘口挂出做市单
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(200000) };
        var engine = new MatchingEngine(rules);
        var session = new TradingSession(engine);
        var MM = new ParticipantId("seed");
        var mm = session.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10m));
        // 初始一笔成交确立价格
        session.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(1)));
        session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Market, Price.Zero, new Quantity(1)));

        var instB = new InstitutionB(session, new ParticipantId("机构B"), new Price(10m),
            cash: 500_000_000m, initialHolding: 20000, seed: 1);
        Assert.Equal(RiskLevel.Low, instB.CurrentRiskLevel);
        Assert.Equal(20000, instB.Account.Position.Total.Value);
    }
}
