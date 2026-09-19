using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Simulation.Cards;

/// <summary>操盘意图(战略层):跨段持续的基础盘方向,免费且不占出牌数。</summary>
public enum TurnIntent
{
    None,        // 未设定
    Accumulate,  // 吸筹:买一下方挂800手(安静收集)
    Push,        // 拉升:市价买800手
    Wash,        // 洗盘:上压600+下托600(双向夹)
    Distribute,  // 出货:卖一队尾挂800手
    Defend,      // 防御:±0.03小墙各600
}

/// <summary>订单意图(回合出牌收集,揭牌时统一撮合)。</summary>
public record OrderIntent(
    ParticipantId Participant, Side Side, OrderType Type,
    decimal Price, int Qty, bool IsShort = false);

/// <summary>
/// 卡牌引擎:管理回合/能量/手牌/出牌验证/延迟效果执行/连招检测。
/// GameSingleton 每帧调用 OnTick 推进;玩家出牌时调用 PlayCard。
/// </summary>
public sealed class CardEngine
{
    private const int HandSize = 5;           // 手牌上限
    private const int MaxCardsPerTurn = 2;     // 每回合最多出2张牌

    // ── 12段交易日:盘前申报10/盘前锁单竞价10/盘中8×20/尾盘申报12/收盘竞价8 ──
    // 盘前段1(可挂可撤,9:15-9:20模拟)+段2(锁单,9:20-9:25模拟);
    // 尾盘申报(14:57)+收盘竞价锁收盘价。总和=200tick/天,须与Clock对齐。
    public static readonly int[] TurnDurations = { 10, 10, 20, 20, 20, 20, 20, 20, 20, 20, 12, 8 };
    /// <summary>一天总段数。</summary>
    public static int TurnsPerDay => TurnDurations.Length;
    /// <summary>一天总tick(各段时长之和)。</summary>
    public static int TotalTicksPerDay => TurnDurations.Sum();
    /// <summary>当前段时长。</summary>
    public int CurrentTurnTicks => TurnDurations[Math.Min(Turn, TurnsPerDay - 1)];
    private const int MaxEnergy = 12;          // 能量上限(允许攒一次暴力拉升+架势+急拉的爆发)
    private const int EnergyPerTurn = 4;       // 每段恢复4点(日收入48,覆盖全程2张中等牌的节奏)

    private readonly Random _rng;
    private int _tickInTurn;                    // 当前回合内已过的tick
    private int _cardIdSeq;
    private readonly List<CardEffect> _recentPlays = new();  // 近期出牌(展示用)
    private readonly List<CardEffect> _turnPlays = new();    // 本回合出牌(连招检测,回合切换清空)
    private int _lastComboTick = -100;          // 上次连招触发的tick

    /// <summary>当前阶段(Play=出牌冻结,Resolve=执行中)。</summary>
    public CardPhase Phase { get; private set; } = CardPhase.Play;
    /// <summary>当前能量。</summary>
    public int Energy { get; private set; } = EnergyPerTurn;
    /// <summary>当前回合(一天0-4)。</summary>
    public int Turn { get; private set; }
    /// <summary>手牌。</summary>
    public List<CardInstance> Hand { get; } = new();
    /// <summary>本回合已出牌数。</summary>
    public int CardsPlayedThisTurn { get; private set; }
    /// <summary>待执行的延迟效果(分批出货等)。</summary>
    public List<PendingEffect> PendingEffects { get; } = new();
    /// <summary>蛰伏冷却标记(本回合不能出操盘牌)。</summary>
    public bool IsLayingLow { get; private set; }
    /// <summary>当前架势(架势牌设置,揭牌时消费变体,回合切换清空)。</summary>
    public CardEffect ActiveStance { get; set; } = CardEffect.None;

    // —— 牌随势走: 意图偏置抽牌 + 方向保底(默认牌库模式;自配牌组尊重玩家选择) ——
    /// <summary>抽牌偏置方向(由 GameCore 按操盘意图同步;null=无偏置)。</summary>
    public Side? DrawBias { get; set; }

    /// <summary>牌是否属于指定方向(买=吸筹/拉升/平仓;卖=砸盘/出货/做空)。</summary>
    public static bool IsDirectionCard(CardDefinition c, Side side) => side switch
    {
        Side.Buy => c.Effect is CardEffect.LimitBuy or CardEffect.MarketBuy or CardEffect.ShortCover,
        Side.Sell => c.Effect is CardEffect.MarketSell or CardEffect.ScheduledSell or CardEffect.ShortSell,
        _ => false,
    };

    /// <summary>方向保底: 手牌无偏置方向牌时强制补入一张(设意图时立即生效,不等下段抽牌)。</summary>
    public void EnsureDirectionFloor()
    {
        if (DrawBias is not { } side) return;
        if (HasCustomDeck) return;                     // 自配牌组尊重组牌
        if (Hand.Any(c => IsDirectionCard(c.Definition, side))) return;
        var pool = CardDefinition.Library.Where(c => IsDirectionCard(c, side)).ToList();
        if (pool.Count == 0 || Hand.Count == 0) return;
        Hand[^1] = new CardInstance(++_cardIdSeq, pool[_rng.Next(pool.Count)]);   // 替换最后一张
    }
    /// <summary>上次触发的连招(供前端展示)。</summary>
    public string? LastCombo { get; private set; }
    /// <summary>本回合触发的连招名(同回合按序打出连招序列才触发;揭牌时消费加成后清空)。</summary>
    public string? ActiveCombo { get; private set; }

    /// <summary>本回合收集的订单意图(玩家+AI+散户),揭牌时统一撮合。</summary>
    public List<OrderIntent> PendingIntents { get; } = new();

    /// <summary>自配牌组(玩家从牌库挑选的牌,可重复)。为空则用全牌库随机。</summary>
    private List<CardDefinition> _deck = new();
    /// <summary>牌组抽牌队列(洗牌后的顺序)。</summary>
    private Queue<CardDefinition> _drawPile = new();
    /// <summary>弃牌堆(用过的牌)。</summary>
    private List<CardDefinition> _discardPile = new();
    /// <summary>是否使用自配牌组。</summary>
    public bool HasCustomDeck => _deck.Count > 0;

    /// <summary>延迟效果定义。</summary>
    public sealed record PendingEffect(CardEffect Effect, int RemainingTicks, int EffectValue, int TickInterval, int TicksSinceLast);

    /// <summary>连招表。</summary>
    public static readonly IReadOnlyList<ComboDefinition> Combos = new List<ComboDefinition>
    {
        new("诱多陷阱", new[] { CardEffect.LimitWall, CardEffect.MarketBuy },
            "挂墙+拉升=诱多陷阱!推价效果额外+5%,监管减半"),
        new("砸盘收割", new[] { CardEffect.SignalFakeSell, CardEffect.MarketSell },
            "假大宗卖出+砸盘=砸盘收割!砸盘量翻倍"),
        new("完美出货", new[] { CardEffect.NewsPump, CardEffect.ScheduledSell },
            "水军造势+分批出货=完美出货!出货速度翻倍,监管不增"),
    };

    public CardEngine(int? seed = null)
    {
        _rng = new Random(seed ?? Environment.TickCount);
    }

    /// <summary>设置自配牌组(从牌库挑选的牌列表,可重复同一张)。</summary>
    public void SetDeck(List<CardDefinition> deck)
    {
        _deck = deck;
        _drawPile.Clear();
        _discardPile.Clear();
    }

    /// <summary>洗牌:把弃牌堆+剩余牌重新打乱放入抽牌队列。</summary>
    private void Shuffle()
    {
        var all = new List<CardDefinition>(_drawPile);
        all.AddRange(_discardPile);
        _discardPile.Clear();
        _drawPile.Clear();
        // Fisher-Yates 洗牌
        for (int i = all.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }
        foreach (var c in all) _drawPile.Enqueue(c);
    }

    /// <summary>初始化:洗牌、抽起始手牌、进入第一回合Play阶段。</summary>
    public void Init()
    {
        Turn = 0;
        Energy = EnergyPerTurn;
        _tickInTurn = 0;
        CardsPlayedThisTurn = 0;
        IsLayingLow = false;
        Hand.Clear();
        _drawPile.Clear();
        _discardPile.Clear();
        _turnPlays.Clear();
        ActiveCombo = null;
        ActiveStance = CardEffect.None;
        // 准备抽牌堆:自配牌组 or 全牌库
        if (HasCustomDeck)
        {
            foreach (var c in _deck) _drawPile.Enqueue(c);
            Shuffle();
        }
        for (int i = 0; i < HandSize; i++) DrawCard();
        EnsureDirectionFloor();
        Phase = CardPhase.Play;
    }

    /// <summary>出牌。返回是否成功+错误信息。effectExecutor 执行实际效果。</summary>
    public bool PlayCard(int handIndex, Func<CardDefinition, int, bool> effectExecutor, out string error)
    {
        error = "";
        if (Phase != CardPhase.Play) { error = "当前不是出牌阶段"; return false; }
        if (handIndex < 0 || handIndex >= Hand.Count) { error = "无效的手牌索引"; return false; }

        var card = Hand[handIndex];
        var def = card.Definition;
        bool isStance = def.Category == CardCategory.Stance;

        // 出牌数上限:架势牌不占限额(先架势再双操盘是合法连招)
        if (!isStance && CardsPlayedThisTurn >= MaxCardsPerTurn) { error = $"本回合最多出{MaxCardsPerTurn}张牌"; return false; }

        // 验证能量
        if (Energy < def.EnergyCost) { error = $"能量不足(需要{def.EnergyCost},剩余{Energy})"; return false; }
        // 蛰伏冷却:不能出操盘牌
        if (IsLayingLow && def.Category == CardCategory.Trading) { error = "蛰伏冷却中,不能出操盘牌"; return false; }

        // 执行效果
        bool ok = effectExecutor(def, _cardIdSeq);
        if (!ok) { error = "效果执行失败(资金/持仓/券源不足)"; return false; }

        // 扣能量;架势牌设置当前架势且不占出牌数
        Energy -= def.EnergyCost;
        if (isStance) ActiveStance = def.Effect;
        else CardsPlayedThisTurn++;

        // 从手牌移除,放入弃牌堆,抽新牌
        Hand.RemoveAt(handIndex);
        if (HasCustomDeck) _discardPile.Add(def);
        DrawCard();

        // 记录出牌(连招检测;架势牌不进入序列,不打断连招)
        _recentPlays.Add(def.Effect);
        if (_recentPlays.Count > 10) _recentPlays.RemoveAt(0);
        if (!isStance) _turnPlays.Add(def.Effect);

        // 蛰伏冷却标记
        if (def.Effect == CardEffect.LayLow) IsLayingLow = true;

        // 延迟效果注册(分批出货)
        if (def.DurationTicks > 0)
            PendingEffects.Add(new PendingEffect(def.Effect, def.DurationTicks, def.EffectValue, 1, 0));

        // 连招检测(同回合按序)——完美出货:分批出货量翻倍
        DetectTurnCombo();
        if (ActiveCombo == "完美出货" && def.DurationTicks > 0 && PendingEffects.Count > 0)
        {
            var last = PendingEffects[^1];
            PendingEffects[^1] = last with { EffectValue = last.EffectValue * 2 };
        }

        return true;
    }

    /// <summary>检测本回合连招:回合内出牌序列末尾匹配连招序列即触发。</summary>
    private void DetectTurnCombo()
    {
        foreach (var combo in Combos)
        {
            if (_turnPlays.Count < combo.RequiredSequence.Length) continue;
            int start = _turnPlays.Count - combo.RequiredSequence.Length;
            bool match = true;
            for (int i = 0; i < combo.RequiredSequence.Length; i++)
            {
                if (_turnPlays[start + i] != combo.RequiredSequence[i]) { match = false; break; }
            }
            if (match)
            {
                ActiveCombo = combo.Name;
                LastCombo = combo.Name;
                return;
            }
        }
    }

    /// <summary>消费本回合连招(揭牌加成用完后清空,下回合重新检测)。</summary>
    public string? ConsumeTurnCombo()
    {
        var combo = ActiveCombo;
        ActiveCombo = null;
        return combo;
    }

    /// <summary>玩家结束回合 → 进入执行阶段。</summary>
    public void EndTurn()
    {
        Phase = CardPhase.Resolve;
        _tickInTurn = 0;
    }

    /// <summary>每tick调用(Resolve阶段)。处理延迟效果+回合计时。
    /// resolveEffect 执行延迟效果(如分批卖出)。返回true=回合结束需进入下一回合。</summary>
    public bool OnTick(Action<PendingEffect> resolveEffect)
    {
        if (Phase != CardPhase.Resolve) return false;

        // 处理延迟效果
        foreach (var pe in PendingEffects.ToArray())
        {
            int newSinceLast = pe.TicksSinceLast + 1;
            if (newSinceLast >= pe.TickInterval)
            {
                resolveEffect(pe);
                newSinceLast = 0;
            }
            var updated = pe with { RemainingTicks = pe.RemainingTicks - 1, TicksSinceLast = newSinceLast };
            PendingEffects.Remove(pe);
            if (updated.RemainingTicks > 0) PendingEffects.Add(updated);
        }

        _tickInTurn++;
        return _tickInTurn >= CurrentTurnTicks;   // 回合结束
    }

    /// <summary>开始新回合:恢复能量、清状态、进入Play阶段。</summary>
    public void StartNewTurn()
    {
        Turn++;
        if (Turn > TurnsPerDay - 1) Turn = 0;   // 跨日重置
        Energy = Math.Min(MaxEnergy, Energy + EnergyPerTurn);
        CardsPlayedThisTurn = 0;
        IsLayingLow = false;
        _turnPlays.Clear();
        ActiveCombo = null;
        ActiveStance = CardEffect.None;
        Phase = CardPhase.Play;
        // 补满手牌(自配牌组总张数<手牌上限时抽无可抽,必须防死循环)
        while (Hand.Count < HandSize)
        {
            int before = Hand.Count;
            DrawCard();
            if (Hand.Count == before) break;   // 小牌组耗尽:有多少拿多少
        }
        EnsureDirectionFloor();
    }

    /// <summary>检测连招。返回触发的连招名(null=无)。</summary>
    public string? CheckCombo()
    {
        foreach (var combo in Combos)
        {
            // 检查 recentPlays 末尾是否匹配连招序列
            if (_recentPlays.Count < combo.RequiredSequence.Length) continue;
            int start = _recentPlays.Count - combo.RequiredSequence.Length;
            bool match = true;
            for (int i = 0; i < combo.RequiredSequence.Length; i++)
            {
                if (_recentPlays[start + i] != combo.RequiredSequence[i]) { match = false; break; }
            }
            if (match)
            {
                LastCombo = combo.Name;
                _lastComboTick = Turn * 20 + _tickInTurn;   // 近似刻度(仅展示用)
                return combo.Name;
            }
        }
        return null;
    }

    private void DrawCard()
    {
        if (Hand.Count >= HandSize) return;
        // 自配牌组模式:从抽牌堆抽,空了则洗弃牌堆
        if (HasCustomDeck)
        {
            if (_drawPile.Count == 0)
            {
                if (_discardPile.Count == 0) return;   // 牌组耗尽
                Shuffle();   // 弃牌堆洗回
            }
            var def = _drawPile.Dequeue();
            Hand.Add(new CardInstance(++_cardIdSeq, def));
        }
        else
        {
            // 无自配牌组:全牌库加权随机——意图方向牌权重×3(牌随势走)
            var lib = CardDefinition.Library;
            CardDefinition def = lib[0];
            if (DrawBias is { } bias)
            {
                int total = 0;
                Span<int> w = stackalloc int[lib.Count];
                for (int i = 0; i < lib.Count; i++)
                {
                    w[i] = IsDirectionCard(lib[i], bias) ? 3 : 1;
                    total += w[i];
                }
                int pick = _rng.Next(total);
                for (int i = 0; i < lib.Count; i++)
                {
                    pick -= w[i];
                    if (pick < 0) { def = lib[i]; break; }
                }
                def ??= lib[^1];
            }
            else def = lib[_rng.Next(lib.Count)];
            Hand.Add(new CardInstance(++_cardIdSeq, def));
        }
    }

    /// <summary>存档恢复:整体回填牌组状态(手牌/抽牌堆/弃牌堆/能量/意图偏置)。</summary>
    internal void RestoreState(List<CardDefinition> hand, Queue<CardDefinition> draw, List<CardDefinition> discard,
                               int energy, int turn, CardPhase phase)
    {
        Hand.Clear(); hand.ForEach(c => Hand.Add(new CardInstance(++_cardIdSeq, c)));
        _drawPile = new Queue<CardDefinition>(draw);
        _discardPile = new List<CardDefinition>(discard);
        Energy = energy; Turn = turn; Phase = phase;
        CardsPlayedThisTurn = 0; IsLayingLow = false;
        _turnPlays.Clear(); ActiveCombo = null; ActiveStance = CardEffect.None;
        PendingEffects.Clear();
    }

    /// <summary>内部牌堆快照(存档导出用)。</summary>
    internal (List<CardDefinition> hand, Queue<CardDefinition> draw, List<CardDefinition> discard) PilesSnapshot()
        => (Hand.Select(h => h.Definition).ToList(), new Queue<CardDefinition>(_drawPile), _discardPile.ToList());

    /// <summary>日切重置:回合归零(次日盘前竞价),恢复能量/出牌数/Play阶段。
    /// 收盘静默路径不经过 StartNewTurn,这里必须做完整重置。</summary>
    public void OnNewDay()
    {
        Turn = 0;
        _tickInTurn = 0;
        PendingEffects.Clear();
        _recentPlays.Clear();
        _turnPlays.Clear();
        ActiveCombo = null;
        ActiveStance = CardEffect.None;
        CardsPlayedThisTurn = 0;
        IsLayingLow = false;
        Energy = Math.Min(MaxEnergy, Energy + EnergyPerTurn);
        Phase = CardPhase.Play;
        while (Hand.Count < HandSize)
        {
            int before = Hand.Count;
            DrawCard();
            if (Hand.Count == before) break;
        }
    }
}
