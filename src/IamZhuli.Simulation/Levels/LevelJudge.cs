using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;

namespace IamZhuli.Simulation.Levels;

/// <summary>单个目标的达成情况。</summary>
public readonly record struct ObjectiveResult(string Description, bool Achieved, decimal Progress, string Detail);

/// <summary>关卡结算结果。</summary>
public sealed class LevelResult
{
    public bool IsVictory { get; init; }
    public int Stars { get; init; }          // 0~3
    public List<ObjectiveResult> Objectives { get; init; } = new();
    public string CoachComment { get; init; } = "";   // AI 教练结业评价
    public string FailureReason { get; init; } = "";
}

/// <summary>
/// 关卡判定器。评估目标达成、计算三星评分、生成 AI 教练结业评价。
/// </summary>
public sealed class LevelJudge
{
    private readonly LevelDefinition _level;

    public LevelJudge(LevelDefinition level) => _level = level;

    /// <summary>评估当前是否达成所有目标(用于实时进度)。</summary>
    public List<ObjectiveResult> EvaluateProgress(
        Price? lastPrice, Account player, int floatShares, decimal maxHeatReached)
        => _level.Objectives.Select(o => EvaluateOne(o, lastPrice, player, floatShares, maxHeatReached)).ToList();

    private static ObjectiveResult EvaluateOne(Objective o, Price? lastPrice, Account player, int floatShares, decimal maxHeatReached)
    {
        return o.Type switch
        {
            ObjectiveType.ReachPrice => new(o.Description,
                lastPrice is { } p && p.Value >= o.TargetPrice,
                lastPrice is { } p2 ? Math.Min(1m, p2.Value / o.TargetPrice) : 0,
                lastPrice is { } p3 ? $"当前{p3} / 目标{o.TargetPrice}" : "尚未有成交"),
            ObjectiveType.DistributeAtHigh => EvaluateDistribute(o, player),
            ObjectiveType.DefendPrice => new(o.Description,
                lastPrice is { } p && p.Value >= o.TargetPrice,
                lastPrice is { } p2 ? (p2.Value >= o.TargetPrice ? 1m : p2.Value / o.TargetPrice) : 0,
                lastPrice is { } p3 ? $"现价{p3}需≥{o.TargetPrice}" : "无成交"),
            ObjectiveType.AccumulateQuietly => EvaluateAccumulate(o, player, floatShares, maxHeatReached),
            _ => new(o.Description, false, 0, "未知目标")
        };
    }

    private static ObjectiveResult EvaluateDistribute(Objective o, Account player)
    {
        // 出货比例:用"累计卖出量/累计买入量"近似(POC);更精确需跟踪累计成交流
        // 简化:若玩家持仓低于初始买入量的(1-TargetRatio)视为已出货
        decimal totalBought = player.TotalBoughtQty;   // 需 Account 暴露
        decimal sold = totalBought - player.Position.Total.Value;
        decimal ratio = totalBought > 0 ? sold / totalBought : 0;
        return new(o.Description, ratio >= o.TargetRatio, Math.Min(1m, ratio / o.TargetRatio),
            $"已出货{ratio:P0} / 目标{o.TargetRatio:P0}");
    }

    private static ObjectiveResult EvaluateAccumulate(Objective o, Account player, int floatShares, decimal maxHeatReached)
    {
        int holding = player.Position.Total.Value;
        decimal ratio = floatShares > 0 ? (decimal)holding / floatShares : 0;
        bool heatOk = maxHeatReached <= o.MaxHeat;
        bool achieved = ratio >= o.TargetRatio && heatOk;
        return new(o.Description, achieved, Math.Min(1m, ratio / o.TargetRatio),
            $"持仓{ratio:P0} / 目标{o.TargetRatio:P0},最高关注{maxHeatReached:F0}%(限{o.MaxHeat:F0}%)");
    }

    /// <summary>结算关卡(时间结束或主动结束)。判定胜负、星级、教练评价。</summary>
    public LevelResult Settle(
        Price? lastPrice, Account player, int floatShares,
        decimal maxHeatReached, decimal initialCash, bool failedByRegulator)
    {
        if (failedByRegulator)
        {
            return new LevelResult
            {
                IsVictory = false, Stars = 0,
                FailureReason = "监管关注值满100%,被强制平仓",
                CoachComment = "太激进了。对倒和虚假挂单是监管的红线,你需要更隐蔽地操作。下次试试分散下单、控制节奏。"
            };
        }

        var results = EvaluateProgress(lastPrice, player, floatShares, maxHeatReached);
        bool allAchieved = results.All(r => r.Achieved);

        if (!allAchieved)
        {
            var miss = results.Where(r => !r.Achieved).Select(r => r.Description);
            return new LevelResult
            {
                IsVictory = false, Stars = 0,
                Objectives = results,
                FailureReason = "未达成目标: " + string.Join("; ", miss),
                CoachComment = GenerateCoachComment(results, maxHeatReached, player, initialCash, false)
            };
        }

        // 胜利:计算星级
        int stars = 1;   // 基础达成=1星
        decimal equity = player.TotalEquity(lastPrice ?? new Price(_level.IntrinsicValue));
        if (equity > initialCash) stars++;   // 盈利=2星
        if (maxHeatReached <= 40m) stars++;  // 操纵优雅(关注值低位)=3星
        stars = Math.Min(3, stars);

        return new LevelResult
        {
            IsVictory = true, Stars = stars,
            Objectives = results,
            CoachComment = GenerateCoachComment(results, maxHeatReached, player, initialCash, true)
        };
    }

    private string GenerateCoachComment(List<ObjectiveResult> results, decimal maxHeat, Account player, decimal initial, bool win)
    {
        if (!win)
        {
            if (maxHeat >= 80m) return "你被监管盯上了。操纵要像呼吸一样自然——分散、缓慢、有耐心。";
            return "目标没达成。复盘一下:是时机不对,还是力度不够?主力操盘是艺术,急不得。";
        }
        string c = "干得漂亮!";
        if (maxHeat <= 30m) c += "全程监管关注值控制得极低,操作非常隐蔽优雅。";
        else if (maxHeat <= 60m) c += "监管虽有关注但在可控范围,节奏把握不错。";
        else c += "虽然达成了目标,但监管关注值偏高,下次可以更隐蔽些。";
        decimal equity = player.Cash > 0 ? player.Cash : initial;
        if (equity > initial * 1.1m) c += " 而且盈利可观,这才是主力的水平。";
        return c;
    }
}
