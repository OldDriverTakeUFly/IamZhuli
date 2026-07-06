using IamZhuli.Core;

namespace IamZhuli.Simulation.Participants.RetailV2;

/// <summary>
/// 全局市场情绪指数(多维)。驱动所有散户画像的活跃度与激进程度。
/// 特性:惯性(不瞬变)、非对称(恐慌比贪婪传染快)、群体传染。
///
/// 维度:
/// - GreedFear (0~1): 贪婪/恐惧,价格驱动(原 Value)。所有画像兼容读取。
/// - Confidence (0~1): 对个股的信心/预期,消息面驱动,盘后冲击次日衰减。
/// - HerdMood (0~1): 群体热度(成交活跃度+情绪极端度),传闻驱动。
/// - NewsBias (-1~+1): 消息面净偏差(利好+,利空-),盘后消息写入。
/// </summary>
public sealed class MarketSentiment
{
    /// <summary>贪婪/恐惧值 0~1(0=极度恐慌,0.5=中性,1=极度贪婪)。价格驱动的主情绪。
    /// 原 Value 的映射,保持兼容。</summary>
    public decimal Value { get; private set; } = 0.5m;

    // —— 新增维度 ——
    /// <summary>对个股的信心 0~1(0=极度悲观,1=极度乐观)。消息面驱动。</summary>
    public decimal Confidence { get; private set; } = 0.5m;
    /// <summary>群体热度 0~1(0=冷清,1=狂热)。传闻/水军驱动成交活跃度。</summary>
    public decimal HerdMood { get; private set; } = 0.3m;
    /// <summary>消息面净偏差 -1~+1(正=利好累积,负=利空累积)。盘后消息写入。</summary>
    public decimal NewsBias { get; private set; }

    // —— 兼容属性(映射 GreedFear) ——
    /// <summary>贪婪度=Value(>0.5 偏贪婪)。</summary>
    public decimal Greed => Value;
    /// <summary>恐惧度=1-Value(<0.5 偏恐惧)。</summary>
    public decimal Fear => 1m - Value;
    /// <summary>情绪极端度:偏离 0.5 的绝对值,0~0.5。</summary>
    public decimal Extremity => Math.Abs(Value - 0.5m);
    public bool IsEuphoric => Value >= 0.8m;
    public bool IsPanic => Value <= 0.2m;

    private decimal _target;          // 目标情绪(由市场信号决定,Value 缓慢趋近它)
    private decimal _recentReturn;    // 近期收益率(正=涨)
    private decimal _volatility;      // 近期波动率

    /// <summary>每 tick 更新情绪指数。传入近期收益率、波动率。</summary>
    public void Update(decimal recentReturn, decimal volatility, decimal volumeSpike = 0m)
    {
        _recentReturn = recentReturn;
        _volatility = volatility;

        // —— 计算目标情绪 ——
        decimal sentimentFromReturn = 0.5m + Math.Clamp(recentReturn * 35m, -0.45m, 0.45m);
        decimal volPush = volatility * 3m * Math.Sign(recentReturn == 0 ? 1 : recentReturn);
        _target = Math.Clamp(sentimentFromReturn + volPush + volumeSpike * 0.15m, 0.02m, 0.98m);

        // —— 非对称趋近:恐慌下跌时趋近更快(跳窗户),贪婪上涨时趋近更慢(爬楼梯)——
        decimal adjSpeed = _target < Value ? 0.20m : 0.10m;
        Value += (_target - Value) * adjSpeed;
        Value = Math.Clamp(Value, 0m, 1m);

        // —— 群体热度:由贪婪度极端度 + 成交量放大驱动(自然演化) ——
        decimal naturalHerd = Extremity * 1.5m + Math.Min(0.3m, volumeSpike * 0.1m);
        HerdMood += (naturalHerd - HerdMood) * 0.05m;   // 缓慢趋近自然热度
        HerdMood = Math.Clamp(HerdMood, 0m, 1m);
    }

    /// <summary>消息系统注入的每 tick 影响(由 NewsSystem.Tick 调用)。</summary>
    public void ApplyNewsEffect(decimal confidenceDelta, decimal herdDelta, decimal greedTargetDelta)
    {
        Confidence = Math.Clamp(Confidence + confidenceDelta, 0m, 1m);
        HerdMood = Math.Clamp(HerdMood + herdDelta, 0m, 1m);
        // greedTargetDelta 直接加到 _target(绕过价格驱动,模拟消息面推贪婪/恐惧)
        _target = Math.Clamp(_target + greedTargetDelta, 0.02m, 0.98m);
    }

    /// <summary>盘后消息冲击:直接改 NewsBias + Confidence。
    /// 在日切暂停态调用,影响次日开盘。</summary>
    public void NewsShock(bool positive, decimal impact)
    {
        NewsBias = Math.Clamp(NewsBias + (positive ? impact : -impact), -1m, 1m);
        Confidence = Math.Clamp(Confidence + (positive ? impact : -impact) * 0.8m, 0m, 1m);
        // 同时推一下贪婪目标(利好推贪婪,利空推恐惧)
        _target = Math.Clamp(_target + (positive ? impact * 0.5m : -impact * 0.5m), 0.02m, 0.98m);
    }

    /// <summary>日切衰减:信心回归中性,群体热度消散,消息偏差大幅衰减。</summary>
    public void DailyDecay()
    {
        Confidence = Confidence * 0.6m + 0.5m * 0.4m;   // 信心向中性回归
        HerdMood *= 0.5m;                                 // 群体热度消散
        NewsBias *= 0.3m;                                 // 消息偏差大幅衰减(隔夜效应递减)
    }

    /// <summary>重置(关卡重新开始时)。</summary>
    public void Reset() { Value = 0.5m; _target = 0.5m; _recentReturn = 0; _volatility = 0; Confidence = 0.5m; HerdMood = 0.3m; NewsBias = 0; }
}
