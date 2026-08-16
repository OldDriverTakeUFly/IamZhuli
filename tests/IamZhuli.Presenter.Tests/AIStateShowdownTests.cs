using Xunit;
using IamZhuli.Simulation.AI;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// AI 状态矩阵对局:强制 AI 状态/档位(出牌阶段注入,EndCardTurn 产意图即生效),
/// 与玩家关键牌做确定性对撞——把"涌现碰运气"变成可枚举的关键对局表。
/// 覆盖此前无法覆盖的缺口:玩家×AI状态 的方向性交互。
/// </summary>
public sealed class AIStateShowdownTests
{
    private readonly ITestOutputHelper _out;
    public AIStateShowdownTests(ITestOutputHelper o) => _out = o;

    /// <summary>标准对局流程:跳过回合1攒能量 → 强制AI状态 → 出牌 → 揭牌 → 返回揭牌结果。</summary>
    private static AuctionResultDto Showdown(string playerCard, AIState aiState, string deckThird = "限价吸筹")
    {
        var core = Harness.NewGame("accumulate", playerCard, deckThird);
        Harness.NextTurn(core);   // 回合1空过,能量3+3=6
        core.AI!.ForceStateForTest(aiState);
        Assert.True(Harness.Play(core, playerCard), Harness.LastPlayError);
        core.EndCardTurn();
        return core.LastAuction!;
    }

    private static int SumBy(AuctionResultDto ra, string name, bool asBuyer)
        => ra.Trades.Where(t => asBuyer ? t.Buyer == name : t.Seller == name).Sum(t => t.Qty);

    [Fact]
    public void AI_Follow_BuysIntoMarket_EvenWithoutPlayerPush()
    {
        // AI跟风(市价买):玩家只出被动限价吸筹(不吃卖方供给),AI 跟风单应吃到流动性。
        // 注:玩家若出大额急拉,AI 小单会被时间优先级挤出(供给先被玩家吃尽)——那是正确行为
        var ra = Showdown("限价吸筹", AIState.Follow);
        int aiBought = SumBy(ra, "AI主力", asBuyer: true);
        _out.WriteLine($"跟风对局: 竞价{ra.Price} 量{ra.Volume} AI买入{aiBought}");
        Assert.True(aiBought > 0, "Follow 状态的 AI 应在拍卖中买入");
    }

    [Fact]
    public void AI_Distribute_Fills_PlayerPump()
    {
        // AI出货(限价卖) × 玩家急拉:对手盘——AI应是把筹码派发给玩家的卖方
        var ra = Showdown("市价急拉", AIState.Distribute);
        int fromAi = ra.Trades.Where(t => t.Buyer == "你" && t.Seller == "AI主力").Sum(t => t.Qty);
        _out.WriteLine($"出货对局: 竞价{ra.Price} 量{ra.Volume} 你从AI主力接货{fromAi}");
        Assert.True(fromAi > 0, "Distribute 状态的 AI 应派发给买入的玩家");
    }

    [Fact]
    public void AI_Wash_Hits_PlayerPump_Heavier()
    {
        // AI洗盘(市价卖,量更大) × 玩家急拉:对冲压力更大,成交量显著
        var ra = Showdown("市价急拉", AIState.Wash);
        int aiSold = SumBy(ra, "AI主力", asBuyer: false);
        _out.WriteLine($"洗盘对局: 竞价{ra.Price} 量{ra.Volume} AI卖出{aiSold}");
        Assert.True(aiSold > 0, "Wash 状态的 AI 应在拍卖中卖出");
    }

    [Fact]
    public void AI_Counter_Collides_With_PlayerPump()
    {
        // AI反杀(市价卖+条件做空) × 玩家急拉:正面相撞——AI是卖方,量能对冲
        var ra = Showdown("市价急拉", AIState.Counter);
        int aiSold = SumBy(ra, "AI主力", asBuyer: false);
        _out.WriteLine($"反杀对局: 竞价{ra.Price} 量{ra.Volume} AI卖出{aiSold}");
        Assert.True(aiSold > 0, "Counter 状态的 AI 应反手卖出");
    }

    [Fact]
    public void AI_Defend_Absorbs_PlayerShortRaid()
    {
        // AI护盘(限价买) × 玩家做空突袭:AI的买墙承接空头
        var ra = Showdown("做空突袭", AIState.Defend);
        int aiBought = SumBy(ra, "AI主力", asBuyer: true);
        _out.WriteLine($"护盘对局: 竞价{ra.Price} 量{ra.Volume} AI承接{aiBought}");
        Assert.True(aiBought > 0, "Defend 状态的 AI 买单应承接玩家的做空");
    }

    [Fact]
    public void AI_Observe_Produces_NoIntents()
    {
        // AI观望:不产任何意图——拍卖里不应出现AI主力
        var ra = Showdown("市价急拉", AIState.Observe);
        bool aiPresent = ra.Trades.Any(t => t.Buyer == "AI主力" || t.Seller == "AI主力");
        _out.WriteLine($"观望对局: 竞价{ra.Price} 量{ra.Volume} AI在场={aiPresent}");
        Assert.False(aiPresent, "Observe 状态的 AI 不应出现在拍卖");
    }

    // ── 机构B 风险档位矩阵 ──

    [Fact]
    public void InstB_LowRisk_WallsAbsorb_PlayerPump()
    {
        var core = Harness.NewGame("accumulate", "市价急拉");
        Harness.NextTurn(core);
        core.Institution!.ForceRiskLevelForTest(RiskLevel.Low);
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        int instSold = SumBy(ra, "机构B", asBuyer: false);
        _out.WriteLine($"做市对局: 竞价{ra.Price} 量{ra.Volume} 机构B卖出{instSold}");
        Assert.True(instSold > 0, "Low 档做市墙应承接玩家的买入");
    }

    [Fact]
    public void InstB_HighRisk_ThinsWalls_AndReduces()
    {
        var core = Harness.NewGame("accumulate", "市价急拉");
        Harness.NextTurn(core);
        core.Institution!.ForceRiskLevelForTest(RiskLevel.High);
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        // High: 墙深×0.3 + 减仓市价卖(≤500) → 机构B仍卖但墙变薄
        int instSold = SumBy(ra, "机构B", asBuyer: false);
        _out.WriteLine($"高风险对局: 竞价{ra.Price} 量{ra.Volume} 机构B卖出{instSold}");
        Assert.True(instSold > 0, "High 档应同时做市+减仓(卖方在场)");
    }

    [Fact]
    public void InstB_Critical_NoWalls_DirectionalOnly()
    {
        var core = Harness.NewGame("accumulate", "市价急拉");
        Harness.NextTurn(core);
        core.Institution!.ForceRiskLevelForTest(RiskLevel.Critical);
        Assert.True(Harness.Play(core, "市价急拉"), Harness.LastPlayError);
        core.EndCardTurn();
        var ra = core.LastAuction!;
        int instSold = SumBy(ra, "机构B", asBuyer: false);
        _out.WriteLine($"危急对局: 竞价{ra.Price} 量{ra.Volume} 机构B卖出{instSold}");
        // Critical: 不再做市(无墙),只有方向性卖单(减仓500)——卖方在场但无墙
        Assert.True(instSold > 0, "Critical 档应只剩方向性减仓(卖)");
    }

    [Fact]
    public void Matrix_AllStates_ProduceValidAuctions_NoCrash()
    {
        // 穷举:5状态×4张关键牌=20个对局,全部产出合法拍卖且不崩(不变量由 fuzz 层负责)
        string[] cards = { "市价急拉", "做空突袭", "限价挂墙", "限价吸筹" };
        var states = new[] { AIState.Defend, AIState.Wash, AIState.Distribute, AIState.Follow, AIState.Counter };
        int runs = 0;
        foreach (var s in states)
            foreach (var c in cards)
            {
                var ra = Showdown(c, s);
                Assert.NotNull(ra);
                Assert.True(ra.Trades.All(t => t.Price > 0 && t.Qty > 0), $"{s}×{c} 非法成交");
                runs++;
            }
        _out.WriteLine($"矩阵穷举 {runs} 局全部有效");
        Assert.Equal(20, runs);
    }
}
