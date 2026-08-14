using System;
using IamZhuli.Presenter;
using UnityEngine;

namespace IamZhuli.Unity
{
    /// <summary>
    /// GameCore 的 Unity 桥:主线程按速度档驱动 tick,快照分发给 UI。
    /// 游戏逻辑全部在 GameCore(与 Web 共用),本类只做时钟与事件。
    /// </summary>
    public sealed class GameHost : MonoBehaviour
    {
        public GameCore Core { get; } = new GameCore();

        [Range(0f, 20f)] public float ticksPerSecond = 2.5f;

        /// <summary>每次 tick 后触发(卡牌Play阶段/盘前也触发,UI 需刷新但时钟未走)。</summary>
        public event Action<MarketSnapshotDto> SnapshotUpdated;

        /// <summary>每笔成交触发(成交明细滚动条)。</summary>
        public event Action<TradeDto> TradeTick;

        private float _accumulator;
        private const int MaxTradesShown = 40;
        private readonly System.Collections.Generic.List<TradeDto> _recentTrades = new();

        void OnEnable()
        {
            Core.TradePushed += OnTrade;
        }

        void OnDisable()
        {
            Core.TradePushed -= OnTrade;
        }

        private void OnTrade(TradeDto t)
        {
            lock (_recentTrades)
            {
                _recentTrades.Add(t);
                if (_recentTrades.Count > MaxTradesShown)
                    _recentTrades.RemoveAt(0);
            }
            TradeTick?.Invoke(t);
        }

        /// <summary>最近成交(新→旧)。</summary>
        public System.Collections.Generic.IReadOnlyList<TradeDto> RecentTrades
        {
            get
            {
                lock (_recentTrades) return _recentTrades.ToArray();
            }
        }

        void Update()
        {
            if (ticksPerSecond <= 0f) return;
            _accumulator += Time.deltaTime * ticksPerSecond;
            while (_accumulator >= 1f)
            {
                _accumulator -= 1f;
                var snap = Core.Step();
                if (snap == null) { _accumulator = 0f; break; }   // 关卡结束
                SnapshotUpdated?.Invoke(snap);
                // 冻结态(盘前/卡牌出牌)不推进时钟,一帧推一次快照即可
                bool frozen = snap.IsPreMarket
                    || (snap.CardState != null && snap.CardState.Phase == "Play");
                if (frozen) { _accumulator = 0f; break; }
            }
        }
    }
}
