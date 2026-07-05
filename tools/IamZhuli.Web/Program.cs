using IamZhuli.Simulation.Levels;
using IamZhuli.Web;

var builder = WebApplication.CreateBuilder(args);

// SignalR
builder.Services.AddSignalR();
// 游戏大脑单例 + 后台 tick 服务
builder.Services.AddSingleton<GameSingleton>();
builder.Services.AddHostedService<GameHostService>();

var app = builder.Build();

app.UseDefaultFiles();
// 开发期禁用静态文件缓存,避免改了 index.html 后浏览器仍用旧缓存
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate"
});

// —— REST 端点 ——
app.MapGet("/api/snapshot", async (GameSingleton game) =>
    Results.Ok(await game.BuildSnapshotAsync()));

app.MapPost("/api/order", async (GameSingleton game, OrderRequestDto dto) =>
    Results.Ok(await game.SubmitAsync(dto)));

app.MapPost("/api/cancel", async (GameSingleton game, long orderId) =>
    Results.Ok(new { ok = await game.CancelAsync(orderId) }));

app.MapPost("/api/cancelall", async (GameSingleton game) =>
    Results.Ok(new { ok = true, cancelled = await game.CancelAllAsync() }));

app.MapPost("/api/cancelbyside", async (GameSingleton game, string side) =>
    Results.Ok(new { ok = true, cancelled = await game.CancelBySideAsync(side) }));

app.MapPost("/api/pause", async (GameSingleton game) =>
{ await game.PauseAsync(); return Results.Ok(); });

app.MapPost("/api/resume", async (GameSingleton game) =>
{ await game.ResumeAsync(); return Results.Ok(); });

app.MapPost("/api/begin", (GameSingleton game) =>
{ game.BeginTrading(); return Results.Ok(); });

app.MapPost("/api/skipday", async (GameSingleton game) =>
{ await game.SkipDayAsync(); return Results.Ok(); });

app.MapPost("/api/nextday", async (GameSingleton game) =>
{ await game.StartNextDayAsync(); return Results.Ok(); });

app.MapGet("/api/ai", async (GameSingleton game, int? count) =>
    Results.Ok(await game.GetAIThoughtsAsync(count ?? 20)));

app.MapPost("/api/endlevel", async (GameSingleton game) =>
    Results.Ok(await game.EndLevelAsync()));

app.MapGet("/api/score", async (GameSingleton game) =>
    Results.Ok(await Task.Run(() => game.SettleScore())));

app.MapGet("/api/chips", (GameSingleton game, int? day) =>
    Results.Ok(game.GetChipHistory(day)));

app.MapPost("/api/retry", async (GameSingleton game) =>
{ await game.RetryAsync(); return Results.Ok(); });

app.MapPost("/api/loadlevel", async (GameSingleton game, string id) =>
{
    await game.LoadLevelAsync(id);
    return Results.Ok();
});

app.MapGet("/api/level", (GameSingleton game) =>
    Results.Ok(game.CurrentLevel));

// —— SignalR Hub ——
app.MapHub<GameHub>("/gamehub");

app.Run();
