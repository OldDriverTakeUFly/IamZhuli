using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 性质测试(Fuzz):随机牌局×随机决策跑大量整局,每步断言全局不变量。
/// 目标不是验证具体行为,而是把"上万组合空间"里的崩溃类/账务破坏类问题压到零:
/// 现金守恒、券池不穿底、冻结不泄漏、价格不越界、状态不 NaN、操作不抛异常。
/// 失败时输出 seed+回合号,可精确复现(GameCore(seed) 已确定性)。
/// </summary>
public sealed class FuzzTests
{
    private readonly ITestOutputHelper _out;
    public FuzzTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] AllLevels = { "tutorial", "accumulate", "wash_pump", "adversarial", "pump_dump" };
    private static readonly string[] AllCards = IamZhuli.Simulation.Cards.CardDefinition.Library
        .Select(c => c.Name).ToArray();

    // ── 卡牌模式:随机牌组+随机出牌+随机盘后操作 ──

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    [InlineData(404)]
    [InlineData(505)]
    [InlineData(606)]
    [InlineData(707)]
    [InlineData(808)]
    [InlineData(909)]
    [InlineData(1101)]
    [InlineData(1212)]
    [InlineData(1313)]
    public void Fuzz_CardMode_RandomDeckAndPlays_InvariantsHold(int seed)
    {
        var rng = new Random(seed);
        string level = AllLevels[rng.Next(AllLevels.Length)];
        var core = new GameCore(seed: seed * 31 + 7);
        core.LoadLevelById(level);

        // 随机牌组(30张,从全牌库抽)
        var deck = Enumerable.Range(0, 30).Select(_ => AllCards[rng.Next(AllCards.Length)]).ToList();
        Assert.True(core.SetDeck(deck, out _));
        core.BeginTrading();

        Invariants.Check(core.BuildSnapshot(), $"[{level}#{seed} 开局]");

        int turns = 0;
        while (turns++ < 28 && !core.Loop.IsFinished && !core.BuildSnapshot().IsLevelOver)
        {
            var snap = core.BuildSnapshot();
            var cs = snap.CardState!;
            // 出 0~2 张随机手牌(允许失败:能量/规则拦截是正常路径)
            int plays = rng.Next(3);
            for (int i = 0; i < plays && cs.CardsPlayed + i < 2; i++)
                core.PlayCard(rng.Next(core.BuildSnapshot().CardState!.Hand.Count), out _);

            core.EndCardTurn();
            var ra = core.LastAuction;
            Assert.NotNull(ra);
            foreach (var t in ra!.Trades)
                Assert.True(t.Price > 0 && t.Qty > 0, $"[{level}#{seed} T{turns}] 揭牌成交非法: {t.Qty}@{t.Price}");

            // Resolve 推进到下一回合(含跨日),每回合校验
            var s2 = Harness.NextTurn(core);
            Invariants.Check(s2, $"[{level}#{seed} T{turns}]");

            // 日切时随机盘后操作
            if (core.Loop.IsDayClosed && !core.Loop.IsFinished)
            {
                if (rng.Next(3) == 0) core.PublishNews(AllLevels[rng.Next(2)] == "tutorial" ? "rumor" : "pump", out _);
                if (rng.Next(4) == 0) core.UpgradeWaterArmy(out _);
                if (rng.Next(4) == 0) core.ToggleWaterArmy(out _);
                if (rng.Next(3) == 0) core.PublishSignal("dragonlist", out _);
                Invariants.Check(core.BuildSnapshot(), $"[{level}#{seed} T{turns} 盘后]");
            }
        }
        _out.WriteLine($"[{level}#{seed}] {turns}回合完成, 权益 {core.BuildSnapshot().Account.TotalEquity / 10000:F0}万");
    }

    // ── 连续模式:随机订单流+撤单+做空平仓+跨日 ──

    [Theory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    [InlineData(44)]
    [InlineData(55)]
    [InlineData(66)]
    [InlineData(77)]
    [InlineData(88)]
    public void Fuzz_ContinuousMode_RandomOrders_InvariantsHold(int seed)
    {
        var rng = new Random(seed);
        string level = AllLevels[rng.Next(AllLevels.Length)];
        var core = new GameCore(seed: seed * 131 + 3) { CardMode = false };
        core.LoadLevelById(level);
        core.BeginTrading();
        Invariants.Check(core.BuildSnapshot(), $"[{level}#{seed} 开局]");

        int steps = 0, actions = 0;
        while (steps++ < 1600 && !core.Loop.IsFinished)
        {
            if (core.Loop.IsDayClosed) { core.StartNextDay(); continue; }

            // 每 5~15 tick 随机操作一次
            if (steps % rng.Next(5, 16) == 0)
            {
                actions++;
                decimal last = core.BuildSnapshot().LastPrice ?? 10m;
                var dto = rng.Next(6) switch
                {
                    0 => new OrderRequestDto("buy", "market", null, rng.Next(50, 5000)),
                    1 => new OrderRequestDto("sell", "market", null, rng.Next(50, 3000)),
                    2 => new OrderRequestDto("buy", "limit", Math.Round(last * (0.95m + (decimal)rng.NextDouble() * 0.08m), 2), rng.Next(100, 4000)),
                    3 => new OrderRequestDto("sell", "limit", Math.Round(last * (0.97m + (decimal)rng.NextDouble() * 0.08m), 2), rng.Next(100, 3000)),
                    4 => new OrderRequestDto("sell", "market", null, rng.Next(50, 1500), IsShort: true),   // 做空
                    _ => new OrderRequestDto("buy", "market", null, rng.Next(50, 1500), IsShort: true),    // 平仓
                };
                core.SubmitSafe(dto);
                if (rng.Next(5) == 0) core.CancelAll();
                if (rng.Next(6) == 0) core.PublishNews("pump", out _);
                if (rng.Next(8) == 0) core.PublishSignal("fakebigbuy", out _);
            }

            core.Step();
            if (steps % 40 == 0)
                Invariants.Check(core.BuildSnapshot(), $"[{level}#{seed} step{steps}]");
        }
        _out.WriteLine($"[{level}#{seed}] {steps}步/{actions}次操作, 权益 {core.BuildSnapshot().Account.TotalEquity / 10000:F0}万, 券池 {core.BuildSnapshot().ShortablePool}/{core.BuildSnapshot().TotalShortable}");
    }
}
