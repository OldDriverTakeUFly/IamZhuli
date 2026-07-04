namespace IamZhuli.Web;

// ── 盘口快照 ──
public record PriceLevelDto(decimal Price, int Qty);
public record TimesharePointDto(int TickOfDay, decimal Price, int CumVolume);
public record DailyCandleDto(int Day, decimal Open, decimal High, decimal Low, decimal Close, int Volume);
public record MacdDto(decimal Dif, decimal Dea, decimal Hist);
public record ObjectiveProgressDto(string Description, bool Achieved, decimal Progress, string Detail);
public record MarketSnapshotDto(
    int CurrentDay, int TotalDays,
    int TickOfDay, int TicksPerDay,
    string Phase, bool IsPaused, bool IsFinished,
    decimal? LastPrice, decimal? BestBid, decimal? BestAsk,
    decimal UpperLimit, decimal LowerLimit,
    decimal PreviousClose, decimal TurnoverRate,
    List<PriceLevelDto> Asks,
    List<PriceLevelDto> Bids,
    AccountDto Account,
    List<TimesharePointDto> Timeshare,
    DailyCandleDto? TodayCandle,
    List<DailyCandleDto> DailyCandles,
    List<MacdDto> Macd,
    decimal RegulatorHeat, string PenaltyLevel, string LatestRegulatorEvent,
    List<ObjectiveProgressDto> Objectives, bool IsLevelOver,
    decimal Sentiment, int RetailActiveCount);

// ── 关卡结算 ──
public record LevelResultDto(bool IsVictory, int Stars, string CoachComment, string FailureReason,
    List<ObjectiveProgressDto> Objectives);

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
