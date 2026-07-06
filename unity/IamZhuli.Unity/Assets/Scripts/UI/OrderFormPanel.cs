using UnityEngine;
using UnityEngine.UI;

namespace IamZhuli.Unity.UI
{
    /// <summary>
    /// 下单面板:输入价格/数量,选择买/卖 + 限价/市价,提交订单。
    /// 挂载到含 InputField×2 + Toggle/Button 的 Panel 上。
    /// </summary>
    public class OrderFormPanel : MonoBehaviour
    {
        [Header("引用")]
        public GameHost gameHost;

        [Header("UI 元素")]
        public InputField priceInput;
        public InputField qtyInput;
        public Toggle isMarketToggle;   // 勾选=市价单,不勾=限价单
        public Button buyButton;
        public Button sellButton;
        public Text messageText;         // 操作反馈

        void Start()
        {
            buyButton?.onClick.AddListener(() => Submit("buy"));
            sellButton?.onClick.AddListener(() => Submit("sell"));
        }

        private void Submit(string side)
        {
            if (gameHost == null) { ShowMsg("GameHost 未绑定"); return; }

            string type = (isMarketToggle != null && isMarketToggle.isOn) ? "market" : "limit";
            decimal price = 0;
            if (type == "limit")
            {
                if (!decimal.TryParse(priceInput?.text, out price) || price <= 0)
                {
                    ShowMsg("请输入有效价格");
                    return;
                }
            }
            if (!int.TryParse(qtyInput?.text, out int qty) || qty <= 0)
            {
                ShowMsg("请输入有效数量");
                return;
            }

            try
            {
                var result = gameHost.SubmitOrder(side, type, price, qty);
                string status = result.FinalStatus.ToString();
                int filled = result.TotalFilled.Value;
                ShowMsg(side == "buy" ? $"买入{status} 成交{filled}手" : $"卖出{status} 成交{filled}手");
            }
            catch (System.Exception e)
            {
                ShowMsg($"失败: {e.Message}");
            }
        }

        private void ShowMsg(string msg)
        {
            if (messageText != null) messageText.text = msg;
        }
    }
}
