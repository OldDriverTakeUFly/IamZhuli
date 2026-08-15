using IamZhuli.Presenter;
using IamZhuli.Simulation.Cards;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 内核测试脚手架:固定牌组(手牌可控)、按名出牌、回合驱动(含自动跨日/日切)。
/// 牌组每张牌给足副本,手牌永远由指定牌构成,测试与洗牌顺序无关。
/// </summary>
public static class Harness
{
    /// <summary>从牌库按名取定义。</summary>
    public static CardDefinition Card(string name) =>
        CardDefinition.Library.First(c => c.Name == name)
        ?? throw new ArgumentException($"未知卡牌: {name}");

    /// <summary>新开一局:加载关卡→设固定牌组→开始操盘。不指定 deckCards 则用全牌库随机。</summary>
    public static GameCore NewGame(string levelId, params string[] deckCards)
    {
        var core = new GameCore();
        core.LoadLevelById(levelId);
        if (deckCards.Length > 0)
        {
            var deck = new List<string>();
            int copies = Math.Max(2, 30 / deckCards.Length);
            // 交错排布(A,B,A,B,...):固定种子的洗牌序列不会把同一种牌聚到一手
            for (int i = 0; i < copies; i++)
                foreach (var name in deckCards)
                    deck.Add(name);
            if (!core.SetDeck(deck.Take(30).ToList(), out var err))
                throw new InvalidOperationException($"SetDeck 失败: {err}");
        }
        core.BeginTrading();
        return core;
    }

    /// <summary>最近一次出牌的错误信息(诊断用)。</summary>
    public static string LastPlayError = "";

    /// <summary>按名出牌(手牌中查找)。返回是否成功(能量/规则不满足返回 false)。</summary>
    public static bool Play(GameCore core, string cardName)
    {
        var hand = core.BuildSnapshot().CardState!.Hand;
        for (int i = 0; i < hand.Count; i++)
            if (hand[i].Name == cardName)
            {
                bool ok = core.PlayCard(i, out var err);
                LastPlayError = ok ? "" : $"{cardName}: {err}";
                return ok;
            }
        LastPlayError = $"{cardName}: 手牌中不存在(手牌={string.Join(",", hand.Select(h => h.Name))})";
        return false;
    }

    /// <summary>按优先级出牌(最多2张,能量允许范围内)。返回实际出牌数。</summary>
    public static int PlayByPriority(GameCore core, params string[] priority)
    {
        int played = 0;
        foreach (var name in priority)
        {
            if (played >= 2) break;
            if (Play(core, name)) played++;
        }
        return played;
    }

    /// <summary>结束当前回合并推进到下一回合出牌阶段。自动处理日切(T+1解锁/情绪衰减/水军日费)。</summary>
    public static MarketSnapshotDto NextTurn(GameCore core)
    {
        core.EndCardTurn();
        for (int i = 0; i < 800; i++)
        {
            if (core.Loop.IsFinished) return core.BuildSnapshot();
            if (core.Loop.IsDayClosed) { core.StartNextDay(); continue; }
            var s = core.Step();
            if (s == null) return core.BuildSnapshot();   // 关卡结束
            if (s.CardState != null && s.CardState.Phase == "Play") return s;
        }
        return core.BuildSnapshot();
    }

    /// <summary>推进 N 个回合(每回合可先执行自定义出牌策略)。</summary>
    public static List<MarketSnapshotDto> RunTurns(GameCore core, int turns, Action<GameCore>? policy = null)
    {
        var snaps = new List<MarketSnapshotDto>();
        for (int t = 0; t < turns && !core.Loop.IsFinished; t++)
        {
            policy?.Invoke(core);
            snaps.Add(NextTurn(core));
        }
        return snaps;
    }
}
