using System.Collections;
using IamZhuli.Presenter;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using IamZhuli.Unity;
namespace IamZhuli.Tests
{
    /// <summary>
    /// PlayMode 测试:GameHost(MonoBehaviour)按帧驱动 GameCore 并分发快照事件。
    /// 验证 Unity 主循环 ↔ 游戏大脑 ↔ 事件桥的真实运转(卡牌出牌阶段会自动冻结时钟)。
    /// </summary>
    public sealed class GameHostPlayTests
    {
        [UnityTest]
        public IEnumerator GameHost_LoadsLevel_AndEmitsSnapshot()
        {
            var go = new GameObject("host");
            var host = go.AddComponent<GameHost>();
            host.Core.LoadLevelById("tutorial");
            host.Core.BeginTrading();
            host.ticksPerSecond = 60f;

            MarketSnapshotDto got = null;
            host.SnapshotUpdated += s => got = s;

            int frames = 0;
            while (got == null && frames++ < 300)
                yield return null;   // 等真实帧推进

            Assert.IsNotNull(got, "GameHost 应在数帧内发出快照");
            Assert.GreaterOrEqual(got.CurrentDay, 1, "快照应有天数");
            Assert.IsNotNull(got.CardState, "卡牌模式快照应含卡牌状态");
            Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator GameHost_CardPlayPhase_FreezesClock()
        {
            var go = new GameObject("host");
            var host = go.AddComponent<GameHost>();
            host.Core.LoadLevelById("tutorial");
            host.Core.BeginTrading();
            host.ticksPerSecond = 60f;

            // Play 阶段:tick 不推进,快照里 tickOfDay 不变
            int tickBefore = -1;
            for (int i = 0; i < 30; i++) yield return null;
            var s1 = host.Core.BuildSnapshot();
            tickBefore = s1.TickOfDay;
            for (int i = 0; i < 30; i++) yield return null;
            var s2 = host.Core.BuildSnapshot();

            Assert.AreEqual("Play", s2.CardState.Phase, "未出牌应停留在 Play 阶段");
            Assert.AreEqual(tickBefore, s2.TickOfDay, "Play 阶段时钟必须冻结(等玩家出牌)");

            Object.Destroy(go);
        }
    }
}
