using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

using IamZhuli.Unity;
using IamZhuli.Presenter;
namespace IamZhuli.Tests
{
    /// <summary>
    /// UXML 与 MainUI 的一致性测试:MainUI.Q 查询的所有元素名必须在 Main.uxml 里存在,
    /// 自绘图表元素能从 UXML 实例化,USS 主题资产可加载。
    /// 在编辑器跑一次就能抓住"UXML 改了名/删了元素,MainUI 没跟上"这类错位——
    /// 这是不开 Play 模式也能验证 UI 布局完整性的关键测试。
    /// </summary>
    public sealed class UxmlSanityTests
    {
        private const string UxmlPath = "Assets/IamZhuli/UI/Main.uxml";
        private const string UssPath = "Assets/IamZhuli/UI/Theme.uss";

        /// <summary>MainUI.Bind() 里全部 Q&lt;T&gt;(name) 清单(与代码保持同步)。</summary>
        private static readonly string[] ElementNames =
        {
            // 顶栏
            "phaseLabel", "lastPriceLabel", "lastPriceBig", "sentimentLabel", "regulatorLabel",
            // 盘口/成交
            "asksContainer", "bidsContainer", "tradesContainer",
            // 图表
            "timeshareChart", "volLabel", "turnoverLabel",
            // 卡牌
            "cardPanel", "handContainer", "turnLabel", "energyLabel", "cardPhaseLabel",
            "endTurnBtn", "revealPanel", "revealSummary", "revealTrades",
            // 下单(连续模式)
            "orderPanel", "buyBtn", "sellBtn", "cancelAllBtn",
            // 账户/目标
            "equityLabel", "cashLabel", "positionLabel", "costLabel", "floatLabel",
            "shortLabel", "objectivesContainer",
            // 底栏
            "speed1Btn", "speed3Btn", "speed10Btn", "pauseBtn", "nextDayBtn", "msgLabel",
            // 覆盖层
            "levelSelectOverlay", "levelList", "resultOverlay", "resultTitle", "resultBody", "retryBtn",
        };

        [Test]
        public void Uxml_IsValidAsset()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assert.IsNotNull(uxml, "Main.uxml 应可加载(路径/语法)");
        }

        [Test]
        public void Uss_IsValidAsset()
        {
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            Assert.IsNotNull(uss, "Theme.uss 应可加载(路径/语法)");
        }

        [Test]
        public void Uxml_ContainsEveryElement_MainUIQueries()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assume.That(uxml, Is.Not.Null);
            var tree = uxml.CloneTree();
            var missing = new System.Collections.Generic.List<string>();
            foreach (var name in ElementNames)
                if (tree.Q(name) == null) missing.Add(name);
            Assert.IsEmpty(missing, "UXML 缺少 MainUI 查询的元素: " + string.Join(", ", missing));
        }

        [Test]
        public void TimeshareElement_InstantiatesFromUxml()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assume.That(uxml, Is.Not.Null);
            var tree = uxml.CloneTree();
            var chart = tree.Q<TimeshareElement>("timeshareChart");
            Assert.IsNotNull(chart, "timeshareChart 应实例化为 TimeshareElement(UxmlFactory)");
        }

        [Test]
        public void TimeshareElement_SetData_EmptyAndNormal_DoesNotThrow()
        {
            var chart = new TimeshareElement();
            Assert.DoesNotThrow(() => chart.SetData(
                new System.Collections.Generic.List<TimesharePointDto>(), 10m), "空数据不应抛异常");
            var points = new System.Collections.Generic.List<TimesharePointDto>
            {
                new TimesharePointDto(0, 10.0m, 100),
                new TimesharePointDto(1, 10.1m, 260),
                new TimesharePointDto(2, 9.9m, 460),
            };
            Assert.DoesNotThrow(() => chart.SetData(points, 10.0m), "正常数据不应抛异常");
        }
    }
}
