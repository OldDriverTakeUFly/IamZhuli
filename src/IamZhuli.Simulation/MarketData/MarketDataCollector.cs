using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.MarketData;

/// <summary>分时图的一个采样点。</summary>
public readonly record struct TimesharePoint(int TickOfDay, decimal Price, int CumVolume);

/// <summary>带日序的日K线(区别于无日的 Candle,供图表展示与指标计算)。</summary>
public readonly record struct DailyCandle(
    int Day, decimal Open, decimal High, decimal Low, decimal Close, int Volume)
{
    public bool IsUp => Close >= Open;
}

/// <summary>
/// 市场数据采集器。订阅 SimulationLoop 的三个事件,聚合分时点、当日K线、历史日K、成交量、换手率。
/// 并在日切时归档当日K、更新涨跌停基准(PreviousClose)、重算 MACD。
/// 线程约束:事件回调里只做内存读写,不抢 GameSingleton 的锁。
/// </summary>
public sealed class MarketDataCollector
{
    private readonly SimulationLoop _loop;
    private readonly MacdCalculator _macd = new();

    // 当日聚合状态
    private decimal? _todayOpen;
    private decimal _todayHigh = decimal.MinValue;
    private decimal _todayLow = decimal.MaxValue;
    private int _todayVolume;
    private bool _todayFinalized;
    private decimal _lastPrice;   // 最新已知价(无成交时沿用)

    public List<TimesharePoint> TodayTimeshare { get; } = new();
    public List<DailyCandle> DailyCandles { get; } = new();
    public List<MacdPoint> MacdSeries { get; } = new();
    public decimal PreviousClose { get; private set; }

    public int TodayVolume => _todayVolume;
    public decimal? TodayOpen => _todayOpen;
    public decimal TodayHigh => _todayHigh == decimal.MinValue ? 0 : _todayHigh;
    public decimal TodayLow => _todayLow == decimal.MaxValue ? 0 : _todayLow;
    public decimal LastPrice => _lastPrice;
    /// <summary>当日换手率(%)= 成交量 / 流通盘。</summary>
    public decimal TurnoverRate => _loop.Session.Engine.Rules.FloatShares.Value == 0 ? 0
        : (decimal)_todayVolume / _loop.Session.Engine.Rules.FloatShares.Value * 100;

    public MarketDataCollector(SimulationLoop loop, decimal initialPreviousClose)
    {
        _loop = loop;
        PreviousClose = initialPreviousClose;
        loop.Session.OnTrade += OnTrade;
        loop.OnTick += OnTick;
        loop.OnNewDay += OnNewDay;
    }

    /// <summary>预加载历史K线(关卡背景)。玩家进场即看到过去的走势。</summary>
    public void PreloadHistory(IEnumerable<DailyCandle> historyCandles)
    {
        DailyCandles.Clear();
        foreach (var c in historyCandles)
        {
            DailyCandles.Add(c);
            PreviousClose = c.Close;
        }
        // 更新涨跌停基准为历史最后收盘
        if (DailyCandles.Count > 0)
            _loop.Session.Engine.Rules.PreviousClose = new Price(DailyCandles[^1].Close);
    }

    /// <summary>每笔成交:更新开盘/最高/最低/累计量。</summary>
    private void OnTrade(Price price, Quantity qty, Side side)
    {
        decimal p = price.Value;
        _todayOpen ??= p;                 // 当日首笔成交价 = 开盘价
        if (p > _todayHigh) _todayHigh = p;
        if (p < _todayLow) _todayLow = p;
        _todayVolume += qty.Value;
        _lastPrice = p;
    }

    /// <summary>每 tick:记分时点;最后一 tick 固化收盘并归档。</summary>
    private void OnTick(long totalTick)
    {
        var clock = _loop.Clock;
        // 若当日尚无成交,沿用上一已知价做分时点(真实分时形态)
        decimal pointPrice = _lastPrice == 0
            ? (_loop.Session.Engine.LastPrice?.Value ?? PreviousClose)
            : _lastPrice;
        TodayTimeshare.Add(new TimesharePoint(clock.CurrentTickOfDay, Math.Round(pointPrice, 2), _todayVolume));

        // 收盘固化:当 tick 是当日最后一个(已进入 Closed 阶段或到 TicksPerDay)
        if (!_todayFinalized && (clock.Phase == SessionPhase.Closed || clock.CurrentTickOfDay >= clock.TicksPerDay))
        {
            FinalizeToday(clock.CurrentDay);
        }
    }

    /// <summary>归档当日K线、更新 PreviousClose、重算 MACD、清空当日状态。</summary>
    private void FinalizeToday(int day)
    {
        decimal close = _lastPrice == 0 ? PreviousClose : _lastPrice;
        decimal open = _todayOpen ?? close;
        DailyCandles.Add(new DailyCandle(day, Math.Round(open, 2), Math.Round(TodayHigh, 2),
            Math.Round(TodayLow == decimal.MaxValue ? open : TodayLow, 2),
            Math.Round(close, 2), _todayVolume));
        PreviousClose = close;
        // 修复涨跌停基准:日切后用新收盘价
        _loop.Session.Engine.Rules.PreviousClose = new Price(close);
        // 增量计算 MACD
        _macd.Update(close);
        MacdSeries.Add(_macd.Current);
        _todayFinalized = true;
    }

    /// <summary>日切:清空当日聚合,准备新的一天。</summary>
    private void OnNewDay(int day)
    {
        // 若上一日因无成交等原因未固化,补固化
        if (!_todayFinalized && (_todayOpen.HasValue || _todayVolume > 0))
            FinalizeToday(day - 1);
        _todayOpen = null;
        _todayHigh = decimal.MinValue;
        _todayLow = decimal.MaxValue;
        _todayVolume = 0;
        _todayFinalized = false;
        TodayTimeshare.Clear();
    }
}
