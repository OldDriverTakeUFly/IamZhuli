using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Regulators;

namespace IamZhuli.Simulation.MarketData;

/// <summary>信号误导类型(假大宗/龙虎榜造假)。</summary>
public enum SignalType
{
    /// <summary>假大宗卖出:散布"主力在大宗市场抛售"假象 → 散户恐慌(Confidence↓, NewsBias-)。</summary>
    FakeBigSell,
    /// <summary>假大宗买入:散布"机构大宗接货"假象 → 散户跟风(Confidence↑, NewsBias+, GreedFear↑)。</summary>
    FakeBigBuy,
    /// <summary>上龙虎榜:吸引市场注意力 → 群体热度暴增(HerdMood↑)。</summary>
    DragonList
}

/// <summary>
/// 信号误导系统:通过制造假的大宗交易/龙虎榜信号,误导散户对主力的判断。
/// 消耗资金,影响情绪,有信息操纵监管风险。
/// </summary>
public sealed class BlockTradeSignal
{
    private readonly MarketSentiment _sentiment;
    private readonly Regulator _regulator;
    private readonly List<(int Day, SignalType Type)> _history = new();

    public IReadOnlyList<(int Day, SignalType Type)> History => _history;

    public BlockTradeSignal(MarketSentiment sentiment, Regulator regulator)
    {
        _sentiment = sentiment;
        _regulator = regulator;
    }

    /// <summary>发布假信号。返回是否成功+错误信息。</summary>
    public bool Publish(SignalType type, Account player, int day, out string error)
    {
        error = "";
        decimal cost = GetCost(type);
        if (player.AvailableCash < cost)
        {
            error = $"资金不足,需要{cost / 10000:F0}万";
            return false;
        }
        player.DebitCash(cost);
        _history.Add((day, type));

        // 情绪影响
        switch (type)
        {
            case SignalType.FakeBigSell:
                _sentiment.NewsShock(positive: false, 0.15m);   // Confidence↓, NewsBias-
                break;
            case SignalType.FakeBigBuy:
                _sentiment.NewsShock(positive: true, 0.15m);    // Confidence↑, NewsBias+
                _sentiment.ApplyNewsEffect(0, 0, 0.08m);         // GreedFear↑(贪婪目标推高)
                break;
            case SignalType.DragonList:
                _sentiment.ApplyNewsEffect(0, 0.3m, 0);          // HerdMood↑(群体热度暴增)
                break;
        }

        // 监管风险:信号误导属于信息操纵
        _regulator.OnSignalPublished(type.ToString());
        return true;
    }

    /// <summary>信号类型的成本。</summary>
    public static decimal GetCost(SignalType type) => type switch
    {
        SignalType.FakeBigSell => 100_000m,   // 10万
        SignalType.FakeBigBuy => 100_000m,
        SignalType.DragonList => 50_000m,     // 5万
        _ => 50_000m
    };
}
