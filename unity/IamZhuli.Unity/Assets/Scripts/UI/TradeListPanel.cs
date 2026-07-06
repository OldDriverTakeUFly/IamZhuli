using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Unity.UI
{
    /// <summary>
    /// 成交明细面板:订阅 OnTrade,显示最近成交(价格/数量/方向)。
    /// 挂载到含一个 Text(或 ScrollView 内的 Text)的 Panel 上。
    /// </summary>
    public class TradeListPanel : MonoBehaviour
    {
        public GameHost gameHost;
        public Text tradeListText;
        public int maxDisplay = 15;

        private readonly List<string> _trades = new();

        void OnEnable()
        {
            if (gameHost != null)
            {
                var session = gameHost.GetLoop().Session;
                session.OnTrade += OnTrade;
            }
        }

        void OnDisable()
        {
            if (gameHost != null)
            {
                var session = gameHost.GetLoop().Session;
                session.OnTrade -= OnTrade;
            }
        }

        private void OnTrade(Price price, Quantity qty, Side side)
        {
            string color = side == Side.Buy ? "<color=#ef5350>" : "<color=#26a69a>";
            string dir = side == Side.Buy ? "买" : "卖";
            _trades.Insert(0, $"{color}{dir} {price.Value:F2}×{qty.Value}</color>");

            while (_trades.Count > maxDisplay) _trades.RemoveAt(_trades.Count - 1);

            if (tradeListText != null)
                tradeListText.text = string.Join("\n", _trades);
        }
    }
}
