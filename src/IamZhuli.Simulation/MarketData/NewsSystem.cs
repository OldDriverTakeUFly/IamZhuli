using IamZhuli.Simulation.Participants.RetailV2;

namespace IamZhuli.Simulation.MarketData;

/// <summary>消息类型(决定影响哪个情绪维度、影响哪些画像)。</summary>
public enum NewsType
{
    /// <summary>利好消息 → Confidence↑, NewsBias+ (模拟公开利好新闻,免费但一次性)</summary>
    Positive,
    /// <summary>利空消息 → Confidence↓, NewsBias- (模拟公开利空新闻,免费但一次性)</summary>
    Negative,
    /// <summary>传闻 → HerdMood↑ (群体热度升,成交活跃。小额成本,对价投无效)</summary>
    Rumor,
    /// <summary>水军造势 → GreedFear↑ (贪婪直接升。每回合收费,效果可累积叠加)</summary>
    Pump
}

/// <summary>消息条目(记录历史,复盘可见)。</summary>
public sealed record NewsItem(int Tick, NewsType Type, string Headline, decimal Impact, int DurationTicks);

/// <summary>
/// 消息系统:管理盘后发布的消息对情绪的持续影响。
/// 消息有 DurationTicks(有效期),期间每 tick 通过 MarketSentiment.ApplyNewsEffect 施加影响。
/// 过期后影响消退。日切时未过期消息保留但影响减半(隔夜效应)。
/// </summary>
public sealed class NewsSystem
{
    private readonly MarketSentiment _sentiment;
    private readonly List<NewsItem> _activeNews = new();    // 当前生效的消息
    private readonly List<NewsItem> _history = new();       // 全部历史(复盘)
    private int _tickCounter;

    public IReadOnlyList<NewsItem> ActiveNews => _activeNews;
    public IReadOnlyList<NewsItem> History => _history;
    /// <summary>当前消息面净偏差(供前端展示)。</summary>
    public decimal CurrentBias => _sentiment.NewsBias;

    public NewsSystem(MarketSentiment sentiment) => _sentiment = sentiment;

    /// <summary>发布一条消息。impact 越大效果越强,durationTicks 控制持续时长。</summary>
    public void Publish(NewsType type, string headline, decimal impact, int durationTicks, int tick = -1)
    {
        var item = new NewsItem(tick < 0 ? _tickCounter : tick, type, headline, impact, durationTicks);
        _activeNews.Add(item);
        _history.Add(item);

        // 盘后即时冲击:直接改 NewsBias 和 Confidence(次日开盘可见)
        switch (type)
        {
            case NewsType.Positive:
                _sentiment.NewsShock(positive: true, impact);
                break;
            case NewsType.Negative:
                _sentiment.NewsShock(positive: false, impact);
                break;
            case NewsType.Rumor:
                // 传闻主要影响群体热度,不改 NewsBias
                _sentiment.ApplyNewsEffect(0, impact * 0.3m, 0);
                break;
            case NewsType.Pump:
                // 水军直接推贪婪目标(绕过价格驱动)
                _sentiment.ApplyNewsEffect(0, 0, impact * 0.4m);
                _sentiment.NewsShock(positive: true, impact * 0.3m);   // 也小幅推信心
                break;
        }
    }

    /// <summary>每 tick 调用:应用活跃消息的持续影响,过期消息移除。</summary>
    public void Tick()
    {
        _tickCounter++;
        if (_activeNews.Count == 0) return;

        var expired = new List<NewsItem>();
        foreach (var news in _activeNews)
        {
            int elapsed = _tickCounter - news.Tick;
            if (elapsed >= news.DurationTicks) { expired.Add(news); continue; }

            // 持续影响:每 tick 施加衰减后的小幅影响
            // 影响随时间衰减(前强后弱):decay = 1 - elapsed/duration
            decimal decay = 1m - (decimal)elapsed / news.DurationTicks;
            decimal perTickImpact = news.Impact * decay * 0.005m;   // 每 tick 影响很小,但持续累积

            switch (news.Type)
            {
                case NewsType.Positive:
                    _sentiment.ApplyNewsEffect(perTickImpact * 0.5m, 0, perTickImpact * 0.2m);
                    break;
                case NewsType.Negative:
                    _sentiment.ApplyNewsEffect(-perTickImpact * 0.5m, 0, -perTickImpact * 0.2m);
                    break;
                case NewsType.Rumor:
                    _sentiment.ApplyNewsEffect(0, perTickImpact * 0.3m, 0);
                    break;
                case NewsType.Pump:
                    _sentiment.ApplyNewsEffect(0, 0, perTickImpact * 0.4m);
                    break;
            }
        }

        foreach (var e in expired) _activeNews.Remove(e);
    }

    /// <summary>日切:未过期消息保留(隔夜效应),但 tick 重置使影响重新计算。</summary>
    public void OnNewDay()
    {
        // 隔夜消息保留但效果减半(模拟"睡一觉冷静了")
        // 实现:不改变 DurationTicks,但影响通过 DailyDecay 自然衰减
        // (MarketSentiment.DailyDecay 已经让 Confidence/NewsBias 衰减)
    }

    /// <summary>消息类型的成本(元)。</summary>
    public static decimal GetCost(NewsType type) => type switch
    {
        NewsType.Positive => 0m,        // 利好新闻免费(模拟公开信息)
        NewsType.Negative => 0m,        // 利空新闻免费
        NewsType.Rumor => 5000m,        // 传闻:小额成本(放风/渠道)
        NewsType.Pump => 20000m,        // 水军:每回合2万
        _ => 0m
    };

    /// <summary>消息类型的默认影响强度和持续时长。</summary>
    public static (decimal Impact, int DurationTicks) GetDefaults(NewsType type) => type switch
    {
        NewsType.Positive => (0.15m, 300),    // 信心+15%,持续300tick(约2分钟)
        NewsType.Negative => (0.15m, 300),
        NewsType.Rumor => (0.10m, 200),       // 群体热度+10%,持续200tick
        NewsType.Pump => (0.08m, 400),        // 贪婪+8%,持续400tick(水军长期渗透)
        _ => (0.1m, 200)
    };
}
