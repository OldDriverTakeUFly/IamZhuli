using IamZhuli.Core;
using IamZhuli.Simulation.MarketData;

namespace IamZhuli.Simulation.Scenarios;

/// <summary>剧本类型:决定历史走势的大方向。</summary>
public enum ScenarioType
{
    /// <summary>下跌(高位持续下跌,如12→10):散户被套、情绪冰点。</summary>
    Decline,
    /// <summary>上涨(低位持续上涨,如8→10):跟风客多、情绪过热。</summary>
    Rally,
    /// <summary>横盘(窄幅震荡):价投为主、情绪中性。</summary>
    Sideways,
    /// <summary>V型(先跌后涨):抄底盘获利、情绪复杂。</summary>
    VReversal
}

/// <summary>
/// 剧本:生成有确定性趋势的历史日K序列(30天),作为预演的价格引导目标。
/// 每种剧本产生不同的走势+情绪背景,决定玩家进场时的市场状态。
/// </summary>
public sealed class MarketScenario
{
    public ScenarioType Type { get; }
    public Price StartPrice { get; }
    public Price EndPrice { get; }      // 历史终点=玩家进场价
    public int Days { get; }
    public decimal DailyVolatility { get; }

    public MarketScenario(ScenarioType type, Price startPrice, Price endPrice,
                          int days = 30, decimal dailyVolatility = 0.02m)
    {
        Type = type;
        StartPrice = startPrice;
        EndPrice = endPrice;
        Days = days;
        DailyVolatility = dailyVolatility;
    }

    /// <summary>剧本对应的初始情绪标签(预演后的预期情绪)。</summary>
    public decimal ExpectedSentiment => Type switch
    {
        ScenarioType.Decline => 0.15m,    // 冰点恐慌
        ScenarioType.Rally => 0.85m,      // 过热贪婪
        ScenarioType.Sideways => 0.50m,   // 中性
        ScenarioType.VReversal => 0.60m,  // 偏热(已反弹)
        _ => 0.50m
    };

    /// <summary>生成每天的目标价序列(预演引导用)。返回每天的 OHLCV。</summary>
    public List<DailyCandle> GenerateCandles(int? seed = null)
    {
        var rng = new Random(seed ?? Environment.TickCount);
        var candles = new List<DailyCandle>();
        decimal price = StartPrice.Value;
        decimal target = EndPrice.Value;

        for (int day = 0; day < Days; day++)
        {
            decimal progress = (decimal)(day + 1) / Days;
            // 趋势价:按剧本类型插值
            decimal trendPrice = Type switch
            {
                ScenarioType.Decline => StartPrice.Value + (target - StartPrice.Value) * EaseOut(progress),
                ScenarioType.Rally => StartPrice.Value + (target - StartPrice.Value) * EaseIn(progress),
                ScenarioType.Sideways => target + (StartPrice.Value - target) * (1 - progress) * 0.3m,
                ScenarioType.VReversal => GenerateVShape(day, progress),
                _ => target
            };

            // 加随机波动(围绕趋势价)
            decimal noiseFactor = (decimal)(rng.NextDouble() - 0.5) * 2m;
            decimal noise = noiseFactor * DailyVolatility * trendPrice;
            decimal open = price;
            decimal close = Math.Max(0.01m, trendPrice + noise);
            decimal high = Math.Max(open, close) * (1 + (decimal)rng.NextDouble() * DailyVolatility * 0.5m);
            decimal low = Math.Min(open, close) * (1 - (decimal)rng.NextDouble() * DailyVolatility * 0.5m);
            int volume = 5000 + rng.Next(0, 10000);

            candles.Add(new DailyCandle(day - Days, Math.Round(open, 2), Math.Round(close, 2),
                Math.Round(high, 2), Math.Round(low, 2), volume));
            price = close;
        }
        return candles;
    }

    /// <summary>每天的目标收盘价序列(预演引导做市商用)。</summary>
    public List<decimal> DailyTargets(int? seed = null)
        => GenerateCandles(seed).Select(c => c.Close).ToList();

    // V型:前半段从起点跌到低点(比终点更低),后半段从低点涨到终点
    private decimal GenerateVShape(int day, decimal progress)
    {
        decimal low = Math.Min(StartPrice.Value, EndPrice.Value) * 0.9m;  // V底(比起终点都低)
        if (progress < 0.5m)
        {
            // 前半段:起点→V底(下跌)
            decimal t = progress * 2;   // 0→1
            return StartPrice.Value + (low - StartPrice.Value) * t;
        }
        else
        {
            // 后半段:V底→终点(上涨)
            decimal t = (progress - 0.5m) * 2;   // 0→1
            return low + (EndPrice.Value - low) * t;
        }
    }

    // 缓出(前期慢跌后期加速)——下跌剧本:慢慢跌然后加速
    private static decimal EaseOut(decimal t) => 1m - (1m - t) * (1m - t);
    // 缓入(前期慢涨后期加速)——上涨剧本:慢慢涨然后加速
    private static decimal EaseIn(decimal t) => t * t;

    /// <summary>默认剧本工厂(根据关卡类型选合适剧本)。</summary>
    public static MarketScenario Decline(Price intrinsic) => new(ScenarioType.Decline, new Price(intrinsic.Value * 1.2m), intrinsic);
    public static MarketScenario Rally(Price intrinsic) => new(ScenarioType.Rally, new Price(intrinsic.Value * 0.8m), intrinsic);
    public static MarketScenario Sideways(Price intrinsic) => new(ScenarioType.Sideways, intrinsic, intrinsic);
    public static MarketScenario VReversal(Price intrinsic) => new(ScenarioType.VReversal, new Price(intrinsic.Value * 1.1m), intrinsic);
}
