using IamZhuli.Web;

var builder = WebApplication.CreateBuilder(args);

// SignalR
builder.Services.AddSignalR();
// 游戏大脑单例 + 后台 tick 服务
builder.Services.AddSingleton<GameSingleton>();
builder.Services.AddHostedService<GameHostService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// —— REST 端点 ——
app.MapGet("/api/snapshot", async (GameSingleton game) =>
    Results.Ok(await game.BuildSnapshotAsync()));

app.MapPost("/api/order", async (GameSingleton game, OrderRequestDto dto) =>
    Results.Ok(await game.SubmitAsync(dto)));

app.MapPost("/api/cancel", async (GameSingleton game, long orderId) =>
    Results.Ok(new { ok = await game.CancelAsync(orderId) }));

app.MapPost("/api/pause", async (GameSingleton game) =>
{ await game.PauseAsync(); return Results.Ok(); });

app.MapPost("/api/resume", async (GameSingleton game) =>
{ await game.ResumeAsync(); return Results.Ok(); });

app.MapPost("/api/skipday", async (GameSingleton game) =>
{ await game.SkipDayAsync(); return Results.Ok(); });

// —— SignalR Hub ——
app.MapHub<GameHub>("/gamehub");

app.Run();
