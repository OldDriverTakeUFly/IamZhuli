using Microsoft.AspNetCore.SignalR;

namespace IamZhuli.Web;

/// <summary>SignalR Hub。客户端连接后被动接收推送(无客户端→服务端调用)。
/// 服务端通过 IHubContext&lt;GameHub&gt; 主动推送 snapshot/trade。
/// </summary>
public sealed class GameHub : Hub
{
    // 空壳:所有推送由 GameSingleton/GameHostService 经 IHubContext 发起。
    // 客户端只需 connection.on("snapshot", ...) / connection.on("trade", ...)。
}
