using IamZhuli.Presenter;
using IamZhuli.Simulation.Levels;
using Microsoft.AspNetCore.SignalR;

namespace IamZhuli.Web;

/// <summary>
/// GameCore 的 Web 薄壳:信号量异步锁(HTTP 请求线程与后台 tick 线程互斥)
/// + SignalR 推送。游戏逻辑全部委托给引擎无关的 GameCore(Presenter 类库)。
/// </summary>
public sealed class GameSingleton
{
    private readonly GameCore _core = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IHubContext<GameHub> Hub { get; }

    // —— 直通属性(读侧无需持锁:引用/值读取原子) ——
    public bool CardMode { get => _core.CardMode; set => _core.CardMode = value; }
    public bool IsInitialized => _core.IsInitialized;
    public bool IsPreMarket => _core.IsPreMarket;
    public bool IsLevelOver => _core.IsLevelOver;
    public object CurrentLevel => _core.CurrentLevel;
    public AuctionResultDto? LastAuction => _core.LastAuction;

    public GameSingleton(IHubContext<GameHub> hub)
    {
        Hub = hub;
    }

    /// <summary>订阅成交事件 → SignalR 推送(由 GameHostService 启动时调用一次)。</summary>
    public void WireEvents(Func<TradeDto, Task> pushTrade)
        => _core.TradePushed += t => { try { _ = pushTrade(t); } catch { } };

    /// <summary>推进一个 tick 并返回该 tick 后的快照。一次持锁完成 Step+快照。</summary>
    public async Task<MarketSnapshotDto?> StepAsync()
    {
        await _gate.WaitAsync();
        try { return _core.Step(); }
        finally { _gate.Release(); }
    }

    /// <summary>锁内构建完整快照(供推送/初始加载)。</summary>
    public async Task<MarketSnapshotDto> BuildSnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return _core.BuildSnapshot(); }
        finally { _gate.Release(); }
    }

    // —— 下单/撤单/时钟(全部持锁) ——

    public async Task<OrderResultDto> SubmitAsync(OrderRequestDto dto)
    {
        await _gate.WaitAsync();
        try { return _core.SubmitSafe(dto); }
        finally { _gate.Release(); }
    }

    public async Task<bool> CancelAsync(long orderId)
    {
        await _gate.WaitAsync();
        try { return _core.Cancel(orderId); }
        finally { _gate.Release(); }
    }

    public async Task<int> CancelAllAsync()
    {
        await _gate.WaitAsync();
        try { return _core.CancelAll(); }
        finally { _gate.Release(); }
    }

    public async Task<int> CancelBySideAsync(string side)
    {
        await _gate.WaitAsync();
        try { return _core.CancelBySide(side); }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try { _core.Pause(); }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try { _core.Resume(); }
        finally { _gate.Release(); }
    }

    public async Task SkipDayAsync()
    {
        await _gate.WaitAsync();
        try { _core.SkipDay(); }
        finally { _gate.Release(); }
    }

    public async Task StartNextDayAsync()
    {
        await _gate.WaitAsync();
        try { _core.StartNextDay(); }
        finally { _gate.Release(); }
    }

    /// <summary>按 ID 切换关卡并重新加载。</summary>
    public async Task LoadLevelAsync(string id)
    {
        await _gate.WaitAsync();
        try { _core.LoadLevelById(id); }
        finally { _gate.Release(); }
    }

    /// <summary>直接加载关卡定义(测试/内部用)。</summary>
    public async Task LoadLevelAsync(LevelDefinition level)
    {
        await _gate.WaitAsync();
        try { _core.LoadLevel(level); }
        finally { _gate.Release(); }
    }

    public void BeginTrading() => _core.BeginTrading();

    public async Task<LevelResultDto> EndLevelAsync()
    {
        await _gate.WaitAsync();
        try { return _core.EndLevelDto(); }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync()
    {
        await _gate.WaitAsync();
        try { _core.Retry(); }
        finally { _gate.Release(); }
    }

    // —— 操盘意图(战略层:与卡牌同区,Play阶段无锁竞争) ——
    public bool SetIntent(string id, out string error) => _core.SetIntent(id, out error);

    // —— 卡牌模式(玩家操作;tick 在 Play 阶段冻结,不与 Step 竞争,直调即可) ——

    public bool PlayCard(int handIndex, out string error) => _core.PlayCard(handIndex, out error);

    public void EndCardTurn() => _core.EndCardTurn();

    public bool SetDeck(List<string> cardNames, out string error) => _core.SetDeck(cardNames, out error);

    // —— 盘后操作(日终暂停时无 tick 竞争) ——

    public bool PublishNews(string newsType, out string error) => _core.PublishNews(newsType, out error);

    public bool UpgradeWaterArmy(out string error) => _core.UpgradeWaterArmy(out error);

    public bool ToggleWaterArmy(out string error) => _core.ToggleWaterArmy(out error);

    public bool PublishSignal(string signalType, out string error) => _core.PublishSignal(signalType, out error);

    // —— 查询 ——

    public ScoreSettlement SettleScore() => _core.SettleScore();

    public List<DayChipDto> GetChipHistory(int? day = null) => _core.GetChipHistory(day);

    public ReplayDataDto GetReplayData() => _core.GetReplayData();

    public async Task<List<AIDto>> GetAIThoughtsAsync(int count = 20)
    {
        await _gate.WaitAsync();
        try { return _core.GetAIThoughts(count); }
        finally { _gate.Release(); }
    }
}
