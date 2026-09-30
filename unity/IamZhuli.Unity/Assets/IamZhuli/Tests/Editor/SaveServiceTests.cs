using System;
using System.IO;
using IamZhuli.Presenter;
using IamZhuli.Unity;
using NUnit.Framework;
using UnityEngine;

namespace IamZhuli.Tests
{
    /// <summary>
    /// 本地存档服务测试(EditMode,临时目录注入——不碰编辑器 persistentDataPath):
    /// 文件落盘/读取/列表/坏档跳过/槽位清洗,以及真对局"存→读→续玩"闭环。
    /// 序列化格式本身由 xUnit SaveJsonTests(逻辑层)覆盖。
    /// </summary>
    public sealed class SaveServiceTests
    {
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "iamzhuli-saves-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        /// <summary>推到日切边界(与 GameCoreSmokeTests 同驱动模型:段Play空过→日收显式跨日)。</summary>
        private static void RunToDayClose(GameCore core)
        {
            for (int guard = 0; guard < 3000 && !core.Loop.IsDayClosed && !core.Loop.IsFinished; guard++)
            {
                if (core.BuildSnapshot().CardState is { Phase: "Play" } and { CardsPlayed: 0 })
                    core.EndCardTurn();
                core.Step();
            }
            Assert.IsTrue(core.Loop.IsDayClosed, "应推到日切");
        }

        [Test]
        public void Save_AtDayBoundary_WritesFileWithValidJson()
        {
            var core = NewStartedCore();
            RunToDayClose(core);
            var svc = new SaveService(_dir);

            Assert.IsNull(svc.Save("slot1", core), "日切边界应存档成功");
            var file = Path.Combine(_dir, "slot1.json");
            Assert.IsTrue(File.Exists(file), "应落盘 slot1.json");
            // 文件内容可被 SaveJson 直接解析(端到端:ExportSave→文件→读回)
            var sd = SaveJson.Parse(File.ReadAllText(file));
            Assert.AreEqual(core.CurrentLevel!.Id, sd.LevelId);
        }

        [Test]
        public void Save_MidDay_RejectedByGate()
        {
            var core = NewStartedCore();
            core.EndCardTurn();               // 进入Resolve执行中
            for (int i = 0; i < 5; i++) core.Step();
            Assert.IsFalse(core.CanSave, "盘中不可存档(UI 层同款门禁)");
        }

        [Test]
        public void Save_AtTurn0Start_IsAllowed()
        {
            var core = NewStartedCore();      // 开局即段0起点
            Assert.IsTrue(core.CanSave, "段0起点可存档");
            Assert.IsNull(new SaveService(_dir).Save("auto", core));
        }

        [Test]
        public void Load_RestoresIntoFreshCore_AndGameContinues()
        {
            var a = NewStartedCore(seed: 4242);
            RunToDayClose(a);
            var svc = new SaveService(_dir);
            Assert.IsNull(svc.Save("slot1", a));

            var sd = svc.Load("slot1");
            Assert.IsNotNull(sd, "应读到存档");
            var b = new GameCore(seed: 99);
            Assert.IsTrue(b.LoadFromSave(sd!), "应恢复成功");

            var sb = b.BuildSnapshot();
            Assert.AreEqual(sd!.Day, sb.CurrentDay);
            Assert.AreEqual(sd.Cards.Energy, sb.CardState!.Energy);
            Assert.AreEqual(sd.Cards.Hand.Count, sb.CardState.Hand.Count);

            // 续玩:过一段不崩
            b.EndCardTurn();
            for (int i = 0; i < 800 && !b.IsLevelOver; i++)
            {
                if (b.Loop.IsFinished) break;
                if (b.Step()?.CardState is { Phase: "Play" }) break;
            }
            Assert.Greater(b.BuildSnapshot().LastPrice, 0m, "续玩后应有成交价");
        }

        [Test]
        public void Load_MissingSlot_ReturnsNull()
            => Assert.IsNull(new SaveService(_dir).Load("nope"));

        [Test]
        public void ListSaves_NewestFirst_CorruptSkipped()
        {
            var svc = new SaveService(_dir);
            var core = NewStartedCore();
            Assert.IsNull(svc.Save("b", core));
            System.Threading.Thread.Sleep(20);   // 保证 mtime 可分辨(FS粒度)
            Assert.IsNull(svc.Save("a", core));
            File.WriteAllText(Path.Combine(_dir, "broken.json"), "broken-截断坏档");

            var list = svc.ListSaves();
            Assert.AreEqual(2, list.Count, "坏档应被跳过");
            Assert.AreEqual("a", list[0].Slot, "新→旧");
            Assert.AreEqual("b", list[1].Slot);
            Assert.AreEqual(core.CurrentLevel!.Id, list[0].LevelId);
            Assert.GreaterOrEqual(list[0].Day, 1);
        }

        [Test]
        public void SlotName_IsSanitized_AgainstPathTraversal()
        {
            var svc = new SaveService(_dir);
            var core = NewStartedCore();
            Assert.IsNull(svc.Save("../../evil", core));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "..", "..", "evil.json")), "不得逃逸目录");
            var files = Directory.GetFiles(_dir, "*.json");
            Assert.AreEqual(1, files.Length);
            StringAssert.DoesNotContain("/", Path.GetFileName(files[0]));
        }

        private static GameCore NewStartedCore(int seed = 1)
        {
            var core = new GameCore(seed: seed);
            core.LoadLevelById("accumulate");
            core.BeginTrading();
            return core;
        }
    }
}
