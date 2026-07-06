using UnityEngine;
using UnityEngine.UI;

namespace IamZhuli.Unity.UI
{
    /// <summary>
    /// 账户面板:显示权益/现金/持仓/成本/监管/情绪。
    /// 挂载到含多个 Text 的 Panel 上。
    /// </summary>
    public class AccountPanel : MonoBehaviour
    {
        public GameHost gameHost;

        [Header("账户")]
        public Text equityText;
        public Text cashText;
        public Text availableText;

        [Header("持仓")]
        public Text holdingText;
        public Text availableHoldingText;
        public Text costText;

        [Header("状态")]
        public Text dayText;
        public Text regulatorText;
        public Text sentimentText;
        public Text preMarketText;     // 盘前提示

        void OnEnable()
        {
            if (gameHost != null) gameHost.OnTickEvent += Refresh;
        }

        void OnDisable()
        {
            if (gameHost != null) gameHost.OnTickEvent -= Refresh;
        }

        void Refresh()
        {
            if (gameHost == null) return;
            var snap = gameHost.GetSnapshot();

            SetText(equityText, $"{snap.PlayerEquity / 10000:F1}万");
            SetText(cashText, $"{snap.PlayerCash / 10000:F1}万");
            SetText(availableText, $"{snap.PlayerAvailable / 10000:F1}万");
            SetText(holdingText, $"{snap.PlayerHolding} 手");
            SetText(availableHoldingText, $"{snap.PlayerAvailableHolding} 手");
            SetText(costText, $"{snap.PlayerCost:F2}");
            SetText(dayText, $"第{snap.Day}/{snap.TotalDays}日 · tick {snap.TickOfDay}/{snap.TicksPerDay}");
            SetText(regulatorText, $"监管 交易{snap.RegulatorHeat:F0}% 信息{snap.InfoHeat:F0}%");
            SetText(sentimentText, $"情绪{snap.Sentiment * 100:F0} 信心{snap.Confidence * 100:F0}");

            if (preMarketText != null)
            {
                preMarketText.gameObject.SetActive(snap.IsPreMarket);
                if (snap.IsPreMarket) preMarketText.text = "📋 盘前准备中 · 点击开始操盘";
            }
        }

        private void SetText(Text t, string value)
        {
            if (t != null) t.text = value;
        }
    }
}
