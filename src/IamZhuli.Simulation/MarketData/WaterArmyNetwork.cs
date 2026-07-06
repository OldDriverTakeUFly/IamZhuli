using IamZhuli.Simulation.Accounts;

namespace IamZhuli.Simulation.MarketData;

/// <summary>
/// 水军网络(舆论资源系统):玩家的持续运营资源,不是单次点击。
/// 升级需要一次性投入,运营每天扣维持费,运营期间自动注入 Pump 消息(累积推贪婪)。
/// 停止后效果逐渐消退。等级越高,单次效果越强、持续越长。
/// </summary>
public sealed class WaterArmyNetwork
{
    /// <summary>网络等级 0-5(0=无网络)。</summary>
    public int Level { get; private set; }
    /// <summary>是否在运营中(每天扣维持费+注入Pump)。</summary>
    public bool IsActive { get; private set; }
    /// <summary>已运营天数。</summary>
    public int ActiveDays { get; private set; }

    /// <summary>每日维持费 = 等级 × 1万。</summary>
    public decimal DailyCost => Level * 10000m;
    /// <summary>是否已建立网络(Level>0)。</summary>
    public bool HasNetwork => Level > 0;

    public WaterArmyNetwork() { }

    /// <summary>升级网络(一次性投入)。返回是否成功+花费。</summary>
    public bool TryUpgrade(out decimal cost)
    {
        if (Level >= 5) { cost = 0; return false; }
        Level++;
        cost = Level * 50000m;   // 1级5万,2级10万...5级25万
        return true;
    }

    /// <summary>启动运营(需已建立网络)。</summary>
    public bool Start()
    {
        if (!HasNetwork) return false;
        IsActive = true;
        return true;
    }

    /// <summary>停止运营(效果逐渐消退)。</summary>
    public void Stop() => IsActive = false;

    /// <summary>日切:扣维持费,运营中自动注入 Pump 消息。
    /// 返回 false 表示资金不足(需停止运营)。</summary>
    public bool OnNewDay(Account player, NewsSystem news)
    {
        if (!IsActive || !HasNetwork) return true;
        // 扣维持费
        if (player.AvailableCash < DailyCost)
        {
            IsActive = false;   // 资金不足,自动停止
            return false;
        }
        player.DebitCash(DailyCost);
        ActiveDays++;
        // 按等级注入 Pump 消息(等级越高效果越强、持续越长)
        var (baseImpact, baseDuration) = NewsSystem.GetDefaults(NewsType.Pump);
        decimal impactMult = Level switch { 1 => 0.6m, 2 => 0.8m, 3 => 1.0m, 4 => 1.2m, 5 => 1.5m, _ => 0.6m };
        int durationMult = Level switch { 1 => 200, 2 => 250, 3 => 300, 4 => 350, 5 => 400, _ => 200 };
        news.Publish(NewsType.Pump, $"水军网络Lv{Level}每日造势(运营第{ActiveDays}天)",
            baseImpact * impactMult, durationMult);
        return true;
    }
}
