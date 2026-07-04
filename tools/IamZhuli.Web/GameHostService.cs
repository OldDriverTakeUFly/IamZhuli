using Microsoft.AspNetCore.SignalR;

namespace IamZhuli.Web;

/// <summary>
/// 后台服务:用 PeriodicTimer 自动驱动 tick,并把每个 tick 后的快照 + 成交推送给前端。
/// 推送策略:每 tick 推完整快照(盘口+账户+时钟);成交由 Session.OnTrade 触发即时单独推送。
/// </summary>
public sealed class GameHostService : BackgroundService
{
    private readonly GameSingleton _game;
    private readonly IHubContext<GameHub> _hub;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);

    public GameHostService(GameSingleton game, IHubContext<GameHub> hub)
    {
        _game = game;
        _hub = hub;
        // 订阅成交事件 → 即时推送
        _game.WireEvents(t => _hub.Clients.All.SendAsync("trade", t));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        // 启动时先推一个初始快照,让前端首屏有数据
        var initSnap = await _game.BuildSnapshotAsync();
        await _hub.Clients.All.SendAsync("snapshot", initSnap, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snap = await _game.StepAsync();
            if (snap != null)
                await _hub.Clients.All.SendAsync("snapshot", snap, stoppingToken);
        }
    }
}
