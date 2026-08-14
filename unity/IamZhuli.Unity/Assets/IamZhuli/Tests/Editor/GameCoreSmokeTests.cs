using System;
using IamZhuli.Presenter;
using NUnit.Framework;

namespace IamZhuli.Tests
{
    /// <summary>
    /// GameCore 在 Unity 环境的全流程冒烟测试(打开编辑器即可跑)。
    /// 验证:逻辑层 DLL 加载、关卡加载(预演)、卡牌回合+揭牌、整局跑到结算。
    /// 这是 M4 的验收测试——DLL 通道与游戏大脑在 Unity 里端到端可用。
    /// </summary>
    public sealed class GameCoreSmokeTests
    {
        private const int MaxSteps = 50000;   // 防死循环

        [Test]
        public void DllLoads_CoreCreates_WithDefaults()
        {
            var core = new GameCore();
            Assert.IsFalse(core.IsInitialized, "未加载关卡前 IsInitialized=false");
            Assert.IsNull(core.CurrentLevel, "未加载关卡前 CurrentLevel=null");
        }

        [Test]
        public void LoadLevel_PreplayProducesHistory()
        {
            var core = new GameCore();
            core.LoadLevelById("accumulate");
            Assert.IsTrue(core.IsInitialized);
            var snap = core.BuildSnapshot();
            Assert.Greater(snap.TotalDays, 0, "关卡应有天数");
            Assert.IsNotNull(snap.DailyCandles, "预演应产出历史K线");
            Assert.Greater(snap.DailyCandles.Count, 5, "预演历史K线应有足够天数");
            Assert.IsTrue(snap.IsPreMarket, "加载后应处于盘前态");
        }

        [Test]
        public void BeginTrading_CardMode_DealsHandOfFive()
        {
            var core = new GameCore();
            core.LoadLevelById("tutorial");
            core.BeginTrading();
            var snap = core.BuildSnapshot();
            Assert.IsNotNull(snap.CardState, "卡牌模式应有卡牌状态");
            Assert.AreEqual("Play", snap.CardState.Phase, "开局应为出牌阶段");
            Assert.AreEqual(5, snap.CardState.Hand.Count, "起始手牌5张");
            Assert.GreaterOrEqual(snap.CardState.Energy, 3, "起始能量≥3");
        }

        [Test]
        public void PlayCard_EndCardTurn_AuctionRevealAndConservation()
        {
            var core = new GameCore();
            core.LoadLevelById("accumulate");
            core.BeginTrading();

            decimal equityBefore = core.BuildSnapshot().Account.TotalEquity;

            // 出第一张可出的牌(操盘牌收集意图,舆论牌立即生效)
            bool played = false;
            for (int i = 0; i < 5 && !played; i++)
                played = core.PlayCard(i, out string _);
            Assert.IsTrue(played, "5张手牌里应至少有1张可出");

            core.EndCardTurn();
            var ra = core.LastAuction;
            Assert.IsNotNull(ra, "结束回合应产生揭牌结果");
            Assert.AreEqual(0, ra.Turn, "首回合揭牌");
            if (ra.Volume > 0)
            {
                Assert.Greater(ra.Price, 0m, "有成交时竞价价>0");
                Assert.AreEqual(ra.Volume, SumTradeQty(ra), "总量=逐笔之和");
            }

            // Resolve 阶段推进到下一回合(出牌冻结)
            var snap = StepUntil(core, s => s.CardState != null && s.CardState.Phase == "Play", 200);
            Assert.IsNotNull(snap, "30 tick 后应回到出牌阶段");
            Assert.AreEqual(1, snap.CardState.Turn, "进入第2回合");

            // 权益有限(无 NaN/爆仓穿透)
            Assert.IsFalse(double.IsNaN((double)snap.Account.TotalEquity), "权益不应为 NaN");
        }

        [Test]
        public void FullGame_RunsToSettlement_WithScoreRankings()
        {
            var core = new GameCore();
            core.LoadLevelById("tutorial");
            core.BeginTrading();

            int steps = 0;
            while (!core.IsLevelOver && steps++ < MaxSteps)
            {
                var s = core.Step();
                if (s == null) break;   // 关卡时间结束
                // 卡牌出牌阶段冻结 → 替玩家出牌并揭牌(全自动驱动)
                if (s.CardState != null && s.CardState.Phase == "Play")
                {
                    for (int i = 0; i < 5; i++)
                        if (core.PlayCard(i, out string _)) break;
                    core.EndCardTurn();
                }
            }
            Assert.Less(steps, MaxSteps, "关卡应在步数上限内结束");
            Assert.IsTrue(core.IsLevelOver, "关卡应结束");

            var score = core.SettleScore();
            Assert.AreEqual(3, score.Rankings.Count, "三方排名:玩家/AI主力/机构B");
            Assert.IsNotEmpty(score.Rankings[0].Name, "第一名应有名字");

            var result = core.EndLevelDto();
            Assert.IsNotNull(result.CoachComment, "结算应有教练点评");
        }

        [Test]
        public void Retry_ReloadsSameLevel()
        {
            var core = new GameCore();
            core.LoadLevelById("tutorial");
            core.BeginTrading();
            core.Retry();
            Assert.IsTrue(core.IsPreMarket, "重试后回到盘前态");
            Assert.AreEqual("tutorial", core.CurrentLevel.Id);
        }

        // ── 辅助 ──

        private static MarketSnapshotDto StepUntil(GameCore core, Func<MarketSnapshotDto, bool> predicate, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                var s = core.Step();
                if (s == null) return null;   // 关卡结束
                if (predicate(s)) return s;
            }
            return null;
        }

        private static int SumTradeQty(AuctionResultDto ra)
        {
            int sum = 0;
            foreach (var t in ra.Trades) sum += t.Qty;
            return sum;
        }
    }
}
