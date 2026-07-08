namespace IamZhuli.Web;

// ── 盘口快照 ──
public record PriceLevelDto(decimal Price, int Qty);
/// <summary>玩家当前在簿的挂单(限价单未成交/部分成交),供"我的挂单"列表用。</summary>
public record OpenOrderDto(long OrderId, string Side, decimal Price, int TotalQty,
                           int FilledQty, int RemainingQty);
public record TimesharePointDto(int TickOfDay, decimal Price, int CumVolume);
public record DailyCandleDto(int Day, decimal Open, decimal High, decimal Low, decimal Close, int Volume);
public record MacdDto(decimal Dif, decimal Dea, decimal Hist);
public record ObjectiveProgressDto(string Description, bool Achieved, decimal Progress, string Detail);
public record MarketSnapshotDto(
    int CurrentDay, int TotalDays,
    int TickOfDay, int TicksPerDay,
    string Phase, bool IsPaused, bool IsFinished, bool IsPreMarket,
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
    decimal Sentiment, int RetailActiveCount,
    List<OpenOrderDto> OpenOrders,
    decimal Confidence, decimal HerdMood, decimal NewsBias,
    List<NewsItemDto> ActiveNews,
    int WaterArmyLevel, bool WaterArmyActive, int WaterArmyDays, decimal WaterArmyDailyCost,
    decimal InfoHeat,   // 信息操纵关注值
    int ShortablePool, int TotalShortable);   // 可融券余量/总量

// ── 消息 ──
public record NewsItemDto(string Type, string Headline, int RemainingTicks);

// ── 关卡结算 ──
public record LevelResultDto(bool IsVictory, int Stars, string CoachComment, string FailureReason,
    List<ObjectiveProgressDto> Objectives);

// ── 筹码分布(筹码峰:按价位分桶,不区分持有方)──
public record PriceBandDto(decimal PriceLow, decimal PriceHigh, int Quantity, decimal Pct);
public record DayChipDto(int Day, decimal ClosePrice, int TotalQuantity,
    decimal PeakConcentration, List<PriceBandDto> Bands);

// ── 复盘(关键帧快照+交易日志+事件)──
public record ParticipantStateDto(string Name, int Holding, decimal AvgCost, decimal Equity);
public record ReplaySnapshotDto(int TickIndex, int Day, int TickOfDay,
    decimal Price, decimal RegulatorHeat,
    List<PriceLevelDto> TopBids, List<PriceLevelDto> TopAsks,
    List<ParticipantStateDto> Participants);
public record ReplayTradeDto(int TickIndex, decimal Price, int Qty,
    string TakerSide, string TakerId, string MakerId);
public record ReplayEventDto(int Tick, string Source, string State, string Detail);
public record ReplayDataDto(int TotalTicks, int TotalDays,
    List<ReplaySnapshotDto> Snapshots,
    List<ReplayTradeDto> Trades,
    List<ReplayEventDto> Events,
    List<DayChipDto> Chips,
    List<DailyCandleDto> DailyCandles);

// ── 积分结算 ──
public record PartyScore(string Name, decimal ReturnRate, decimal MaxDrawdown,
    decimal Score, int Rank, string Comment);
public record ScoreSettlement(List<PartyScore> Rankings);

// ── 账户 ──
public record AccountDto(
    decimal Cash,               // 总现金(元)
    decimal AvailableCash,      // 可用(元)
    int PositionTotal,          // 总持仓(手)
    int PositionAvailable,      // 可卖(手)
    int PositionT1Locked,       // T+1 锁定(手)
    decimal AverageCost,        // 持仓成本(元)
    decimal TotalEquity,        // 总权益(元)
    decimal FloatingProfit,     // 浮盈(元)
    int ShortQty,               // 空头持仓(手)
    decimal ShortCost,          // 空头成本
    decimal MarginFrozen,       // 冻结保证金
    decimal MaintenanceRatio);  // 担保比例

// ── 下单 ──
public record OrderRequestDto(string Side, string Type, decimal? Price, int Qty, bool IsShort = false);
public record OrderResultDto(
    long OrderId, string Status,
    decimal AvgFillPrice, int TotalFilled, int RemainingQty, string? Error);

// ── 成交推送 ──
public record TradeDto(decimal Price, int Qty, string TakerSide);
public record PriceDto(decimal Price);

// ── AI 内心独白(调试/复盘)——
public record AIDto(int Day, int TickOfDay, string State, string DetectedIntent, double Confidence, string Reason);
