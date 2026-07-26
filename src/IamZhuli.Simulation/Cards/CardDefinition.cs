namespace IamZhuli.Simulation.Cards;

/// <summary>卡牌类别。</summary>
public enum CardCategory
{
    Trading,    // 操盘牌(直接下单)
    Media,      // 舆论牌(操纵情绪)
    Signal,     // 信号牌(误导散户)
    Defense     // 防御牌(降低风险)
    // Combo 连招牌不在手牌中,自动检测组合触发
}

/// <summary>卡牌效果类型(决定 CardEngine 怎么执行)。</summary>
public enum CardEffect
{
    None,
    LimitBuy,           // 限价买入
    MarketBuy,          // 市价买入
    MarketSell,         // 市价卖出(需持仓)
    LimitWall,          // 限价挂墙(买卖各挂)
    ScheduledSell,      // 分批延迟卖出(未来N tick每tick卖X手)
    ShortSell,          // 做空卖出
    ShortCover,         // 买回平仓
    NewsPositive,       // 发布利好
    NewsNegative,       // 散布利空
    NewsRumor,          // 制造传闻
    NewsPump,           // 水军造势
    SignalFakeSell,     // 假大宗卖出
    SignalFakeBuy,      // 假大宗买入
    SignalDragonList,   // 上龙虎榜
    LayLow,             // 蛰伏冷却(监管-15,不能下单)
    CancelAll,          // 撤清所有挂单
}

/// <summary>卡牌定义(模板)。每张牌的静态属性。</summary>
public sealed record CardDefinition(
    string Name,            // 牌名
    CardCategory Category,  // 类别
    CardEffect Effect,      // 效果类型
    int EnergyCost,         // 能量消耗
    decimal CashCost,       // 资金消耗(0=不消耗)
    int RegulatorHeat,      // 出牌增加的监管槽(正=增,负=减)
    int InfoHeat,           // 出牌增加的信息监管槽
    int EffectValue,        // 效果数值(如买入量500手)
    int DurationTicks,      // 延迟效果持续tick数(0=即时)
    string Description)     // 描述文字
{
    /// <summary>牌库(21张基础牌,不含连招牌)。</summary>
    public static readonly IReadOnlyList<CardDefinition> Library = BuildLibrary();

    private static List<CardDefinition> BuildLibrary() => new()
    {
        // —— 操盘牌 ——
        new("限价吸筹", CardCategory.Trading, CardEffect.LimitBuy, 1, 0, 0, 0, 500, 0, "限价买入500手,挂在买一下方"),
        new("限价加仓", CardCategory.Trading, CardEffect.LimitBuy, 2, 0, 0, 0, 1500, 0, "限价买入1500手"),
        new("市价急拉", CardCategory.Trading, CardEffect.MarketBuy, 3, 0, 5, 0, 2000, 0, "市价买入2000手,急速拉升"),
        new("暴力拉升", CardCategory.Trading, CardEffect.MarketBuy, 5, 0, 10, 0, 5000, 0, "市价买入5000手,不计成本推高"),
        new("市价砸盘", CardCategory.Trading, CardEffect.MarketSell, 3, 0, 5, 0, 2000, 0, "市价卖出2000手,急速打压"),
        new("暴力砸盘", CardCategory.Trading, CardEffect.MarketSell, 5, 0, 10, 0, 5000, 0, "市价卖出5000手,不计成本砸盘"),
        new("限价挂墙", CardCategory.Trading, CardEffect.LimitWall, 1, 0, 2, 0, 1000, 0, "限价买卖各挂1000手,夹缝支撑/压制"),
        new("分批出货", CardCategory.Trading, CardEffect.ScheduledSell, 2, 0, 3, 0, 200, 10, "未来10tick每tick限价卖出200手"),
        new("做空突袭", CardCategory.Trading, CardEffect.ShortSell, 3, 0, 5, 0, 1000, 0, "做空卖出1000手"),
        new("大额做空", CardCategory.Trading, CardEffect.ShortSell, 5, 0, 8, 0, 3000, 0, "做空卖出3000手"),
        new("买回平仓", CardCategory.Trading, CardEffect.ShortCover, 2, 0, 0, 0, 0, 0, "平仓全部空头持仓"),

        // —— 舆论牌 ——
        new("发布利好", CardCategory.Media, CardEffect.NewsPositive, 1, 0, 0, 0, 0, 0, "发布利好消息,散户信心+15%"),
        new("散布利空", CardCategory.Media, CardEffect.NewsNegative, 1, 0, 0, 0, 0, 0, "散布利空消息,散户信心-15%"),
        new("制造传闻", CardCategory.Media, CardEffect.NewsRumor, 2, 5000, 0, 5, 0, 0, "制造市场传闻,群体热度+10%(花费5k)"),
        new("水军造势", CardCategory.Media, CardEffect.NewsPump, 2, 20000, 0, 3, 0, 0, "雇佣水军发帖,贪婪持续上升(花费2w)"),

        // —— 信号牌 ——
        new("假大宗卖出", CardCategory.Signal, CardEffect.SignalFakeSell, 2, 100000, 0, 8, 0, 0, "散布假大宗卖出信号,散户恐慌(花费10w)"),
        new("假大宗买入", CardCategory.Signal, CardEffect.SignalFakeBuy, 2, 100000, 0, 8, 0, 0, "散布假大宗买入信号,散户跟风(花费10w)"),
        new("上龙虎榜", CardCategory.Signal, CardEffect.SignalDragonList, 1, 50000, 0, 4, 0, 0, "上龙虎榜吸引注意,群体热度暴增(花费5w)"),

        // —— 防御牌 ——
        new("蛰伏冷却", CardCategory.Defense, CardEffect.LayLow, 0, 0, -15, 0, 0, 0, "监管槽-15,但本回合不能再出操盘牌"),
        new("撤清挂单", CardCategory.Defense, CardEffect.CancelAll, 0, 0, -5, 0, 0, 0, "撤销所有挂单,监管槽-5"),
    };
}

/// <summary>卡牌实例(手牌中的一张,带唯一ID)。</summary>
public sealed record CardInstance(int UniqueId, CardDefinition Definition);

/// <summary>卡牌阶段(回合内)。</summary>
public enum CardPhase
{
    /// <summary>出牌阶段(loop暂停,等玩家操作)。</summary>
    Play,
    /// <summary>执行阶段(loop运行,卡牌效果自动执行)。</summary>
    Resolve
}

/// <summary>连招定义(自动检测,不在手牌中)。</summary>
public sealed record ComboDefinition(
    string Name,
    CardEffect[] RequiredSequence,  // 需要按顺序打出的牌效果
    string BonusDescription);       // 连招加成描述
