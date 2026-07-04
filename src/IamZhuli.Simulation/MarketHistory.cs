using IamZhuli.Core;

namespace IamZhuli.Simulation;

/// <summary>
/// 历史价格序列(关卡背景)。开局给玩家一段历史 K 线,便于判断当前价位高低。
/// 生成方式:以内在价值为均值的随机游走 + 轻微趋势,模拟真实股价波动。
/// 也用于关卡开盘价的基准。
/// </summary>
public sealed class MarketHistory
{
    private readonly List<Candle> _candles = new();
    public IReadOnlyList<Candle> Candles => _candles;
    public Price LastClose => _candles.Count > 0 ? _candles[^1].Close : _intrinsic;

    private readonly Price _intrinsic;

    public MarketHistory(Price intrinsicValue) => _intrinsic = intrinsicValue;

    /// <summary>生成 days 天的历史日线。</summary>
    public void Generate(int days, decimal dailyVolatility, int? seed = null)
    {
        var rng = new Random(seed ?? Environment.TickCount);
        _candles.Clear();
        decimal price = _intrinsic.Value;
        // 缓慢漂移趋势
        decimal drift = (decimal)(rng.NextDouble() - 0.5) * dailyVolatility * 0.3m;

        for (int d = 0; d < days; d++)
        {
            decimal open = price;
            // 日内随机波动
            decimal change = (decimal)(rng.NextDouble() - 0.5) * 2 * dailyVolatility + drift;
            change = Math.Clamp(change, -dailyVolatility * 1.5m, dailyVolatility * 1.5m);
            decimal close = Math.Max(0.01m, open * (1 + change));
            decimal high = Math.Max(open, close) * (1 + (decimal)rng.NextDouble() * dailyVolatility * 0.5m);
            decimal low = Math.Min(open, close) * (1 - (decimal)rng.NextDouble() * dailyVolatility * 0.5m);
            int volume = 5000 + rng.Next(0, 10000);

            _candles.Add(new Candle(open, close, high, low, volume));
            price = close;

            // 均值回归:偏离内在价值太远时拉回
            if (Math.Abs(close - _intrinsic.Value) / _intrinsic.Value > 0.2m)
                drift = (_intrinsic.Value - close) / close * 0.1m;
            else
                drift = (decimal)(rng.NextDouble() - 0.5) * dailyVolatility * 0.2m;
        }
    }
}

/// <summary>日线 OHLCV。</summary>
public readonly record struct Candle(decimal Open, decimal Close, decimal High, decimal Low, int Volume)
{
    public bool IsUp => Close >= Open;
}
