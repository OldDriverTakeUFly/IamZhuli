using System;
using System.Globalization;
using IamZhuli.Presenter;
using UnityEngine;
using UnityEngine.UIElements;

namespace IamZhuli.Unity
{
    /// <summary>
    /// 主界面控制器:绑定 Main.uxml 元素,订阅 GameHost 快照/成交事件渲染,
    /// 玩家输入(选关/出牌/结束回合/速度)直接调 GameCore。
    /// </summary>
    public sealed class MainUI : MonoBehaviour
    {
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        private static readonly (string id, string name, string brief)[] Levels =
        {
            ("tutorial",    "新手教学", "少量资金,学习吸筹→拉升→出货的基本循环"),
            ("accumulate",  "耐心吸筹", "低位悄悄收集筹码,不惊动监管"),
            ("pump_dump",   "拉高出货", "经典坐庄:暴力拉升后高位派发"),
            ("wash_pump",   "洗盘再拉升", "先砸出浮筹,再集中拉升"),
            ("adversarial", "对手博弈", "AI主力与你正面竞争,赢者通吃"),
        };

        private GameHost _host;
        private VisualElement _root;

        // 顶栏
        private Label _phaseLabel, _lastPriceLabel, _sentimentLabel, _regulatorLabel, _lastPriceBig;
        // 盘口/成交
        private VisualElement _asksContainer, _bidsContainer, _tradesContainer;
        // 图表
        private TimeshareElement _chart;
        private Label _volLabel, _turnoverLabel;
        // 卡牌
        private VisualElement _cardPanel, _handContainer, _revealPanel, _revealTrades;
        private Label _turnLabel, _energyLabel, _cardPhaseLabel, _revealSummary;
        private Button _endTurnBtn;
        // 下单(连续模式)
        private VisualElement _orderPanel;
        private Button _buyBtn, _sellBtn, _cancelAllBtn;
        // 账户/目标
        private Label _equityLabel, _cashLabel, _positionLabel, _costLabel, _floatLabel, _shortLabel;
        private VisualElement _objectivesContainer;
        // 底栏
        private Button _speed1, _speed3, _speed10, _pauseBtn, _nextDayBtn;
        private Label _msgLabel;
        // 覆盖层
        private VisualElement _levelOverlay, _resultOverlay;
        private VisualElement _levelList;
        private Label _resultTitle, _resultBody;
        private Button _retryBtn;

        private bool _resultShown;
        private readonly Action<TradeDto> _onTrade;

        public MainUI()
        {
            // 持有委托实例,OnDestroy 才能真正取消订阅(lambda 现场写无法 -=)
            _onTrade = _ => RenderTrades();
        }

        void Start()
        {
            _host = FindObjectOfType<GameHost>();
            if (_host == null)
            {
                Debug.LogError("MainUI 需要 GameHost 在同场景(用菜单 我是主力→一键搭建场景)");
                return;
            }
            _root = GetComponent<UIDocument>().rootVisualElement;
            Bind();
            _host.SnapshotUpdated += Render;
            _host.TradeTick += _onTrade;
            ShowLevelSelect();
            Render(_host.Core.BuildSnapshot());
        }

        void OnDestroy()
        {
            if (_host != null)
            {
                _host.SnapshotUpdated -= Render;
                _host.TradeTick -= _onTrade;
            }
        }

        private void Bind()
        {
            _phaseLabel = _root.Q<Label>("phaseLabel");
            _lastPriceLabel = _root.Q<Label>("lastPriceLabel");
            _lastPriceBig = _root.Q<Label>("lastPriceBig");
            _sentimentLabel = _root.Q<Label>("sentimentLabel");
            _regulatorLabel = _root.Q<Label>("regulatorLabel");

            _asksContainer = _root.Q("asksContainer");
            _bidsContainer = _root.Q("bidsContainer");
            _tradesContainer = _root.Q("tradesContainer");

            _chart = _root.Q<TimeshareElement>("timeshareChart");
            _volLabel = _root.Q<Label>("volLabel");
            _turnoverLabel = _root.Q<Label>("turnoverLabel");

            _cardPanel = _root.Q("cardPanel");
            _handContainer = _root.Q("handContainer");
            _turnLabel = _root.Q<Label>("turnLabel");
            _energyLabel = _root.Q<Label>("energyLabel");
            _cardPhaseLabel = _root.Q<Label>("cardPhaseLabel");
            _endTurnBtn = _root.Q<Button>("endTurnBtn");
            _revealPanel = _root.Q("revealPanel");
            _revealSummary = _root.Q<Label>("revealSummary");
            _revealTrades = _root.Q("revealTrades");

            _orderPanel = _root.Q("orderPanel");
            _buyBtn = _root.Q<Button>("buyBtn");
            _sellBtn = _root.Q<Button>("sellBtn");
            _cancelAllBtn = _root.Q<Button>("cancelAllBtn");

            _equityLabel = _root.Q<Label>("equityLabel");
            _cashLabel = _root.Q<Label>("cashLabel");
            _positionLabel = _root.Q<Label>("positionLabel");
            _costLabel = _root.Q<Label>("costLabel");
            _floatLabel = _root.Q<Label>("floatLabel");
            _shortLabel = _root.Q<Label>("shortLabel");
            _objectivesContainer = _root.Q("objectivesContainer");

            _speed1 = _root.Q<Button>("speed1Btn");
            _speed3 = _root.Q<Button>("speed3Btn");
            _speed10 = _root.Q<Button>("speed10Btn");
            _pauseBtn = _root.Q<Button>("pauseBtn");
            _nextDayBtn = _root.Q<Button>("nextDayBtn");
            _msgLabel = _root.Q<Label>("msgLabel");

            _levelOverlay = _root.Q("levelSelectOverlay");
            _levelList = _root.Q("levelList");
            _resultOverlay = _root.Q("resultOverlay");
            _resultTitle = _root.Q<Label>("resultTitle");
            _resultBody = _root.Q<Label>("resultBody");
            _retryBtn = _root.Q<Button>("retryBtn");

            _endTurnBtn.clicked += () =>
            {
                _host.Core.EndCardTurn();
                Render(_host.Core.BuildSnapshot());
            };
            _buyBtn.clicked += () => QuickOrder("buy");
            _sellBtn.clicked += () => QuickOrder("sell");
            _cancelAllBtn.clicked += () => { _host.Core.CancelAll(); Render(_host.Core.BuildSnapshot()); };
            _speed1.clicked += () => SetSpeed(1, _speed1);
            _speed3.clicked += () => SetSpeed(3, _speed3);
            _speed10.clicked += () => SetSpeed(10, _speed10);
            _pauseBtn.clicked += () =>
            {
                if (_host.Core.Loop.IsPaused) { _host.Core.Resume(); _pauseBtn.text = "暂停"; }
                else { _host.Core.Pause(); _pauseBtn.text = "继续"; }
            };
            _nextDayBtn.clicked += () =>
            {
                _host.Core.StartNextDay();
                Render(_host.Core.BuildSnapshot());
            };
            _retryBtn.clicked += () =>
            {
                _resultShown = false;
                _resultOverlay.style.display = DisplayStyle.None;
                _host.Core.Retry();
                ShowBeginModal();
            };
        }

        // ── 关卡选择/开始 ──

        private void ShowLevelSelect()
        {
            _levelList.Clear();
            foreach (var lv in Levels)
            {
                var item = lv.id;   // 闭包捕获
                var btn = new Button(() => SelectLevel(item)) { name = "level-" + item };
                btn.AddToClassList("level-btn");
                var nameL = new Label(lv.name + " · " + lv.id) { name = "name" };
                nameL.AddToClassList("name");
                var briefL = new Label(lv.brief) { name = "brief" };
                briefL.AddToClassList("brief");
                btn.Add(nameL);
                btn.Add(briefL);
                _levelList.Add(btn);
            }
            _levelOverlay.style.display = DisplayStyle.Flex;
        }

        private void SelectLevel(string id)
        {
            _host.Core.LoadLevelById(id);
            ShowBeginModal();
        }

        /// <summary>关卡已加载,展示简报+开始按钮(盘前态,背景已可看K线)。</summary>
        private void ShowBeginModal()
        {
            _levelList.Clear();
            var info = _host.Core.CurrentLevel;
            var title = new Label(info?.Name ?? "准备");
            title.AddToClassList("name");
            var brief = new Label((info?.Briefing ?? "") + "\n\n盘前:先看历史K线与盘口,准备好了再开盘。");
            brief.AddToClassList("brief");
            var begin = new Button(() =>
            {
                _levelOverlay.style.display = DisplayStyle.None;
                _host.Core.BeginTrading();
                Render(_host.Core.BuildSnapshot());
            }) { text = "开始操盘 ▶" };
            begin.AddToClassList("btn-primary");
            var wrap = new VisualElement();
            wrap.AddToClassList("level-btn");
            wrap.Add(title);
            wrap.Add(brief);
            wrap.Add(begin);
            _levelList.Add(wrap);
            _levelOverlay.style.display = DisplayStyle.Flex;
        }

        // ── 主渲染 ──

        private void Render(MarketSnapshotDto snap)
        {
            if (snap == null) return;

            // 顶栏
            string phaseText = snap.IsPreMarket ? "盘前" : snap.Phase;
            _phaseLabel.text = $"第{snap.CurrentDay}/{snap.TotalDays}天 {phaseText}";
            decimal? lp = snap.LastPrice;
            _lastPriceLabel.text = lp?.ToString("F2", C) ?? "--";
            _lastPriceBig.text = lp?.ToString("F2", C) ?? "--";
            bool up = lp.HasValue && lp.Value >= snap.PreviousClose;
            var priceColor = new StyleColor(up ? new Color(0.937f, 0.325f, 0.314f) : new Color(0.149f, 0.651f, 0.604f));
            _lastPriceLabel.style.color = priceColor;
            _lastPriceBig.style.color = priceColor;
            _sentimentLabel.text = snap.Sentiment.ToString("F0", C);
            _regulatorLabel.text = $"{snap.RegulatorHeat:F0} {snap.PenaltyLevel}";
            _regulatorLabel.EnableInClassList("warn", snap.RegulatorHeat >= 60);

            // 盘口
            RenderBook(snap);

            // 图表
            _chart?.SetData(snap.Timeshare, snap.PreviousClose);
            int cumVol = snap.Timeshare.Count > 0 ? snap.Timeshare[snap.Timeshare.Count - 1].CumVolume : 0;
            _volLabel.text = "量 " + cumVol;
            _turnoverLabel.text = "换手 " + snap.TurnoverRate.ToString("F1", C) + "%";

            // 卡牌/下单面板
            RenderCards(snap);
            _orderPanel.style.display = snap.CardState != null ? DisplayStyle.None : DisplayStyle.Flex;

            // 账户
            RenderAccount(snap);

            // 目标
            _objectivesContainer.Clear();
            foreach (var obj in snap.Objectives)
            {
                var row = new VisualElement();
                row.AddToClassList("objective-row");
                var check = new Label(obj.Achieved ? "✔" : "○") { name = "check" };
                check.AddToClassList("objective-check");
                if (!obj.Achieved) check.AddToClassList("pending");
                row.Add(check);
                row.Add(new Label($"{obj.Description} {obj.Progress:P0}"));
                _objectivesContainer.Add(row);
            }

            // 结算
            if (snap.IsLevelOver && !_resultShown) ShowResult();

            RenderTrades();
        }

        private static bool _accDiagDone;

        private void RenderBook(MarketSnapshotDto snap)
        {
            _asksContainer.Clear();
            // asks 逆序:卖5在上卖1在下
            for (int i = snap.Asks.Count - 1; i >= 0; i--) _asksContainer.Add(BookRow(snap.Asks[i], true));
            for (int i = 0; i < snap.Bids.Count && i < 5; i++) _bidsContainer.Add(BookRow(snap.Bids[i], false));   // 五档
            if (!_accDiagDone && snap.Asks.Count > 0)
            {
                _accDiagDone = true;
                _asksContainer.schedule.Execute(() =>
                {
                    var sb = new System.Text.StringBuilder("[ACCDIAG]");
                    void Dump(string name, VisualElement el)
                    {
                        sb.Append($" {name}(w={el.layout.width:F0},h={el.layout.height:F0}");
                        if (el.parent != null) sb.Append($",parentW={el.parent.layout.width:F0}");
                        int n = 0;
                        foreach (var child in el.Children())
                        {
                            sb.Append($" 子{n}(w={child.layout.width:F0},h={child.layout.height:F0},fd={child.resolvedStyle.flexDirection})");
                            if (++n >= 2) break;
                        }
                        sb.Append(")");
                    }
                    Dump("asks", _asksContainer);
                    Dump("bids", _bidsContainer);
                    if (_tradesContainer != null) Dump("trades", _tradesContainer);
                    Debug.Log(sb.ToString());
                }).StartingIn(2500);
            }
        }

        private static VisualElement BookRow(PriceLevelDto level, bool isAsk)
        {
            var row = new VisualElement();
            row.AddToClassList("book-row");
            row.AddToClassList(isAsk ? "book-ask" : "book-bid");
            var price = new Label(level.Price.ToString("F2", C)) { name = "price" };
            price.AddToClassList("price");
            var qty = new Label(level.Qty.ToString()) { name = "qty" };
            qty.AddToClassList("book-qty");
            row.Add(price);
            row.Add(qty);
            return row;
        }

        private void RenderTrades()
        {
            if (_tradesContainer == null) return;
            _tradesContainer.Clear();
            var trades = _host.RecentTrades;
            // 只挂最近 30 条,避免长跑后数百行拖垮 ScrollView 与合批
            for (int i = trades.Count - 1; i >= 0 && i >= trades.Count - 30; i--)   // 新→旧
            {
                var t = trades[i];
                var row = new VisualElement();
                row.AddToClassList("trade-row");
                var priceL = new Label(t.Price.ToString("F2", C));
                priceL.style.color = t.TakerSide == "Buy"
                    ? new Color(0.937f, 0.325f, 0.314f) : new Color(0.149f, 0.651f, 0.604f);
                row.Add(priceL);
                row.Add(new Label(t.Qty.ToString()) { style = { color = new Color(0.55f, 0.58f, 0.63f) } });
                row.Add(new Label(t.TakerSide == "Buy" ? "买" : "卖") { style = { color = new Color(0.55f, 0.58f, 0.63f) } });
                _tradesContainer.Add(row);
            }
        }

        private void RenderAccount(MarketSnapshotDto snap)
        {
            var a = snap.Account;
            _equityLabel.text = Wan(a.TotalEquity) + " 元";
            _cashLabel.text = $"现金 {Wan(a.Cash)}(可用 {Wan(a.AvailableCash)})";
            _positionLabel.text = $"持仓 {a.PositionTotal} 手(可卖 {a.PositionAvailable}/T+1锁 {a.PositionT1Locked})";
            _costLabel.text = $"成本 {a.AverageCost.ToString("F2", C)}";
            var fp = a.FloatingProfit;
            _floatLabel.text = $"浮盈 {Wan(fp)} 元";
            _floatLabel.style.color = new StyleColor(fp >= 0
                ? new Color(0.937f, 0.325f, 0.314f) : new Color(0.149f, 0.651f, 0.604f));
            _shortLabel.text = a.ShortQty > 0
                ? $"空头 {a.ShortQty} 手@{a.ShortCost.ToString("F2", C)} 担保{a.MaintenanceRatio:P0}"
                : "空头 --";
        }

        private void RenderCards(MarketSnapshotDto snap)
        {
            var cs = snap.CardState;
            if (cs == null)
            {
                _cardPanel.style.display = DisplayStyle.None;
                return;
            }
            _cardPanel.style.display = DisplayStyle.Flex;
            _turnLabel.text = $"回合 {cs.Turn + 1}/5";
            _energyLabel.text = "能量 " + new string('●', cs.Energy) + new string('○', System.Math.Max(0, cs.MaxEnergy - cs.Energy));
            _cardPhaseLabel.text = cs.Phase == "Play" ? "出牌中⏸" : "执行中▶";

            _handContainer.Clear();
            for (int i = 0; i < cs.Hand.Count; i++)
            {
                var card = cs.Hand[i];
                var index = i;
                bool canPlay = cs.Phase == "Play" && cs.CardsPlayed < cs.MaxCardsPerTurn && card.EnergyCost <= cs.Energy;
                var btn = new Button(() => PlayCard(index)) { name = "card-" + card.Id };
                btn.AddToClassList("card");
                btn.AddToClassList(card.Category.ToLowerInvariant());
                if (!canPlay) btn.AddToClassList("disabled");
                var nameRow = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween } };
                nameRow.Add(new Label(card.Name) { name = "name" });
                var cost = card.EnergyCost > 0 ? new string('⚡', card.EnergyCost) : "免费";
                nameRow.Add(new Label(cost + (card.CashCost > 0 ? " " + Wan(card.CashCost) : "")) { name = "cost" });
                var costL = nameRow.ElementAt(1);
                costL.AddToClassList("card-cost");
                var nameL = nameRow.ElementAt(0);
                nameL.AddToClassList("card-name");
                var desc = new Label(card.Description) { name = "desc" };
                desc.AddToClassList("card-desc");
                btn.Add(nameRow);
                btn.Add(desc);
                btn.SetEnabled(canPlay);
                _handContainer.Add(btn);
            }

            // 揭牌
            var ra = cs.LastAuction;
            if (ra != null)
            {
                _revealPanel.style.display = DisplayStyle.Flex;
                _revealSummary.text = ra.Volume > 0
                    ? $"第{ra.Turn + 1}回合 · 竞价价 {ra.Price.ToString("F2", C)} · 总量 {ra.Volume} 手"
                    : $"第{ra.Turn + 1}回合 · 无交叉成交,挂单保留";
                _revealTrades.Clear();
                foreach (var t in ra.Trades)
                {
                    var row = new VisualElement();
                    row.AddToClassList("reveal-row");
                    if (t.Buyer == "你" || t.Seller == "你") row.AddToClassList("mine");
                    var buyer = new Label(t.Buyer + (t.BuyerIsShort ? "·平仓" : "")) { name = "buyer" };
                    buyer.AddToClassList("reveal-buyer");
                    var mid = new Label($"{t.Qty}手 @{t.Price.ToString("F2", C)}") { name = "mid" };
                    mid.AddToClassList("reveal-mid");
                    var seller = new Label(t.Seller + (t.SellerIsShort ? "·做空" : "")) { name = "seller" };
                    seller.AddToClassList("reveal-seller");
                    row.Add(buyer);
                    row.Add(mid);
                    row.Add(seller);
                    _revealTrades.Add(row);
                }
            }
            else
            {
                _revealPanel.style.display = DisplayStyle.None;
            }
        }

        private void PlayCard(int handIndex)
        {
            bool ok = _host.Core.PlayCard(handIndex, out string error);
            _msgLabel.text = ok ? "✓ 已出牌" : "✗ " + error;
            Render(_host.Core.BuildSnapshot());
        }

        /// <summary>连续模式的快捷市价单(固定200手)。</summary>
        private void QuickOrder(string side)
        {
            var result = _host.Core.SubmitSafe(new OrderRequestDto(side, "market", null, 200));
            _msgLabel.text = result.Error ?? $"已提交:{result.Status} 成交{result.TotalFilled}";
            Render(_host.Core.BuildSnapshot());
        }

        private void SetSpeed(float speed, Button src)
        {
            _host.ticksPerSecond = speed;
            _speed1.EnableInClassList("active", src == _speed1);
            _speed3.EnableInClassList("active", src == _speed3);
            _speed10.EnableInClassList("active", src == _speed10);
        }

        private void ShowResult()
        {
            _resultShown = true;
            var r = _host.Core.EndLevelDto();
            _resultTitle.text = r.IsVictory ? $"胜利! ★{r.Stars}" : "失败 " + (r.FailureReason ?? "");
            string body = r.CoachComment + "\n\n";
            var score = _host.Core.SettleScore();
            foreach (var p in score.Rankings)
                body += $"#{p.Rank} {p.Name}: 收益{p.ReturnRate:P1} 回撤{p.MaxDrawdown:P1} → {p.Score:F0}分\n";
            _resultBody.text = body;
            _resultOverlay.style.display = DisplayStyle.Flex;
        }

        private static string Wan(decimal yuan)
            => (yuan / 10000m).ToString("F0", C) + "万";
    }
}
