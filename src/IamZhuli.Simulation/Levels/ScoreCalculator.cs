using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.MarketData;

namespace IamZhuli.Simulation.Levels;

/// <summary>积分结算结果。</summary>
public sealed record ScoreResult(
    decimal ReturnRate,           // 收益率
    decimal MaxDrawdown,          // 最大回撤
    decimal RiskAdjustedScore,    // 最终得分(风险调整后)
    int Rank,                     // 三方排名(1=第一)
    int TotalPlayers,             // 总参与方数
    string Comment);              // AI教练评价

/// <summary>
/// 积分计算器。收益率 + 风险调整(最大回撤/波动率/监管值惩罚)。
/// 三方(玩家/AI/机构B)对比排名。
/// </summary>
public sealed class ScoreCalculator
{
    /// <summary>计算单个账户的得分。</summary>
    public ScoreResult Calculate(
        Account account, IReadOnlyList<decimal> equityCurve,
        decimal initialCash, Price? lastPrice, decimal maxRegulatorHeat)
    {
        decimal equity = account.TotalEquity(lastPrice ?? new Price(10m));
        decimal returnRate = initialCash > 0 ? (equity - initialCash) / initialCash : 0;
        decimal maxDd = EquityCurveCollector.MaxDrawdown(equityCurve);
        decimal vol = EquityCurveCollector.Volatility(equityCurve);
        // 监管惩罚:关注值峰值越高扣越多(0~100 → 0~0.3惩罚)
        decimal regPenalty = maxRegulatorHeat / 100m * 0.3m;

        // 风险调整:收益率 × (1 - 回撤×0.5 - 波动×0.2 - 监管惩罚)
        decimal riskAdjust = 1m - maxDd * 0.5m - vol * 0.2m - regPenalty;
        riskAdjust = Math.Max(0.1m, riskAdjust);   // 最低0.1,不归零
        decimal score = returnRate * riskAdjust;

        string comment = GenerateComment(returnRate, maxDd, maxRegulatorHeat, score);
        return new ScoreResult(returnRate, maxDd, score, 0, 0, comment);   // Rank后续填
    }

    /// <summary>三方对比排名。返回带排名的结果列表。</summary>
    public List<(string Name, ScoreResult Result)> Rank(
        (string Name, ScoreResult Result) player,
        (string Name, ScoreResult Result) ai,
        (string Name, ScoreResult Result) instB)
    {
        var all = new[] { player, ai, instB }
            .OrderByDescending(x => x.Result.RiskAdjustedScore)
            .ToList();
        return all.Select((x, i) => (x.Name, x.Result with { Rank = i + 1, TotalPlayers = 3 })).ToList();
    }

    private static string GenerateComment(decimal ret, decimal dd, decimal heat, decimal score)
    {
        if (score > 0.15m) return $"出色操盘!收益率{ret:P1},回撤{dd:P1}可控,监管关注{heat:F0}%。主力级水平。";
        if (score > 0.05m) return $"稳健。收益率{ret:P1},但回撤{dd:P1}偏大,下次注意风控。";
        if (score > 0m) return $"微利{ret:P1}。操盘偏保守,可以更主动些。";
        if (ret > -0.05m) return $"基本持平{ret:P1}。需要找到更好的进场时机。";
        return $"亏损{ret:P1}。回撤{dd:P1},监管关注{heat:F0}%。复盘一下哪里判断失误。";
    }
}
