using System.Collections.Generic;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using UnityEngine;

namespace IamZhuli.Unity.UI
{
    /// <summary>
    /// 盘口五档面板:显示买卖五档 + 现价。
    /// 挂载到含 11 个 Text(5卖+现价+5买)的 Panel 上,inspector 里绑定引用。
    /// </summary>
    public class OrderBookPanel : MonoBehaviour
    {
        [Header("引用")]
        public GameHost gameHost;

        [Header("卖盘 Text(从卖5到卖1,倒序)")]
        public UnityEngine.UI.Text[] askTexts = new Text[5];   // [0]=卖5 ... [4]=卖1

        [Header("现价")]
        public UnityEngine.UI.Text lastPriceText;

        [Header("买盘 Text(从买1到买5)")]
        public UnityEngine.UI.Text[] bidTexts = new Text[5];   // [0]=买1 ... [4]=买5

        private static readonly string Green = "<color=#26a69a>";
        private static readonly string Red = "<color=#ef5350>";
        private const string EndColor = "</color>";

        void OnEnable()
        {
            if (gameHost != null)
                gameHost.OnTickEvent += Refresh;
        }

        void OnDisable()
        {
            if (gameHost != null)
                gameHost.OnTickEvent -= Refresh;
        }

        void Refresh()
        {
            if (gameHost == null) return;
            var view = gameHost.GetLoop().Session.Engine.View;

            var asks = view.TopAsks(5);
            var bids = view.TopBids(5);

            // 卖盘(从上到下:卖5→卖1)
            for (int i = 0; i < 5; i++)
            {
                int idx = 4 - i;   // asks[0]=卖1(最优),显示在底部
                if (askTexts[i] != null)
                {
                    if (idx < asks.Count)
                        askTexts[i].text = $"卖{idx+1}  {Green}{asks[idx].Price.Value:F2}{EndColor}  {asks[idx].TotalQty.Value}";
                    else
                        askTexts[i].text = $"卖{idx+1}  ——";
                }
            }

            // 现价
            if (lastPriceText != null)
            {
                var lp = view.LastPrice;
                lastPriceText.text = lp != null ? $"{lp.Value:F2}" : "——";
            }

            // 买盘(从上到下:买1→买5)
            for (int i = 0; i < 5; i++)
            {
                if (bidTexts[i] != null)
                {
                    if (i < bids.Count)
                        bidTexts[i].text = $"买{i+1}  {Red}{bids[i].Price.Value:F2}{EndColor}  {bids[i].TotalQty.Value}";
                    else
                        bidTexts[i].text = $"买{i+1}  ——";
                }
            }
        }
    }
}
