using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.AI;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Scenarios;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Preplay;

/// <summary>预演产出:历史K线 + 昨收 + 初始情绪(参与者状态通过共享Session直接保留)。</summary>
public sealed class PreplayResult
{
    public required List<DailyCandle> HistoryCandles { get; init; }
    public decimal PreviousClose { get; init; }
    public decimal InitialSentiment { get; init; }
}

/// <summary>
/// 预演执行器。回放历史K线,让散户+AI+机构B+引导做市商 跑完整个历史。
/// 使用临时SimulationLoop + 共享Session(参与者账户状态直接保留,无需迁移)。
/// 预演结束后,引导做市商随临时loop弃用(其账户留在Session但不再Act)。
/// </summary>
public sealed class MarketPreplay
{
    /// <summary>执行预演。
    /// session/gameLoop 是游戏用的实例(共享),预演在其上跑历史。
    /// 返回预演产出的历史K线+情绪。</summary>
    public PreplayResult Run(TradingSession session, SimulationLoop gameLoop, MarketScenario scenario,
                              int retailHolding = 50000, int? seed = null)
    {
        // 1. 创建临时loop(共享session)。每天少量tick(预演快速跑完,引导做市商锚定)
        int ticksPerDay = 10;
        var tempLoop = new SimulationLoop(session.Engine, new SimulationClock(ticksPerDay, scenario.Days));

        // 2. 数据采集器(预演期间采集历史K线)
        var collector = new MarketDataCollector(tempLoop, scenario.StartPrice.Value);

        // 3. 引导做市商(锚定K线价格,同步给采集器)
        var guidance = new GuidanceMarketMaker(session, new ParticipantId("引导做市"), scenario, collector);
        tempLoop.AddParticipant(guidance);

        // 4. 真实参与者(散户池+AI+机构B)——状态保留在共享session里
        var retail = new RetailProfilePool(session, new ParticipantId("散户池"),
            scenario.EndPrice, seed ?? 42);
        tempLoop.AddParticipant(retail);
        var instB = new InstitutionB(session, new ParticipantId("机构B"), scenario.EndPrice,
            1_000_000_000m, initialHolding: 20000, baseDepthPerLevel: 80, seed: 88);
        tempLoop.AddParticipant(instB);

        // 5. 跑预演(自动跨日,不暂停)
        tempLoop.Start();
        while (!tempLoop.IsFinished)
        {
            if (tempLoop.IsDayClosed)
                tempLoop.PreplayAdvanceDay();
            else
                tempLoop.Step();
        }

        // 6. 产出
        return new PreplayResult
        {
            HistoryCandles = collector.DailyCandles.ToList(),
            PreviousClose = collector.PreviousClose,
            InitialSentiment = retail.Sentiment.Value
        };
    }
}
