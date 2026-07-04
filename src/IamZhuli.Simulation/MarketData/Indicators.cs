namespace IamZhuli.Simulation.MarketData;

/// <summary>MACD 指标的一个点:DIF(快慢线差)、DEA(DIF的EMA9)、柱(2×(DIF-DEA))。</summary>
public readonly record struct MacdPoint(decimal Dif, decimal Dea, decimal Histogram);

/// <summary>
/// MACD 指标计算器(标准算法)。基于日K收盘价序列滚动计算:
/// EMA12、EMA26、DIF=EMA12-EMA26、DEA=DIF的EMA9、MACD柱=2×(DIF-DEA)。
/// 用 Update 逐日追加收盘价,Current 取最新值。EMA 平滑系数 α=2/(N+1)。
/// </summary>
public sealed class MacdCalculator
{
    private const int Fast = 12, Slow = 26, Signal = 9;
    private decimal _emaFast, _emaSlow, _difEma;
    private int _count;
    private bool _initialized;

    public MacdPoint Current { get; private set; }

    /// <summary>追加一个收盘价,更新 MACD。返回当前 MACD 点。</summary>
    public MacdPoint Update(decimal close)
    {
        if (!_initialized)
        {
            // 前 N 日用简单方式初始化:EMA 初始值 = 第一日收盘价
            _emaFast = close;
            _emaSlow = close;
            _difEma = 0;
            _initialized = true;
        }
        _count++;
        // EMA 滚动:EMA_today = α×close + (1-α)×EMA_yesterday
        _emaFast = Ema(Fast, close, _emaFast);
        _emaSlow = Ema(Slow, close, _emaSlow);
        decimal dif = _emaFast - _emaSlow;
        _difEma = Ema(Signal, dif, _difEma);
        decimal hist = 2 * (dif - _difEma);
        Current = new MacdPoint(Math.Round(dif, 4), Math.Round(_difEma, 4), Math.Round(hist, 4));
        return Current;
    }

    private static decimal Ema(int period, decimal value, decimal prevEma)
    {
        decimal alpha = 2m / (period + 1);
        return alpha * value + (1 - alpha) * prevEma;
    }

    public void Reset()
    {
        _emaFast = _emaSlow = _difEma = 0;
        _count = 0;
        _initialized = false;
        Current = default;
    }
}
