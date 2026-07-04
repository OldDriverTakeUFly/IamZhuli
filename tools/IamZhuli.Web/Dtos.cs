namespace IamZhuli.Web;

// ── 盘口快照 ──
public record PriceLevelDto(decimal Price, int Qty);
public record TimesharePointDto(int TickOfDay, decimal Price, int CumVolume);
public record DailyCandleDto(int Day, decimal Open, decimal High, decimal Low, decimal Close, int Volume);
public record MacdDto(decimal Dif, decimal Dea, decimal Hist);
public record MarketSnapshotDto(
    int CurrentDay, int TotalDays,
    int TickOfDay, int TicksPerDay,
    string Phase, bool IsPaused, bool IsFinished,
    decimal? LastPrice, decimal? BestBid, decimal? BestAsk,
    decimal UpperLimit, decimal LowerLimit,
    decimal PreviousClose, decimal TurnoverRate,
    List<PriceLevelDto> Asks,   // 卖5→卖1(展示顺序)
    List<PriceLevelDto> Bids,   // 买1→买5
    AccountDto Account,
    List<TimesharePointDto> Timeshare,        // 当日分时点
    DailyCandleDto? TodayCandle,              // 当日实时K(未收盘)
    List<DailyCandleDto> DailyCandles,        // 历史日K
    List<MacdDto> Macd);                      // MACD序列(对齐日K)

// ── 账户 ──
public record AccountDto(
    decimal Cash,               // 总现金(元)
    decimal AvailableCash,      // 可用(元)
    int PositionTotal,          // 总持仓(手)
    int PositionAvailable,      // 可卖(手)
    int PositionT1Locked,       // T+1 锁定(手)
    decimal AverageCost,        // 持仓成本(元)
    decimal TotalEquity,        // 总权益(元)
    decimal FloatingProfit);    // 浮盈(元)

// ── 下单 ──
public record OrderRequestDto(string Side, string Type, decimal? Price, int Qty);
public record OrderResultDto(
    long OrderId, string Status,
    decimal AvgFillPrice, int TotalFilled, int RemainingQty, string? Error);

// ── 成交推送 ──
public record TradeDto(decimal Price, int Qty, string TakerSide);
public record PriceDto(decimal Price);

// ── AI 内心独白(调试/复盘)——
public record AIDto(int Day, int TickOfDay, string State, string DetectedIntent, double Confidence, string Reason);
