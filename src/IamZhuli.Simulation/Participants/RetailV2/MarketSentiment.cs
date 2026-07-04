using IamZhuli.Core;

namespace IamZhuli.Simulation.Participants.RetailV2;

/// <summary>
/// 全局市场情绪指数(0~1 慢变量)。驱动所有散户画像的活跃度与激进程度。
/// 特性:惯性(不瞬变)、非对称(恐慌比贪婪传染快)、群体传染。
/// </summary>
public sealed class MarketSentiment
{
    /// <summary>当前情绪值 0~1(0=极度恐慌,0.5=中性,1=极度贪婪)。</summary>
    public decimal Value { get; private set; } = 0.5m;

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
        // 上涨→贪婪,下跌→恐惧;波动放大→情绪极端化
        decimal sentimentFromReturn = 0.5m + Math.Clamp(recentReturn * 15m, -0.4m, 0.4m);
        // 放大波动让情绪更极端(大涨大跌都推离中性)
        decimal volPush = volatility * 2m * Math.Sign(recentReturn);
        _target = Math.Clamp(sentimentFromReturn + volPush + volumeSpike * 0.1m, 0.05m, 0.95m);

        // —— 非对称趋近:恐慌下跌时趋近更快(跳窗户),贪婪上涨时趋近更慢(爬楼梯)——
        decimal adjSpeed = _target < Value ? 0.15m : 0.06m;   // 下跌更快
        Value += (_target - Value) * adjSpeed;
        Value = Math.Clamp(Value, 0m, 1m);
    }

    /// <summary>消息冲击(二期信息战接口):直接冲击目标情绪。</summary>
    public void NewsShock(decimal impact)
    {
        _target = Math.Clamp(_target + impact, 0.05m, 0.95m);
    }

    /// <summary>重置。</summary>
    public void Reset() { Value = 0.5m; _target = 0.5m; _recentReturn = 0; _volatility = 0; }
}
