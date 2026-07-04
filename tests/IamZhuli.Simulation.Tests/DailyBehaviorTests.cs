using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 日内行为多样性测试。验证一天内价格行为是否多样化(非单一化)。
/// 统计:涨跌占比、波动幅度、是否出现单向走势、成交分布。
/// </summary>
public class DailyBehaviorTests
{
    private static SimulationLoop SetupFullMarket(int ticksPerDay = 100, int? seed = null)
    {
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(200000) };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, 30));
        var MM = new ParticipantId("MM");
        var mm = loop.Session.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10m));
        // 机构B(做市+风险控制,浅盘口让散户能推动价格)
        loop.AddParticipant(new InstitutionB(loop.Session, new ParticipantId("机构B"), new Price(10m),
            cash: 1_000_000_000m, initialHolding: 20000, baseDepthPerLevel: 100, levels: 8, seed: seed ?? 1));
        // 散户画像池
        loop.AddParticipant(new RetailProfilePool(loop.Session, new ParticipantId("散户池"), new Price(10m), seed: seed ?? 2));
        // AI主力
        loop.AddParticipant(new AI.AIMainForce(loop.Session, new ParticipantId("AI"), new Price(10m),
            cash: 100_000_000m, initialHolding: 10000, initialCost: new Price(10m), seed: seed ?? 3));
        loop.Start();
        return loop;
    }

    [Fact]
    public void DailyBehavior_NotFlatLine_HasVolatility()
    {
        // 跑完整一天,价格不应是一条直线(应有波动)
        var loop = SetupFullMarket(ticksPerDay: 100, seed: 42);
        var prices = new List<decimal>();
        while (!loop.IsDayClosed && !loop.IsFinished)
        {
            loop.Step();
            if (loop.Session.Engine.LastPrice is { } p) prices.Add(p.Value);
        }
        Assert.True(prices.Count > 10, $"应有足够成交,实际{prices.Count}");
        // 计算波动:标准差应 > 0(不是直线)
        decimal mean = prices.Average();
        decimal sumSq = prices.Sum(p => (p - mean) * (p - mean));
        decimal std = (decimal)Math.Sqrt((double)(sumSq / prices.Count));
        Assert.True(std > 0.003m, $"日内应有波动(标准差>0.003),实际{std:F4} 均价{mean:F2}");
    }

    [Fact]
    public void DailyBehavior_NotAlwaysSingleDirection()
    {
        // 跑完整一天,不应是纯单向(全涨或全跌);应有涨有跌
        var loop = SetupFullMarket(ticksPerDay: 100, seed: 100);
        var prices = new List<decimal>();
        while (!loop.IsDayClosed && !loop.IsFinished)
        {
            loop.Step();
            if (loop.Session.Engine.LastPrice is { } p) prices.Add(p.Value);
        }
        if (prices.Count < 5) return;
        // 统计涨跌tick数
        int up = 0, down = 0;
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i] > prices[i - 1]) up++;
            else if (prices[i] < prices[i - 1]) down++;
        }
        // 不应该全是涨或全是跌(允许极端情况,但至少有一个反向)
        Assert.True(up > 0 && down > 0,
            $"日内应有涨有跌(非纯单向),涨{up}跌{down}");
        // 单向占比不应超过90%(否则太单调)
        decimal ratio = (decimal)Math.Max(up, down) / (up + down);
        Assert.True(ratio < 0.9m, $"单向占比{ratio:P0}过高,涨{up}跌{down},行为单一化");
    }

    [Fact]
    public void DailyBehavior_MultipleSeeds_Vary()
    {
        // 不同seed跑出的日内行为应有差异(非完全一致)
        var results = new List<(decimal high, decimal low, decimal close)>();
        for (int s = 0; s < 3; s++)
        {
            var loop = SetupFullMarket(ticksPerDay: 80, seed: s * 10 + 1);
            decimal high = 0, low = decimal.MaxValue, last = 10m;
            while (!loop.IsDayClosed && !loop.IsFinished)
            {
                loop.Step();
                if (loop.Session.Engine.LastPrice is { } p)
                { if (p.Value > high) high = p.Value; if (p.Value < low) low = p.Value; last = p.Value; }
            }
            results.Add((high, low, last));
        }
        // 三个seed的高低点应有差异(非完全相同)
        var highs = results.Select(r => r.high).Distinct().Count();
        var lows = results.Select(r => r.low).Distinct().Count();
        Assert.True(highs > 1 || lows > 1, "不同seed的日内行为应有差异");
    }
}
