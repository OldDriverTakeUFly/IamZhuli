using IamZhuli.Core;

namespace IamZhuli.Simulation.Levels;

/// <summary>关卡目标类型。</summary>
public enum ObjectiveType
{
    /// <summary>把股价拉/压到目标价(如从10拉到15)。</summary>
    ReachPrice,
    /// <summary>在目标价之上完成出货比例(如高位出货30%)。</summary>
    DistributeAtHigh,
    /// <summary>护盘:利空下维持股价不低于某价。</summary>
    DefendPrice,
    /// <summary>隐蔽吸筹:建仓到指定比例且不超监管红线。</summary>
    AccumulateQuietly
}

/// <summary>单个目标。</summary>
public sealed class Objective
{
    public ObjectiveType Type { get; init; }
    public string Description { get; init; } = "";
    /// <summary>目标价格(ReachPrice/DefendPrice 用)。</summary>
    public decimal TargetPrice { get; init; }
    /// <summary>出货/吸筹比例(DistributeAtHigh/AccumulateQuietly 用,0~1)。</summary>
    public decimal TargetRatio { get; init; }
    /// <summary>监管关注值上限(吸筹类用,超过即失败)。</summary>
    public decimal MaxHeat { get; init; } = 100m;
}

/// <summary>
/// 关卡定义(数据驱动,可 JSON 序列化)。
/// 含股票参数、初始状态、目标、约束、难度(AI/散户/监管参数)。
/// </summary>
public sealed class LevelDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Briefing { get; init; } = "";   // 关卡简报(剧情)

    // 股票参数
    public decimal IntrinsicValue { get; init; } = 10m;
    public decimal InitialPrice { get; init; } = 10m;
    public int FloatShares { get; init; } = 200000;        // 流通盘(手)
    public int TotalDays { get; init; } = 30;
    public int TicksPerDay { get; init; } = 600;   // 600 tick × 400ms = 4分钟/天

    // 玩家初始
    public decimal PlayerCash { get; init; } = 100_000_000m;
    public int PlayerInitialHolding { get; init; } = 0;

    // 做市商(提供流动性)
    public int MarketMakerHolding { get; init; } = 100000;

    // 散户
    public int RetailHolding { get; init; } = 50000;
    public decimal RetailCash { get; init; } = 200_000_000m;

    // AI 主力
    public int AiHolding { get; init; } = 20000;
    public double AiSensitivity { get; init; } = 0.6;

    // 目标与约束
    public List<Objective> Objectives { get; init; } = new();
    public decimal MaxHeatAllowed { get; init; } = 100m;    // 监管红线(超过失败)

    public static LevelDefinition Tutorial() => new()
    {
        Id = "tutorial", Name = "教程:首次拉升",
        Briefing = "把鼎鼎集团从10元拉升到12元,完成你的第一次操盘。",
        IntrinsicValue = 10m, InitialPrice = 10m,
        PlayerCash = 100_000_000m,
        Objectives = new() { new() { Type = ObjectiveType.ReachPrice, Description = "股价达到12元", TargetPrice = 12m } }
    };

    public static LevelDefinition PumpAndDump() => new()
    {
        Id = "pump_dump", Name = "拉升出货",
        Briefing = "把股价从10元拉到13元,并在高位出货至少30%仓位。",
        IntrinsicValue = 10m, InitialPrice = 10m,
        PlayerCash = 100_000_000m,
        Objectives = new()
        {
            new() { Type = ObjectiveType.ReachPrice, Description = "股价达到13元", TargetPrice = 13m },
            new() { Type = ObjectiveType.DistributeAtHigh, Description = "高位出货30%", TargetRatio = 0.3m }
        }
    };

    public static LevelDefinition Accumulate() => new()
    {
        Id = "accumulate", Name = "隐蔽吸筹",
        Briefing = "不动声色地建仓至流通盘的15%,且全程监管关注值不超过50%。",
        IntrinsicValue = 10m, InitialPrice = 10m, FloatShares = 200000,
        PlayerCash = 150_000_000m,
        Objectives = new() { new() { Type = ObjectiveType.AccumulateQuietly,
            Description = "建仓至15%(3万手)", TargetRatio = 0.15m, MaxHeat = 50m } },
        MaxHeatAllowed = 50m
    };
}
