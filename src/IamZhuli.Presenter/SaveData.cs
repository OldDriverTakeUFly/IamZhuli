using IamZhuli.Simulation.MarketData;

namespace IamZhuli.Presenter;

/// <summary>存档检查点(仅日切边界有效):纯数据,宿主决定存储位置(服务器文件/本地/云)。</summary>
public record SaveData
{
    public required string LevelId { get; init; }
    public required int Day { get; init; }
    public required decimal LastPrice { get; init; }
    public required decimal PreviousClose { get; init; }

    public required AccountSave Player { get; init; }
    public required AccountSave Ai { get; init; }
    public required AccountSave Institution { get; init; }
    public required string AiState { get; init; }
    public required string InstRiskLevel { get; init; }

    public required CardEngineSave Cards { get; init; }
    public string? Intent { get; init; }

    public required decimal Heat { get; init; }
    public required decimal InfoHeat { get; init; }
    public required int ShortablePool { get; init; }
    public required int TotalShortable { get; init; }

    public required SentimentSave Sentiment { get; init; }
    public required WaterArmySave WaterArmy { get; init; }
    public required List<SavedNewsDto> News { get; init; }
    public required List<CandleDto> Candles { get; init; }
}

public record AccountSave(decimal Cash, int Available, int T1Locked, decimal AvgCost,
    int ShortQty, decimal ShortCost, decimal Margin, int Bought, int Sold);

/// <summary>牌组状态(手牌/抽牌堆/弃牌堆以牌名序列化;同名即同定义)。</summary>
public record CardEngineSave(List<string> Hand, List<string> Draw, List<string> Discard,
    int Energy, int Turn, bool CustomDeck);

public record SentimentSave(decimal Value, decimal Target, decimal Confidence, decimal Herd, decimal Bias);
public record WaterArmySave(int Level, bool Active, int ActiveDays);
public record SavedNewsDto(string Type, string Headline, decimal Impact, int RemainingTicks);
public record CandleDto(int Day, decimal Open, decimal High, decimal Low, decimal Close, int Volume);
