using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants.RetailV2;

/// <summary>
/// 散户画像池:管理动态进出的画像实例,实现 IParticipant。
/// 每 tick:更新市场上下文 → 情绪指数 → 评估进场(抽取新画像)→ 驱动在场画像 Act → 清理离场画像。
/// 取代旧的 RetailMarket(固定4群体共享账户)。
/// </summary>
public sealed class RetailProfilePool : IParticipant
{
    public ParticipantId Id { get; }
    private readonly TradingSession _session;
    private readonly Price _intrinsic;
    private readonly Random _rng;
    private readonly MarketSentiment _sentiment = new();
    private readonly List<RetailProfile> _active = new();     // 在场画像
    private readonly PriceHistory _priceHistory;              // 近期价格序列(算动量/波动)
    private readonly VolTracker _volTracker = new();          // 成交量跟踪
    private long _tickCounter;

    public MarketSentiment Sentiment => _sentiment;
    public IReadOnlyList<RetailProfile> ActiveProfiles => _active;
    public int ActiveCount => _active.Count;
    /// <summary>散户整体净流入(累计买入量-卖出量,正=资金净流入)。</summary>
    public long NetInflow { get; private set; }

    /// <summary>在场画像数上限(计算量控制)。</summary>
    public int MaxActive { get; set; } = 60;

    public RetailProfilePool(TradingSession session, ParticipantId poolId, Price intrinsicValue,
                             int? seed = null)
    {
        Id = poolId;
        _session = session;
        _intrinsic = intrinsicValue;
        _rng = new Random(seed ?? Environment.TickCount);
        _priceHistory = new PriceHistory(30);
        // 订阅成交量
        session.OnTrade += (p, q, s) => _volTracker.Record(q.Value);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        _tickCounter++;
        var engine = session.Engine;
        var view = engine.View;
        var lastPrice = view.LastPrice ?? view.BestBid ?? view.BestAsk ?? _intrinsic;

        // 1. 更新价格历史与市场上下文
        _priceHistory.Record(lastPrice);
        var ctx = new RetailMarketContext
        {
            LastPrice = view.LastPrice,
            BestBid = view.BestBid,
            BestAsk = view.BestAsk,
            UpperLimit = engine.Rules.UpperLimit.Value,
            LowerLimit = engine.Rules.LowerLimit.Value,
            RecentReturn = _priceHistory.Return ?? 0m,
            Volatility = _priceHistory.Volatility,
            VolumeSpike = _volTracker.Spike,
            Sentiment = _sentiment,
            IntrinsicValue = _intrinsic.Value,
            FloatShares = engine.Rules.FloatShares.Value
        };

        // 2. 更新情绪指数
        _sentiment.Update(ctx.RecentReturn, ctx.Volatility, ctx.VolumeSpike - 1m);

        // 3. 评估进场(根据价格行为抽取新画像)
        TryRecruit(ctx);

        // 4. 驱动在场画像
        foreach (var p in _active.ToArray())   // ToArray 防止离场时修改集合
        {
            try { p.Act(session, ctx, _tickCounter); }
            catch { /* 单画像异常不影响整体 */ }
        }

        // 5. 清理离场画像
        _active.RemoveAll(p => !p.IsActive);

        // 6. 推进成交量窗口
        _volTracker.AdvanceTick();
    }

    /// <summary>根据当前市场信号抽取新画像进场。</summary>
    private void TryRecruit(RetailMarketContext ctx)
    {
        if (_active.Count >= MaxActive) return;
        decimal ret = ctx.RecentReturn;
        decimal vol = ctx.Volatility;
        double greed = (double)ctx.Sentiment.Greed;
        double fear = (double)ctx.Sentiment.Fear;

        // 急涨 → 抽跟风客、羊群(门槛降低,更积极)
        if (ret > 0.005m && Rand() < greed * 0.5)
        {
            Spawn(ret > 0.015m ? ProfileType.AggressiveMomentum : ProfileType.MildMomentum, ctx);
            if (Rand() < greed * 0.25) Spawn(ProfileType.Herd, ctx);
        }
        // 急跌 → 抄底猎手;同时"激活"潜在止损者(初始被套持仓)
        if (ret < -0.005m)
        {
            if (Rand() < (double)ctx.Sentiment.Value * 0.3) Spawn(ProfileType.BargainHunter, ctx);
            if (Rand() < fear * 0.25) Spawn(ProfileType.StopLoss, ctx);   // 被套者恐慌激活
        }
        // 严重低估 → 价投
        if (ctx.LastPrice is { } p && ctx.IntrinsicValue > 0
            && p.Value < ctx.IntrinsicValue * 0.92m && Rand() < 0.15)
            Spawn(ProfileType.ValueInvestor, ctx);
        // 波动放大 → 投机客
        if (vol > 0.01m && Rand() < 0.2)
            Spawn(ProfileType.Speculator, ctx);

        // —— 基础随机进场:即使无明显趋势,也有日常散户进场(避免冷启动死锁)——
        if (_active.Count < 8 && Rand() < 0.5)
        {
            var types = new[] { ProfileType.MildMomentum, ProfileType.ValueInvestor,
                                 ProfileType.Speculator, ProfileType.Herd, ProfileType.BargainHunter };
            Spawn(types[RandInt(0, types.Length)], ctx);
        }
    }

    /// <summary>创建一个画像实例并进场。</summary>
    private void Spawn(ProfileType type, RetailMarketContext ctx)
    {
        var id = new ParticipantId($"散户-{type}-{++_spawnSeq}");
        // 每画像独立账户,初始现金随机(散户钱不多)
        decimal cash = type switch
        {
            ProfileType.ValueInvestor => 500_000m + (decimal)Rand() * 2_000_000m,
            ProfileType.AggressiveMomentum => 200_000m + (decimal)Rand() * 1_000_000m,
            ProfileType.Speculator => 100_000m + (decimal)Rand() * 500_000m,
            _ => 100_000m + (decimal)Rand() * 800_000m
        };
        var acc = _session.GetOrCreateAccount(id, cash);
        // 止损者初始就持有被套筹码(模拟早已进场被套的人)
        if (type == ProfileType.StopLoss)
        {
            decimal cost = ctx.LastPrice?.Value ?? _intrinsic.Value;
            acc.Position.Seed(new Quantity(RandInt(20, 100) * 10), new Price(cost * (1.05m + (decimal)Rand() * 0.1m)));
        }

        var jitter = (decimal)Rand();
        int size = type switch
        {
            ProfileType.AggressiveMomentum => RandInt(5, 15) * 10,
            ProfileType.Speculator => RandInt(1, 5) * 10,
            ProfileType.ValueInvestor => RandInt(5, 12) * 10,
            _ => RandInt(3, 10) * 10
        };
        decimal risk = (decimal)Rand() * 0.6m + 0.2m;

        RetailProfile profile = type switch
        {
            ProfileType.AggressiveMomentum => new AggressiveMomentumProfile(acc, risk, size, jitter, _rng),
            ProfileType.MildMomentum => new MildMomentumProfile(acc, risk, size, jitter, _rng),
            ProfileType.ValueInvestor => new ValueInvestorProfile(acc, risk, size, jitter, _rng),
            ProfileType.StopLoss => new StopLossProfile(acc, risk, size, jitter, _rng),
            ProfileType.BargainHunter => new BargainHunterProfile(acc, risk, size, jitter, _rng),
            ProfileType.Speculator => new SpeculatorProfile(acc, risk, size, jitter, _rng),
            ProfileType.Herd => new HerdProfile(acc, risk, size, jitter, _rng),
            _ => new NewsDrivenProfile(acc, risk, size, jitter, _rng)
        };
        profile.Activate(_tickCounter);
        _active.Add(profile);
    }

    private int _spawnSeq;
    private double Rand() => _rng.NextDouble();
    private int RandInt(int lo, int hi) => _rng.Next(lo, hi);
}

/// <summary>近期价格序列,算收益率与波动率。</summary>
internal sealed class PriceHistory(int window)
{
    private readonly Queue<decimal> _prices = new();
    private readonly int _window = window;

    public decimal? Return
    {
        get
        {
            if (_prices.Count < _window / 2) return null;
            var arr = _prices.ToArray();
            return (arr[^1] - arr[0]) / Math.Max(arr[0], 0.01m);
        }
    }

    public decimal Volatility
    {
        get
        {
            if (_prices.Count < _window / 2) return 0;
            var arr = _prices.ToArray();
            decimal mean = arr.Average();
            decimal sumSq = arr.Sum(p => (p - mean) * (p - mean));
            return (decimal)Math.Sqrt((double)(sumSq / arr.Length)) / Math.Max(mean, 0.01m);
        }
    }

    public void Record(Price p)
    {
        _prices.Enqueue(p.Value);
        while (_prices.Count > _window) _prices.Dequeue();
    }
}

/// <summary>成交量跟踪(按 tick 分桶,算放量倍数)。</summary>
internal sealed class VolTracker
{
    private readonly Queue<int> _buckets = new();
    private int _current;
    public decimal Spike => _buckets.Count > 0 && _buckets.Average() > 0
        ? (decimal)_current / Math.Max(1m, (decimal)_buckets.Average()) : 1m;
    public void Record(int qty) => _current += qty;
    public void AdvanceTick()
    {
        _buckets.Enqueue(_current);
        while (_buckets.Count > 30) _buckets.Dequeue();
        _current = 0;
    }
}
