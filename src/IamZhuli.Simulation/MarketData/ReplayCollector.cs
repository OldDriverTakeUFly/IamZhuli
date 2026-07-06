using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.MarketData;

/// <summary>复盘快照中的一个参与者在某时刻的持仓状态。</summary>
public sealed record ParticipantState(
    string Name, int Holding, decimal AvgCost, decimal Cash, decimal Equity);

/// <summary>关键帧快照:每 N tick 存一次的市场全貌(盘口+各方持仓+现价+监管值)。</summary>
public sealed record ReplaySnapshot(
    int TickIndex, int Day, int TickOfDay,
    decimal Price, decimal RegulatorHeat,
    List<(decimal Price, int Qty)> TopBids,    // 盘口10档(拷贝)
    List<(decimal Price, int Qty)> TopAsks,
    List<ParticipantState> Participants);

/// <summary>交易日志中的一笔(带买卖双方身份,复盘时揭示)。</summary>
public sealed record ReplayTrade(
    int TickIndex, decimal Price, int Qty,
    Side TakerSide, string TakerId, string MakerId);

/// <summary>
/// 复盘数据采集器。订阅 OnTick(每20tick存关键帧快照) + OnTradeDetailed(每笔成交存日志)。
/// 关卡结算后供前端拉取,实现时间轴拖拽回放。
/// 内存:~900快照×12KB + ~10万交易×80B ≈ 11MB + 8MB ≈ 19MB/关卡。
/// </summary>
public sealed class ReplayCollector
{
    private readonly SimulationLoop _loop;
    private readonly TradingSession _session;
    private readonly Func<IEnumerable<(string Name, Account Acc)>> _getParticipants;
    private readonly Func<decimal> _getRegulatorHeat;
    private readonly List<ReplaySnapshot> _snapshots = new();
    private readonly List<ReplayTrade> _trades = new();
    private int _tickIndex;
    private const int SnapshotInterval = 20;   // 每20tick存一次快照

    public IReadOnlyList<ReplaySnapshot> Snapshots => _snapshots;
    public IReadOnlyList<ReplayTrade> Trades => _trades;

    /// <param name="getParticipants">返回要记录的各方(玩家/AI/机构B等)的名称和账户。</param>
    /// <param name="getRegulatorHeat">返回当前监管关注值。</param>
    public ReplayCollector(SimulationLoop loop, TradingSession session,
        Func<IEnumerable<(string Name, Account Acc)>> getParticipants,
        Func<decimal> getRegulatorHeat)
    {
        _loop = loop;
        _session = session;
        _getParticipants = getParticipants;
        _getRegulatorHeat = getRegulatorHeat;
        loop.OnTick += OnTick;
        session.OnTradeDetailed += OnTradeDetailed;
    }

    private void OnTick(long totalTick)
    {
        _tickIndex = (int)totalTick;
        if (_tickIndex % SnapshotInterval != 0) return;
        CaptureSnapshot();
    }

    private void CaptureSnapshot()
    {
        var view = _session.Engine.View;
        var price = view.LastPrice?.Value ?? 0m;
        var heat = _getRegulatorHeat();
        var bids = view.TopBids(10).Select(t => (t.Price.Value, t.TotalQty.Value)).ToList();
        var asks = view.TopAsks(10).Select(t => (t.Price.Value, t.TotalQty.Value)).ToList();
        var mark = view.LastPrice ?? new Price(10m);
        var participants = _getParticipants()
            .Select(p => new ParticipantState(
                p.Name, p.Acc.Position.Total.Value, p.Acc.Position.AverageCost.Value,
                p.Acc.Cash, p.Acc.TotalEquity(mark)))
            .ToList();
        _snapshots.Add(new ReplaySnapshot(
            _tickIndex, _loop.Clock.CurrentDay, _loop.Clock.CurrentTickOfDay,
            price, heat, bids, asks, participants));
    }

    private void OnTradeDetailed(Trade t)
    {
        _trades.Add(new ReplayTrade(
            _tickIndex, t.Price.Value, t.Quantity.Value,
            t.TakerSide, t.TakerId.Value, t.MakerId.Value));
    }

    /// <summary>二分查找:找到 tickIndex 最接近(且<=)目标 tick 的快照索引。</summary>
    public int FindSnapshotIndex(int tickIndex)
    {
        if (_snapshots.Count == 0) return -1;
        int lo = 0, hi = _snapshots.Count - 1, result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_snapshots[mid].TickIndex <= tickIndex) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }
}
