using IamZhuli.Core;
using IamZhuli.Simulation.Scenarios;

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
    AccumulateQuietly,
    /// <summary>洗盘再拉:先砸盘到低价清洗散户,再拉回目标价(两阶段目标)。</summary>
    WashThenPump,
    /// <summary>逆向博弈:在强AI对手下存活N天且不亏损(AI会主动反杀)。</summary>
    SurviveAdversarial
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

    /// <summary>市场场景类型(决定历史K线走势+预演)。默认 Decline(下跌)。</summary>
    public ScenarioType Scenario { get; init; } = ScenarioType.Decline;

    // 股票参数
    public decimal IntrinsicValue { get; init; } = 10m;
    public decimal InitialPrice { get; init; } = 10m;
    public int FloatShares { get; init; } = 200000;        // 流通盘(手)
    public int TotalDays { get; init; } = 30;
    public int TicksPerDay { get; init; } = 150;   // 150 tick × 400ms = 1分钟/天

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
        Scenario = ScenarioType.Sideways,   // 横盘:教程环境平稳,适合新手
        PlayerCash = 100_000_000m,
        Objectives = new() { new() { Type = ObjectiveType.ReachPrice, Description = "股价达到12元", TargetPrice = 12m } }
    };

    public static LevelDefinition PumpAndDump() => new()
    {
        Id = "pump_dump", Name = "拉升出货",
        Briefing = "把股价从10元拉到13元,并在高位出货至少30%仓位。",
        IntrinsicValue = 10m, InitialPrice = 10m,
        Scenario = ScenarioType.Decline,    // 下跌趋势:玩家要逆势拉升
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
        Scenario = ScenarioType.VReversal,  // V型反转:先跌后涨,适合低位吸筹
        PlayerCash = 150_000_000m,
        Objectives = new() { new() { Type = ObjectiveType.AccumulateQuietly,
            Description = "建仓至15%(3万手)", TargetRatio = 0.15m, MaxHeat = 50m } },
        MaxHeatAllowed = 50m
    };

    /// <summary>洗盘再拉:先砸盘清洗散户止损,再拉升到目标价。</summary>
    public static LevelDefinition WashAndPump() => new()
    {
        Id = "wash_pump", Name = "洗盘再拉",
        Briefing = "先砸盘到9元以下清洗浮筹(散户持仓<30%),再拉升至12元完成出货。",
        IntrinsicValue = 10m, InitialPrice = 10m, FloatShares = 200000,
        Scenario = ScenarioType.Rally,      // 上涨趋势:洗完后顺势拉升更容易
        PlayerCash = 120_000_000m,
        Objectives = new()
        {
            new() { Type = ObjectiveType.WashThenPump, Description = "先砸至9元再拉到12元",
                    TargetPrice = 12m, TargetRatio = 0.3m },   // TargetRatio=散户持仓上限(洗盘后散户应<30%)
            new() { Type = ObjectiveType.ReachPrice, Description = "股价达到12元", TargetPrice = 12m }
        }
    };

    /// <summary>逆向博弈:强AI对手主动反杀,存活20天且不亏损。</summary>
    public static LevelDefinition Adversarial() => new()
    {
        Id = "adversarial", Name = "逆向博弈",
        Briefing = "面对 aggressive 的AI主力,存活20天且总权益不低于初始资金。",
        IntrinsicValue = 10m, InitialPrice = 10m, FloatShares = 200000,
        Scenario = ScenarioType.Sideways,   // 横盘:AI和玩家公平博弈
        PlayerCash = 100_000_000m,
        AiSensitivity = 0.9,                // AI 更激进
        Objectives = new() { new() { Type = ObjectiveType.SurviveAdversarial,
            Description = "存活20天且不亏损", TargetRatio = 0 },   // TargetRatio=0表示只需存活
        },
        TotalDays = 20
    };
}
