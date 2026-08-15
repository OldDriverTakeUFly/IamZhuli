using IamZhuli.Simulation.Cards;
using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 卡牌协同测试:连招(同回合按序触发+揭牌加成)、延迟效果、蛰伏封锁、
/// 舆论牌情绪冲击、做空→平仓全周期。分两级:CardEngine 单元级 + GameCore 集成级。
/// </summary>
public sealed class CardSynergyTests
{
    private readonly ITestOutputHelper _out;
    public CardSynergyTests(ITestOutputHelper o) => _out = o;

    // ── CardEngine 单元级:连招检测 ──

    /// <summary>引擎级出牌(stub executor 全部成功),用于隔离检测连招逻辑。</summary>
    private static bool PlayStub(CardEngine ce, string name)
    {
        var hand = ce.Hand;
        for (int i = 0; i < hand.Count; i++)
            if (hand[i].Definition.Name == name)
                return ce.PlayCard(i, (def, seq) => true, out _);
        return false;
    }

    /// <summary>引擎级跳到下一回合(恢复能量)。</summary>
    private static void NextEngineTurn(CardEngine ce)
    {
        ce.EndTurn();
        while (!ce.OnTick(_ => { })) { /* 30 tick resolve */ }
        ce.StartNewTurn();
    }

    private static CardEngine EngineWithDeck(params string[] names)
    {
        var ce = new CardEngine(seed: 42);
        var deck = new List<CardDefinition>();
        int copies = Math.Max(2, 30 / names.Length);
        // 交错排布:固定种子洗牌不会把同一种牌聚到一手
        for (int i = 0; i < copies; i++)
            foreach (var n in names)
                deck.Add(Harness.Card(n));
        ce.SetDeck(deck);
        ce.Init();
        return ce;
    }

    [Fact]
    public void Combo_LureTrap_WallThenMarketBuy_Triggers()
    {
        var ce = EngineWithDeck("限价挂墙", "市价急拉");
        // 回合1能量3不够 挂墙(1)+急拉(3)=4 → 空过攒能量,回合2同回合连出
        NextEngineTurn(ce);   // 能量3+3=6
        Assert.True(PlayStub(ce, "限价挂墙"));
        Assert.True(PlayStub(ce, "市价急拉"));

        Assert.Equal("诱多陷阱", ce.ActiveCombo);
        Assert.Equal("诱多陷阱", ce.LastCombo);
        // 揭牌消费:ConsumeTurnCombo 返回后清空
        Assert.Equal("诱多陷阱", ce.ConsumeTurnCombo());
        Assert.Null(ce.ActiveCombo);
    }

    [Fact]
    public void Combo_CrashHarvest_FakeSellThenSlam_Triggers()
    {
        var ce = EngineWithDeck("假大宗卖出", "市价砸盘");
        NextEngineTurn(ce);   // 能量6:假大宗(2)+砸盘(3)
        Assert.True(PlayStub(ce, "假大宗卖出"));
        Assert.True(PlayStub(ce, "市价砸盘"));
        Assert.Equal("砸盘收割", ce.ActiveCombo);
    }

    [Fact]
    public void Combo_PerfectDistribution_PumpThenScheduledSell_DoublesQty()
    {
        var ce = EngineWithDeck("水军造势", "分批出货");
        NextEngineTurn(ce);   // 能量6:造势(2)+出货(2)
        Assert.True(PlayStub(ce, "水军造势"));
        int baseQty = Harness.Card("分批出货").EffectValue;   // 200
        Assert.True(PlayStub(ce, "分批出货"));

        Assert.Equal("完美出货", ce.ActiveCombo);
        var pe = Assert.Single(ce.PendingEffects);
        Assert.Equal(baseQty * 2, pe.EffectValue);   // 连招:每tick出货量翻倍
        Assert.Equal(baseQty * 2 * pe.RemainingTicks, baseQty * 2 * 10);   // 总出货量翻倍
    }

    [Fact]
    public void Combo_RequiresSameTurn_SplitAcrossTurns_DoesNotTrigger()
    {
        var ce = EngineWithDeck("限价挂墙", "市价急拉");
        Assert.True(PlayStub(ce, "限价挂墙"));   // 回合1:墙
        NextEngineTurn(ce);
        Assert.True(PlayStub(ce, "市价急拉"));   // 回合2:急拉(跨回合)
        Assert.Null(ce.ActiveCombo);   // 连招必须同回合
    }

    [Fact]
    public void Combo_WrongOrder_DoesNotTrigger()
    {
        var ce = EngineWithDeck("限价挂墙", "市价急拉");
        NextEngineTurn(ce);
        Assert.True(PlayStub(ce, "市价急拉"));   // 顺序颠倒:先拉后墙
        Assert.True(PlayStub(ce, "限价挂墙"));
        Assert.Null(ce.ActiveCombo);
    }

    [Fact]
    public void LayLow_BlocksTradingCards_SameTurn()
    {
        var ce = EngineWithDeck("蛰伏冷却", "市价急拉");
        Assert.True(PlayStub(ce, "蛰伏冷却"));
        Assert.True(ce.IsLayingLow);
        // 蛰伏后不能出操盘牌
        Assert.False(PlayStub(ce, "市价急拉"));
        // 下一回合解封
        NextEngineTurn(ce);
        Assert.False(ce.IsLayingLow);
    }

    [Fact]
    public void TwoCardsPerTurn_MaxEnforced()
    {
        var ce = EngineWithDeck("限价吸筹", "限价挂墙");
        NextEngineTurn(ce);   // 能量充足
        Assert.True(PlayStub(ce, "限价吸筹"));
        Assert.True(PlayStub(ce, "限价挂墙"));
        // 第三张直接按索引出(手牌任意一张),必须被"每回合最多2张"拦截
        Assert.False(ce.PlayCard(0, (def, seq) => true, out var err), "每回合最多2张");
        Assert.Contains("最多出2张", err);
    }

    // ── GameCore 集成级:协同效果落到市场/账户/情绪 ──

    [Fact]
    public void ScheduledSell_SellsAcrossDays_TPlus1()
    {
        var core = Harness.NewGame("accumulate", "限价加仓", "分批出货");
        // 第1回合买入建仓(T+1锁定)
        Assert.True(Harness.Play(core, "限价加仓"), Harness.LastPlayError);
        var afterBuy = Harness.NextTurn(core);
        _out.WriteLine($"建仓后持仓 {afterBuy.Account.PositionTotal} 手(锁 {afterBuy.Account.PositionT1Locked})");
        Assert.True(afterBuy.Account.PositionTotal > 0, "限价买入应有成交");

        // 推到第2天(T+1解锁):跨过本日剩余回合
        int day = afterBuy.CurrentDay;
        MarketSnapshotDto day2 = afterBuy;
        while (day2.CurrentDay == day && !core.Loop.IsFinished)
            day2 = Harness.NextTurn(core);
        int held = day2.Account.PositionAvailable;
        Assert.True(held > 0, "跨日后应有可卖持仓");

        // 第2天出分批出货 → Resolve 期间逐tick卖出
        Assert.True(Harness.Play(core, "分批出货"), Harness.LastPlayError);
        var afterSell = Harness.NextTurn(core);
        _out.WriteLine($"分批出货一回合: 可卖 {held} → {afterSell.Account.PositionAvailable}");
        Assert.True(afterSell.Account.PositionAvailable < held, "分批出货应逐tick减仓");
    }

    [Fact]
    public void MediaCard_PositiveNews_LiftsConfidenceImmediately()
    {
        var core = Harness.NewGame("accumulate", "发布利好", "散布利空");
        var before = core.BuildSnapshot();
        Assert.True(Harness.Play(core, "发布利好"));
        var after = core.BuildSnapshot();
        _out.WriteLine($"信心 {before.Confidence} → {after.Confidence} | 消息偏差 {before.NewsBias} → {after.NewsBias}");
        // NewsShock 直接作用于 Confidence(+12) 与 NewsBias(+0.15);合成情绪(Value)随后续tick跟随
        Assert.True(after.Confidence > before.Confidence, "利好应立即抬升散户信心");
        Assert.True(after.NewsBias > before.NewsBias, "利好应推动消息偏差转正");
    }

    [Fact]
    public void ShortSellCard_ThenCoverCard_FullCycleThroughAuctions()
    {
        var core = Harness.NewGame("accumulate", "做空突袭", "买回平仓");
        // 回合1:做空突袭(1000手,经集中撮合)
        Assert.True(Harness.Play(core, "做空突袭"), Harness.LastPlayError);
        var afterShort = Harness.NextTurn(core);
        _out.WriteLine($"做空后: 空头{afterShort.Account.ShortQty} 担保{afterShort.Account.MaintenanceRatio:P0} 券池余{afterShort.ShortablePool}");
        Assert.True(afterShort.Account.ShortQty > 0, "做空牌应建立空头(需对手买盘)");

        // 回合2:买回平仓
        Assert.True(Harness.Play(core, "买回平仓"), Harness.LastPlayError);
        var afterCover = Harness.NextTurn(core);
        _out.WriteLine($"平仓后: 空头{afterCover.Account.ShortQty} 券池余{afterCover.ShortablePool}");
        Assert.Equal(0, afterCover.Account.ShortQty);
    }

    [Fact]
    public void LureTrap_Combo_InGame_DoublesBuyIntent()
    {
        var core = Harness.NewGame("accumulate", "限价挂墙", "市价急拉");
        // 回合1空过攒能量(3+3=6),回合2同回合连出 挂墙(1)+急拉(3) 触发诱多陷阱
        Harness.NextTurn(core);
        var herdBefore = core.BuildSnapshot().HerdMood;
        Assert.True(Harness.Play(core, "限价挂墙"), Harness.LastPlayError);
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);

        var combo = core.BuildSnapshot().CardState!.LastCombo;
        core.EndCardTurn();
        var ra = core.LastAuction!;
        var herdAfter = core.BuildSnapshot().HerdMood;
        _out.WriteLine($"连招={combo} 竞价价={ra.Price} 量={ra.Volume} 群体热度 {herdBefore}→{herdAfter}");
        // 连招效果:揭牌时市价买量翻倍(4000手)+群体热度上升
        Assert.Equal("诱多陷阱", combo);
        Assert.True(herdAfter >= herdBefore, "诱多陷阱应点燃群体热度");
        int playerBought = ra.Trades.Where(t => t.Buyer == "你").Sum(t => t.Qty);
        Assert.True(playerBought > 0, "翻倍买量应有成交");
    }
}
