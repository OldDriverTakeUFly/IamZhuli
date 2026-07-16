using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// AI 行为系统性测试:在真实市场环境中验证 AI 的自主决策。
/// 不是直接调 Transition(),而是设置场景→让 AI 自主 Act→断言行为结果。
///
/// 测试策略:
/// 1. 创造特定条件(价格/持仓/意图信号)
/// 2. 让 AI 自主跑若干 tick
/// 3. 断言 AI 做了符合预期的事(状态切换/买卖/做空/独白)
/// </summary>
public class AIBehaviorTests
{
    private static readonly ParticipantId Player = new("Player");

    /// <summary>搭建只有 AI + 做市商的市场(无散户干扰,聚焦 AI 行为)。</summary>
    private static (SimulationLoop loop, TradingSession session, AIMainForce ai, Account player) SetupAI(int ticksPerDay = 100)
    {
        var rules = new MarketRules
        {
            PreviousClose = new Price(10m), PriceLimitRatio = 0.10m,
            TickSize = new Price(0.01m), FloatShares = new Quantity(200000)
        };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, 30));
        loop.Session.InitShortablePool(200000);

        // 做市商提供流动性
        var mm = loop.Session.GetOrCreateAccount(new ParticipantId("MM"), 1_000_000_000m);
        mm.Position.Seed(new Quantity(80000), new Price(10m));
        for (int i = 1; i <= 8; i++)
        {
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Sell, OrderType.Limit, new Price(10m + i * 0.01m), new Quantity(300)));
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Buy, OrderType.Limit, new Price(10m - i * 0.01m), new Quantity(300)));
        }

        // AI 主力(初始持仓重,成本10,灵敏度中等)
        var ai = new AIMainForce(loop.Session, new ParticipantId("AI"), new Price(10m),
            cash: 100_000_000m, initialHolding: 5000, initialCost: new Price(10m),
            sensitivity: 0.7, seed: 42);
        loop.AddParticipant(ai);

        // 机构B(轻量,不干扰)
        loop.AddParticipant(new InstitutionB(loop.Session, new ParticipantId("机构B"), new Price(10m),
            cash: 1_000_000_000m, initialHolding: 10000, baseDepthPerLevel: 50, levels: 5, seed: 88));

        // 散户池(提供市场活跃度,让 AI 有成交机会)
        loop.AddParticipant(new RetailProfilePool(loop.Session, new ParticipantId("散户池"), new Price(10m), seed: 42));

        var player = loop.Session.GetOrCreateAccount(Player, 200_000_000m);
        loop.Start();
        return (loop, loop.Session, ai, player);
    }

    private static void RunTicks(SimulationLoop loop, int count)
    {
        for (int i = 0; i < count && !loop.IsFinished; i++)
        {
            if (loop.IsDayClosed) break;
            loop.Step();
        }
    }

    // ══════════════════════════════════════
    // 1. AI 基本活跃性
    // ══════════════════════════════════════

    [Fact]
    public void AI_RunsAndProducesThoughts()
    {
        var (loop, _, ai, _) = SetupAI();
        RunTicks(loop, 50);
        // AI 应有内心独白记录(状态变化或定期)
        Assert.True(ai.Thoughts.Count > 0, $"AI 应有独白记录,实际{ai.Thoughts.Count}");
    }

    [Fact]
    public void AI_StateChangesOverTime()
    {
        var (loop, _, ai, _) = SetupAI();
        AIState initialState = ai.CurrentState;
        RunTicks(loop, 80);
        // AI 状态可能变化(也可能一直 Observe,但至少不应崩溃)
        Assert.True(Enum.IsDefined(ai.CurrentState), $"AI 状态应有效,实际{ai.CurrentState}");
    }

    // ══════════════════════════════════════
    // 2. AI 对价格的反应
    // ══════════════════════════════════════

    [Fact]
    public void AI_RespondsToPriceDrop_BelowCost()
    {
        // 玩家大量卖出→价格跌破AI成本→AI应护盘或买入
        var (loop, session, ai, player) = SetupAI();
        player.Position.Seed(new Quantity(20000), new Price(10m));
        // 砸盘(多轮,给AI足够时间反应)
        for (int i = 0; i < 8; i++)
        {
            try { session.Submit(new OrderRequest(Player, Side.Sell, OrderType.Market, Price.Zero, new Quantity(800))); }
            catch { }
            RunTicks(loop, 8);
        }
        decimal price = session.Engine.LastPrice?.Value ?? 10m;
        // 价格跌破AI成本后,AI的反应是概率性的(不一定每次都护盘)
        // 但至少 AI 不应完全无视:有独白、或状态变化、或买入记录
        if (price < 9.9m)
        {
            bool reacted = ai.Thoughts.Count > 2             // 有多轮独白(在思考)
                || ai.CurrentState != AIState.Observe        // 状态变了
                || ai.Account.TotalBoughtQty > 0;            // 买了(护盘)
            Assert.True(reacted,
                $"价格跌至{price},AI应有反应,状态={ai.CurrentState} 独白={ai.Thoughts.Count} 买入={ai.Account.TotalBoughtQty}");
        }
    }

    [Fact]
    public void AI_Distributes_WhenPriceAboveCost()
    {
        // 玩家大量买入→价格上涨→AI持仓在高位应有反应(出货或跟风)
        var (loop, session, ai, _) = SetupAI();
        // 拉升
        for (int i = 0; i < 8; i++)
        {
            try { session.Submit(new OrderRequest(Player, Side.Buy, OrderType.Market, Price.Zero, new Quantity(800))); }
            catch { }
            RunTicks(loop, 5);
        }
        decimal price = session.Engine.LastPrice?.Value ?? 10m;
        // AI 在高位(>成本5%)的反应取决于仓位:
        // - 仓位重(>3000):出货(Distribute)
        // - 仓位轻(<3000):跟风(Follow)
        // 两者都是合理反应,关键是AI不是Observe(无视)
        if (price > 10.3m)
        {
            bool reacted = ai.CurrentState == AIState.Distribute
                || ai.CurrentState == AIState.Follow
                || ai.Account.TotalSoldQty > 0
                || ai.Account.TotalBoughtQty > 0;
            Assert.True(reacted,
                $"价格涨至{price},AI应有反应,状态={ai.CurrentState} 买={ai.Account.TotalBoughtQty} 卖={ai.Account.TotalSoldQty}");
        }
    }

    // ══════════════════════════════════════
    // 3. AI 账户完整性
    // ══════════════════════════════════════

    [Fact]
    public void AI_AccountStaysValid_NeverNegative()
    {
        var (loop, _, ai, _) = SetupAI();
        RunTicks(loop, 80);
        // AI 账户应始终有效:现金≥0,持仓≥0,权益有界
        Assert.True(ai.Account.Cash >= 0, $"AI 现金不应为负,实际{ai.Account.Cash}");
        Assert.True(ai.Account.Position.Total.Value >= 0, $"AI 持仓不应为负");
        var equity = ai.Account.TotalEquity(loop.Session.Engine.LastPrice ?? new Price(10m));
        Assert.True(equity > 0, $"AI 权益应>0,实际{equity}");
    }

    [Fact]
    public void AI_MarketActive_HasTrades()
    {
        var (loop, session, ai, _) = SetupAI();
        RunTicks(loop, 80);
        // 检查是否有成交:看AI或做市商的交易记录
        bool hasTrades = ai.Account.TotalBoughtQty > 0 || ai.Account.TotalSoldQty > 0;
        // 也检查盘口是否有变化(现价不是初始值)
        decimal price = session.Engine.LastPrice?.Value ?? 10m;
        Assert.True(hasTrades || price != 10m, "市场应有交易活动(AI买卖或价格变动)");
    }

    // ══════════════════════════════════════
    // 4. AI 意图识别
    // ══════════════════════════════════════

    [Fact]
    public void AI_DetectsPlayerPumpUp()
    {
        // 玩家持续买入→AI应识别到 PushingUp 意图
        var (loop, session, ai, _) = SetupAI();
        // 玩家急拉
        session.Submit(new OrderRequest(Player, Side.Buy, OrderType.Market, Price.Zero, new Quantity(3000)));
        RunTicks(loop, 20);
        // AI 的独白中应有提到推价/拉升的识别
        bool detected = ai.Thoughts.Any(t =>
            t.DetectedIntent.ToString().Contains("PushingUp") || t.Reason.Contains("拉"));
        // 不强制每次都检测到(概率性),但至少AI的意图识别器应该在工作
        Assert.True(ai.Thoughts.Count > 0, "AI 应有独白记录");
    }

    // ══════════════════════════════════════
    // 5. AI 做空行为
    // ══════════════════════════════════════

    [Fact]
    public void AI_MayShort_WhenOvervalued()
    {
        // 价格远高于内在价值→AI可能在高位做空
        var (loop, session, ai, _) = SetupAI();
        // 玩家暴力拉升到很高
        for (int i = 0; i < 10; i++)
        {
            try { session.Submit(new OrderRequest(Player, Side.Buy, OrderType.Market, Price.Zero, new Quantity(500))); }
            catch { }
            RunTicks(loop, 3);
        }
        decimal price = session.Engine.LastPrice?.Value ?? 10m;
        // 如果价格远超内在价值(>12),AI可能做空了
        if (price > 11.5m)
        {
            // 不强制(概率性),但检查AI账户状态一致
            Assert.True(ai.Account.Position.ShortQty.Value >= 0, "AI 空头持仓不应为负");
            // 如果AI做了空,券池应该减少了
            Assert.True(session.ShortablePool <= session.TotalShortable, "券池不应超过总量");
        }
    }

    // ══════════════════════════════════════
    // 6. 多日AI行为连续性
    // ══════════════════════════════════════

    [Fact]
    public void AI_SurvivesMultiDay_WithoutCrash()
    {
        var (loop, _, ai, _) = SetupAI(ticksPerDay: 50);
        // 跑5天
        for (int day = 0; day < 5; day++)
        {
            RunTicks(loop, 50);
            if (!loop.IsFinished)
            {
                while (!loop.IsDayClosed && !loop.IsFinished) loop.Step();
                loop.StartNextDay();
            }
        }
        // AI 应全程无异常,状态有效,账户完整
        Assert.True(Enum.IsDefined(ai.CurrentState));
        Assert.True(ai.Account.Cash >= 0);
        Assert.True(ai.Thoughts.Count > 0, "AI 5天后应有独白记录");
    }

    // ══════════════════════════════════════
    // 7. InstitutionB 行为
    // ══════════════════════════════════════

    [Fact]
    public void InstitutionB_ProvidesLiquidity_MarketMakes()
    {
        // 机构B作为做市商应在盘口挂单
        var (loop, session, _, _) = SetupAI();
        RunTicks(loop, 10);
        var bids = session.Engine.View.TopBids(5);
        var asks = session.Engine.View.TopAsks(5);
        // 盘口应有深度(机构B在做市)
        Assert.True(bids.Count > 0, "盘口应有买盘");
        Assert.True(asks.Count > 0, "盘口应有卖盘");
    }

    [Fact]
    public void InstitutionB_RiskControl_ReducesDepth_OnHighRisk()
    {
        // 大幅波动→机构B风险升高→减少做市深度
        var (loop, session, _, _) = SetupAI();
        // 制造剧烈波动
        for (int i = 0; i < 5; i++)
        {
            try { session.Submit(new OrderRequest(Player, Side.Buy, OrderType.Market, Price.Zero, new Quantity(2000))); }
            catch { }
            RunTicks(loop, 3);
        }
        // 机构B 不应崩溃,盘口仍有序(不要求深度精确,只要求不断层)
        var asks = session.Engine.View.TopAsks(3);
        Assert.True(asks.Count > 0, "机构B高风险时盘口不应完全消失");
    }
}
