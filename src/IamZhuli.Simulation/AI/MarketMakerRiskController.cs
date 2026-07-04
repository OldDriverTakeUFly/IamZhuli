using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;

namespace IamZhuli.Simulation.AI;

/// <summary>风险等级(决定做市激进程度)。</summary>
public enum RiskLevel { Low, Medium, High, Critical }

/// <summary>风险评估结果。</summary>
public readonly record struct RiskAssessment(
    RiskLevel Level,
    double Score,               // 综合风险值 0~1
    decimal PositionExposure,   // 持仓偏离度(净持仓占总资产比例,0~1)
    decimal DirectionRisk,      // 方向风险(价格偏离公允值程度)
    decimal VolatilityRisk,     // 波动风险
    decimal ImpactRisk,         // 冲击风险(近期被单边吃速度)
    string Detail);

/// <summary>
/// 做市商风险控制器。动态评估机构B 的风险敞口,决定做市激进程度。
/// 流动性的"软约束":风险高时收紧做市,而非硬性资金墙。
/// 四维:持仓偏离(接太多货)、方向风险(怕被套)、波动风险(扩大价差)、冲击频率(察觉主力)。
/// </summary>
public sealed class MarketMakerRiskController
{
    private readonly Price _fairValue;          // 公允价值(内在价值或MA)
    private readonly decimal _maxPositionValue; // 持仓价值上限(决定何时紧张)
    private readonly Queue<int> _recentFilledQty = new();  // 近期被吃量(算冲击)
    private readonly int _impactWindow;
    private decimal _lastPrice;
    private decimal _priceHistory;
    private decimal _volatility;

    public MarketMakerRiskController(Price fairValue, decimal maxPositionValue = 50_000_000m, int impactWindow = 20)
    {
        _fairValue = fairValue;
        _maxPositionValue = maxPositionValue;
        _impactWindow = impactWindow;
    }

    /// <summary>记录一笔成交(机构B参与时调,算冲击频率)。</summary>
    public void OnTrade(Quantity filledQty)
    {
        _recentFilledQty.Enqueue(filledQty.Value);
        while (_recentFilledQty.Count > _impactWindow) _recentFilledQty.Dequeue();
    }

    /// <summary>每 tick 更新价格/波动率。</summary>
    public void OnTick(Price? lastPrice)
    {
        if (lastPrice is { } p)
        {
            _lastPrice = p.Value;
            // 简单波动率跟踪
            if (_priceHistory > 0)
            {
                decimal change = Math.Abs(p.Value - _priceHistory) / _priceHistory;
                _volatility = _volatility * 0.9m + change * 0.1m;
            }
            _priceHistory = p.Value;
        }
    }

    /// <summary>评估当前风险。</summary>
    public RiskAssessment Assess(Account account)
    {
        // 1. 持仓偏离:净持仓市值 / 资金上限(接货越多越紧张)
        int netPos = account.Position.Total.Value;   // 机构B 主要持多头(做市累积)
        decimal posValue = netPos * _lastPrice * 100;
        decimal positionExposure = _maxPositionValue > 0
            ? Math.Min(1m, posValue / _maxPositionValue) : 0;

        // 2. 方向风险:现价偏离公允价值程度
        decimal directionRisk = _fairValue.Value > 0 && _lastPrice > 0
            ? Math.Min(1m, Math.Abs(_lastPrice - _fairValue.Value) / _fairValue.Value / 0.10m) : 0;
        // 偏离超10% 视为满风险

        // 3. 波动风险:近期波动率(归一化到0~1,5%波动=满风险)
        decimal volatilityRisk = Math.Min(1m, _volatility / 0.05m);

        // 4. 冲击风险:近期被吃单速度(近期总量 / 窗口,归一化)
        int recentTotal = _recentFilledQty.Sum();
        decimal avgPerTick = _impactWindow > 0 ? (decimal)recentTotal / _impactWindow : 0;
        // 每tick被吃500手以上=满冲击风险
        decimal impactRisk = Math.Min(1m, avgPerTick / 500m);

        // 综合风险(加权)
        double score = (double)(positionExposure * 0.35m + directionRisk * 0.25m
                                + volatilityRisk * 0.15m + impactRisk * 0.25m);
        score = Math.Clamp(score, 0, 1);

        var level = score switch
        {
            < 0.3 => RiskLevel.Low,
            < 0.6 => RiskLevel.Medium,
            < 0.85 => RiskLevel.High,
            _ => RiskLevel.Critical
        };

        return new RiskAssessment(level, score,
            positionExposure, directionRisk, volatilityRisk, impactRisk,
            $"{level}(score{score:F2}): 持仓偏离{positionExposure:P0} 方向{directionRisk:P0} 波动{volatilityRisk:P0} 冲击{impactRisk:P0}");
    }

    /// <summary>根据风险等级建议做市深度(占正常深度的比例)。</summary>
    public static decimal DepthFactor(RiskLevel level) => level switch
    {
        RiskLevel.Low => 1.0m,       // 正常挂单
        RiskLevel.Medium => 0.6m,    // 减少40%
        RiskLevel.High => 0.3m,      // 减少70%
        RiskLevel.Critical => 0.1m,  // 几乎停止
        _ => 1.0m
    };

    /// <summary>根据风险等级建议价差放大(正常0.02,风险高时扩大)。价差给波动留空间。</summary>
    public static decimal SpreadFactor(RiskLevel level) => level switch
    {
        RiskLevel.Low => 0.02m,
        RiskLevel.Medium => 0.03m,
        RiskLevel.High => 0.05m,
        RiskLevel.Critical => 0.08m,
        _ => 0.02m
    };
}
