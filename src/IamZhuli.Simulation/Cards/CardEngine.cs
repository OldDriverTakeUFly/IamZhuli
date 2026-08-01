using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Simulation.Cards;

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
    private const int TurnTicks = 30;          // 每回合30tick
    private const int MaxCardsPerTurn = 2;     // 每回合最多出2张牌
    private const int MaxEnergy = 10;          // 能量上限
    private const int EnergyPerTurn = 3;       // 每回合恢复3点能量

    private readonly Random _rng;
    private int _tickInTurn;                    // 当前回合内已过的tick
    private int _cardIdSeq;
    private readonly List<CardEffect> _recentPlays = new();  // 近期出牌(连招检测)
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
    /// <summary>上次触发的连招(供前端展示)。</summary>
    public string? LastCombo { get; private set; }

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
        // 准备抽牌堆:自配牌组 or 全牌库
        if (HasCustomDeck)
        {
            foreach (var c in _deck) _drawPile.Enqueue(c);
            Shuffle();
        }
        for (int i = 0; i < HandSize; i++) DrawCard();
        Phase = CardPhase.Play;
    }

    /// <summary>出牌。返回是否成功+错误信息。effectExecutor 执行实际效果。</summary>
    public bool PlayCard(int handIndex, Func<CardDefinition, int, bool> effectExecutor, out string error)
    {
        error = "";
        if (Phase != CardPhase.Play) { error = "当前不是出牌阶段"; return false; }
        if (handIndex < 0 || handIndex >= Hand.Count) { error = "无效的手牌索引"; return false; }
        if (CardsPlayedThisTurn >= MaxCardsPerTurn) { error = $"本回合最多出{MaxCardsPerTurn}张牌"; return false; }

        var card = Hand[handIndex];
        var def = card.Definition;

        // 验证能量
        if (Energy < def.EnergyCost) { error = $"能量不足(需要{def.EnergyCost},剩余{Energy})"; return false; }
        // 蛰伏冷却:不能出操盘牌
        if (IsLayingLow && def.Category == CardCategory.Trading) { error = "蛰伏冷却中,不能出操盘牌"; return false; }

        // 执行效果
        bool ok = effectExecutor(def, _cardIdSeq);
        if (!ok) { error = "效果执行失败(资金/持仓/券源不足)"; return false; }

        // 扣能量
        Energy -= def.EnergyCost;
        CardsPlayedThisTurn++;

        // 从手牌移除,放入弃牌堆,抽新牌
        Hand.RemoveAt(handIndex);
        if (HasCustomDeck) _discardPile.Add(def);
        DrawCard();

        // 记录出牌(连招检测)
        _recentPlays.Add(def.Effect);
        if (_recentPlays.Count > 10) _recentPlays.RemoveAt(0);

        // 蛰伏冷却标记
        if (def.Effect == CardEffect.LayLow) IsLayingLow = true;

        // 延迟效果注册(分批出货)
        if (def.DurationTicks > 0)
            PendingEffects.Add(new PendingEffect(def.Effect, def.DurationTicks, def.EffectValue, 1, 0));

        return true;
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
        return _tickInTurn >= TurnTicks;   // 回合结束
    }

    /// <summary>开始新回合:恢复能量、清状态、进入Play阶段。</summary>
    public void StartNewTurn()
    {
        Turn++;
        if (Turn > 4) Turn = 0;   // 跨日重置(一天5回合)
        Energy = Math.Min(MaxEnergy, Energy + EnergyPerTurn);
        CardsPlayedThisTurn = 0;
        IsLayingLow = false;
        Phase = CardPhase.Play;
        // 补满手牌
        while (Hand.Count < HandSize) DrawCard();
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
                _lastComboTick = Turn * TurnTicks + _tickInTurn;
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
            // 无自配牌组:全牌库随机(原行为)
            var def = CardDefinition.Library[_rng.Next(CardDefinition.Library.Count)];
            Hand.Add(new CardInstance(++_cardIdSeq, def));
        }
    }

    /// <summary>日切重置:回合归零,清延迟效果。</summary>
    public void OnNewDay()
    {
        Turn = 0;
        _tickInTurn = 0;
        PendingEffects.Clear();
        _recentPlays.Clear();
    }
}
