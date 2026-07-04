using IamZhuli.Core;
using IamZhuli.Simulation.Scenarios;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 剧本系统测试。验证不同剧本类型生成的K线符合趋势特征。
/// </summary>
public class MarketScenarioTests
{
    private const decimal Intrinsic = 10m;

    [Fact]
    public void Decline_StartsHigh_EndsLow()
    {
        var s = MarketScenario.Decline(new Price(Intrinsic));   // 12→10
        var candles = s.GenerateCandles(seed: 1);
        Assert.Equal(30, candles.Count);
        Assert.True(candles[0].Open >= 11m, $"下跌剧本起点应接近12,实际{candles[0].Open}");
        Assert.True(candles[^1].Close <= 10.5m, $"下跌剧本终点应接近10,实际{candles[^1].Close}");
        Assert.True(candles[^1].Close < candles[0].Open, "下跌剧本应整体下跌");
    }

    [Fact]
    public void Rally_StartsLow_EndsHigh()
    {
        var s = MarketScenario.Rally(new Price(Intrinsic));   // 8→10
        var candles = s.GenerateCandles(seed: 2);
        Assert.True(candles[0].Open <= 9m, $"上涨剧本起点应接近8,实际{candles[0].Open}");
        Assert.True(candles[^1].Close >= 9.5m, $"上涨剧本终点应接近10,实际{candles[^1].Close}");
        Assert.True(candles[^1].Close > candles[0].Open, "上涨剧本应整体上涨");
    }

    [Fact]
    public void Sideways_StaysInRange()
    {
        var s = MarketScenario.Sideways(new Price(Intrinsic));
        var candles = s.GenerateCandles(seed: 3);
        var closes = candles.Select(c => c.Close).ToList();
        decimal max = closes.Max(), min = closes.Min();
        // 横盘:振幅不应超过15%
        Assert.True((max - min) / min < 0.15m, $"横盘振幅应小,最高{max}最低{min}");
    }

    [Fact]
    public void VReversal_DipsThenRecovers()
    {
        var s = MarketScenario.VReversal(new Price(Intrinsic));   // 11→10,V型
        var candles = s.GenerateCandles(seed: 4);
        // V型:中段应有低点(比首尾都低)
        var closes = candles.Select(c => c.Close).ToList();
        decimal firstHalf = closes.Take(15).Min();
        decimal lastClose = closes[^1];
        Assert.True(firstHalf < lastClose, $"V型中段低点{firstHalf}应低于终点{lastClose}");
    }

    [Fact]
    public void ExpectedSentiment_MatchesScenario()
    {
        Assert.True(MarketScenario.Decline(new Price(Intrinsic)).ExpectedSentiment < 0.3m, "下跌应冰点");
        Assert.True(MarketScenario.Rally(new Price(Intrinsic)).ExpectedSentiment > 0.7m, "上涨应过热");
        Assert.True(Math.Abs(MarketScenario.Sideways(new Price(Intrinsic)).ExpectedSentiment - 0.5m) < 0.1m, "横盘应中性");
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentCandles()
    {
        var s = MarketScenario.Decline(new Price(Intrinsic));
        var a = s.GenerateCandles(seed: 1);
        var b = s.GenerateCandles(seed: 2);
        // 趋势相同但波动不同(不可背诵)
        Assert.NotEqual(a[5].Close, b[5].Close);
    }

    [Fact]
    public void DailyTargets_ReturnsClosePrices()
    {
        var s = MarketScenario.Rally(new Price(Intrinsic));
        var targets = s.DailyTargets(seed: 5);
        var candles = s.GenerateCandles(seed: 5);
        Assert.Equal(candles.Count, targets.Count);
        Assert.Equal(candles[0].Close, targets[0]);
    }
}
