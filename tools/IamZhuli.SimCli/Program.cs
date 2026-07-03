using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.SimCli;

/// <summary>
/// 命令行模拟器(M2 版)。只有玩家一个参与者,验证盘口/账户/T+1/tick 推进。
/// 命令:
///   buy  <price> <qty>        限价买   | buy m <qty>      市价买
///   sell <price> <qty>        限价卖   | sell m <qty>     市价卖
///   cancel <orderId>          撤单
///   tick [n]                  推进 n 个 tick(默认1)
///   day                       跳到下一交易日(触发 T+1 解锁)
///   pause / resume            暂停/恢复
///   book                      显示五档盘口
///   me                        显示账户
///   help                      帮助
///   quit                      退出
/// </summary>
internal static class Program
{
    private static readonly ParticipantId Player = new("Player");
    private static readonly ParticipantId MarketMaker = new("做市商"); // 提供初始挂单的NPC

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 我是主力 · SimCli (M2) ===");
        Console.WriteLine("输入 help 查看命令。初始:做市商已挂出五档盘口,你有 1 亿资金。\n");

        var rules = new MarketRules { PreviousClose = new Price(10.00m), PriceLimitRatio = 0.10m };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay: 30, totalDays: 30));
        var playerAcc = loop.Session.GetOrCreateAccount(Player, 100_000_000m);
        var mmAcc = loop.Session.GetOrCreateAccount(MarketMaker, 1_000_000_000m);

        // 给做市商预置持仓(视作昨日已有,用于能挂卖单)
        mmAcc.Position.Seed(new Quantity(100000), new Price(10.00m));

        // 做市商挂出初始五档
        SeedMarket(loop.Session);
        loop.Session.OnTrade += (p, q, s) =>
            Console.WriteLine($"   成交 {q} @ {p} ({s})");
        loop.Start();

        PrintBook(loop.Session);
        PrintStatus(loop, playerAcc);

        while (true)
        {
            Console.Write($"\n[{loop.Clock}] > ");
            var line = Console.ReadLine();
            if (line == null) break;
            try { if (!HandleCommand(line.Trim(), loop, playerAcc)) break; }
            catch (Exception ex) { Console.WriteLine($"  错误: {ex.Message}"); }
        }
    }

    private static bool HandleCommand(string cmd, SimulationLoop loop, Account player)
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        switch (parts[0].ToLowerInvariant())
        {
            case "quit": case "exit": return false;
            case "help":
                Console.WriteLine("buy <price> <qty> | buy m <qty> | sell <price> <qty> | sell m <qty>");
                Console.WriteLine("cancel <orderId> | tick [n] | day | pause | resume | book | me | quit");
                break;
            case "buy": DoOrder(loop, player, Side.Buy, parts); break;
            case "sell": DoOrder(loop, player, Side.Sell, parts); break;
            case "cancel": {
                if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
                { Console.WriteLine("用法: cancel <orderId>"); break; }
                bool ok = loop.Session.Cancel(Player, new OrderId(id));
                Console.WriteLine(ok ? "  已撤单" : "  撤单失败(订单不存在或已成交)");
                PrintBook(loop.Session); break;
            }
            case "tick": {
                int n = parts.Length >= 2 && int.TryParse(parts[1], out var x) ? x : 1;
                for (int i = 0; i < n && !loop.IsFinished; i++) loop.Step();
                PrintStatus(loop, player); break;
            }
            case "day": loop.SkipToNextDay(); PrintStatus(loop, player); break;
            case "pause": loop.Pause(); Console.WriteLine("  已暂停"); break;
            case "resume": loop.Resume(); Console.WriteLine("  已恢复"); break;
            case "book": PrintBook(loop.Session); break;
            case "me": PrintStatus(loop, player); break;
            default: Console.WriteLine($"  未知命令: {parts[0]} (输入 help)"); break;
        }
        return true;
    }

    private static void DoOrder(SimulationLoop loop, Account player, Side side, string[] parts)
    {
        if (parts.Length < 3) { Console.WriteLine($"用法: {parts[0]} <price> <qty> 或 {parts[0]} m <qty>"); return; }
        OrderRequest req;
        if (parts[1].Equals("m", StringComparison.OrdinalIgnoreCase))
        {
            int qty = int.Parse(parts[2]);
            req = new OrderRequest(Player, side, OrderType.Market, Price.Zero, new Quantity(qty));
        }
        else
        {
            decimal price = decimal.Parse(parts[1]);
            int qty = int.Parse(parts[2]);
            req = new OrderRequest(Player, side, OrderType.Limit, new Price(price), new Quantity(qty));
        }
        var result = loop.Session.Submit(req);
        Console.WriteLine($"  订单#{result.OrderId}: {result.FinalStatus} " +
            $"成交{result.TotalFilled} 均价{result.AverageFillPrice} 剩{result.RemainingQty}");
        PrintBook(loop.Session);
    }

    private static void SeedMarket(TradingSession s)
    {
        // 做市商挂五档买卖,围绕 10.00
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.05m), new Quantity(500)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.04m), new Quantity(300)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.03m), new Quantity(200)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.02m), new Quantity(100)));
        s.Submit(new OrderRequest(MarketMaker, Side.Sell, OrderType.Limit, new Price(10.01m), new Quantity(50)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.99m), new Quantity(50)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.98m), new Quantity(100)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.97m), new Quantity(200)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.96m), new Quantity(300)));
        s.Submit(new OrderRequest(MarketMaker, Side.Buy, OrderType.Limit, new Price(9.95m), new Quantity(500)));
    }

    private static void PrintBook(TradingSession s)
    {
        var asks = s.Engine.View.TopAsks(5);
        var bids = s.Engine.View.TopBids(5);
        Console.WriteLine("\n  ── 盘口五档 ─────────────");
        for (int i = asks.Count - 1; i >= 0; i--)
            Console.WriteLine($"  卖{asks.Count - i}  {asks[i].Price,7:F2}  {Bar(asks[i].TotalQty.Value),-12} {asks[i].TotalQty}");
        Console.WriteLine($"  现价  {s.Engine.View.LastPrice?.ToString() ?? "--",7}");
        for (int i = 0; i < bids.Count; i++)
            Console.WriteLine($"  买{i + 1}  {bids[i].Price,7:F2}  {Bar(bids[i].TotalQty.Value),-12} {bids[i].TotalQty}");
        Console.WriteLine("  ──────────────────────────");
    }

    private static void PrintStatus(SimulationLoop loop, Account player)
    {
        var mark = loop.Session.Engine.View.LastPrice ?? new Price(10.00m);
        Console.WriteLine($"  {player}  权益{player.TotalEquity(mark) / 10000:F2}万  浮盈{player.Position.FloatingProfit(mark) / 10000:F2}万");
    }

    private static string Bar(int qty)
    {
        int n = Math.Min(20, qty / 50);
        return new string('█', n);
    }
}
