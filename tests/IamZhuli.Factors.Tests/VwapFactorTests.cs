using IamZhuli.Core;
using IamZhuli.Factors;

namespace IamZhuli.Factors.Tests;

/// <summary>
/// VWAP 因子测试。手算已知序列的 VWAP 对比实现。
/// </summary>
public class VwapFactorTests
{
    private static MarketDataSnapshot Snap(decimal price)
        => MarketDataSnapshot.Of(new Price(price), Array.Empty<QuoteLevel>(), Array.Empty<QuoteLevel>());

    [Fact]
    public void Vwap_WeightedByVolumePrice()
    {
        // 一笔 10元×100手 + 一笔 11元×100手 → VWAP = (10×100+11×100)/200 = 10.5
        var v = new VwapFactor(window: 10);
        v.RecordTrade(new Price(10m), 100);
        v.OnTick();
        v.RecordTrade(new Price(11m), 100);
        v.OnTick();
        Assert.Equal(10.5m, v.Vwap);
    }

    [Fact]
    public void Vwap_VolumeWeighted_NotSimpleAverage()
    {
        // 量大的一笔在低位:10元×300手 + 11元×100手 → VWAP=10.25(偏向量大的10元)
        var v = new VwapFactor(window: 10);
        v.RecordTrade(new Price(10m), 300);
        v.OnTick();
        v.RecordTrade(new Price(11m), 100);
        v.OnTick();
        Assert.True(v.Vwap < 10.5m, $"量在低价位,VWAP应低于简单均值10.5,实际{v.Vwap}");
        Assert.Equal(10.25m, v.Vwap);
    }

    [Fact]
    public void NoTrades_VwapIsNull()
    {
        var v = new VwapFactor(window: 10);
        v.OnTick();
        Assert.Null(v.Vwap);
    }

    [Fact]
    public void Deviation_PositiveWhenPriceAboveVwap()
    {
        var v = new VwapFactor(window: 10);
        v.RecordTrade(new Price(10m), 100);
        v.OnTick();
        // 现价 10.4 高于 VWAP 10 → 偏离 +4%
        var dev = v.Deviation(Snap(10.4m));
        Assert.True(dev > 0);
        Assert.True(dev > 0.03m && dev < 0.05m, $"偏离应约+4%,实际{dev}");
    }

    [Fact]
    public void Deviation_NegativeWhenPriceBelowVwap()
    {
        var v = new VwapFactor(window: 10);
        v.RecordTrade(new Price(10m), 100);
        v.OnTick();
        var dev = v.Deviation(Snap(9.6m));   // 现价低于 VWAP
        Assert.True(dev < 0);
    }

    [Fact]
    public void Deviation_NullWhenNoVwap()
    {
        var v = new VwapFactor(window: 10);
        v.OnTick();
        Assert.Null(v.Deviation(Snap(10m)));
    }

    [Fact]
    public void Window_RollsOffOldTrades()
    {
        // window=2:旧的 tick 会被挤出窗口
        var v = new VwapFactor(window: 2);
        v.RecordTrade(new Price(10m), 100); v.OnTick();   // tick1
        v.RecordTrade(new Price(12m), 100); v.OnTick();   // tick2
        v.RecordTrade(new Price(14m), 100); v.OnTick();   // tick3 → tick1 被挤出
        // 窗口内只剩 tick2(12×100)+tick3(14×100) → VWAP=13
        Assert.Equal(13m, v.Vwap);
    }
}
