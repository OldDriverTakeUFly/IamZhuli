using Xunit;
using System.Text.Json;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// SaveJson 手写通道(Unity/Mono 用)测试。兼容契约:与 ASP.NET System.Text.Json
/// 落盘的存档文件互相可读(Web 壳的 slot 文件 ↔ 本地文件);decimal 精度无损;
/// 转义完备;坏档干净报错。
/// </summary>
public sealed class SaveJsonTests
{
    private readonly ITestOutputHelper _out;
    public SaveJsonTests(ITestOutputHelper o) => _out = o;

    // ── 对局工具(与 SaveLoadTests 同模型) ──

    private static SaveData SaveAtDayClose(int seed = 5566)
    {
        var core = new GameCore(seed: seed);
        core.LoadLevelById("accumulate");
        core.BeginTrading();
        core.SetIntent("accumulate", out _);
        for (int guard = 0; guard < 3000 && !core.Loop.IsDayClosed && !core.Loop.IsFinished; guard++)
        {
            if (core.BuildSnapshot().CardState is { Phase: "Play" } and { CardsPlayed: 0 })
                core.EndCardTurn();
            core.Step();
        }
        Assert.True(core.CanSave, "应推到日切边界");
        return core.ExportSave();
    }

    // ── 兼容契约:与 System.Text.Json 双向互读 ──

    [Fact]
    public void RealSave_MyFile_IsReadableBySystemTextJson()
    {
        var save = SaveAtDayClose();
        string mine = SaveJson.Serialize(save);
        var viaStj = JsonSerializer.Deserialize<SaveData>(mine);
        Assert.NotNull(viaStj);
        Assert.Equal(save.LevelId, viaStj!.LevelId);
        Assert.Equal(save.Day, viaStj.Day);
        Assert.Equal(save.LastPrice, viaStj.LastPrice);
        Assert.Equal(save.Player.Cash, viaStj.Player.Cash);
        Assert.Equal(save.Player.AvgCost, viaStj.Player.AvgCost);
        Assert.Equal(save.Cards.Hand, viaStj.Cards.Hand);
        Assert.Equal(save.Cards.Energy, viaStj.Cards.Energy);
        Assert.Equal(save.Sentiment.Value, viaStj.Sentiment.Value);
        Assert.Equal(save.News.Count, viaStj.News.Count);
        Assert.Equal(save.Candles.Count, viaStj.Candles.Count);
        Assert.Equal(save.Intent, viaStj.Intent);
        Assert.Equal(save.WaterArmy.Level, viaStj.WaterArmy.Level);
    }

    [Fact]
    public void RealSave_SystemTextJsonFile_IsReadableBySaveJson()
    {
        var save = SaveAtDayClose();
        // 与 GameSingleton.SaveToSlotAsync 同参数(默认选项,PascalCase)
        string stjFile = JsonSerializer.Serialize(save);
        var mine = SaveJson.Parse(stjFile);
        Assert.Equal(save.LevelId, mine.LevelId);
        Assert.Equal(save.Day, mine.Day);
        Assert.Equal(save.LastPrice, mine.LastPrice);
        Assert.Equal(save.Player.Cash, mine.Player.Cash);
        Assert.Equal(save.Player.ShortCost, mine.Player.ShortCost);
        Assert.Equal(save.Cards.Hand, mine.Cards.Hand);
        Assert.Equal(save.Cards.Draw, mine.Cards.Draw);
        Assert.Equal(save.Cards.Energy, mine.Cards.Energy);
        Assert.Equal(save.Cards.Turn, mine.Cards.Turn);
        Assert.Equal(save.Intent, mine.Intent);
        Assert.Equal(save.Sentiment.Confidence, mine.Sentiment.Confidence);
        Assert.Equal(save.News.Count, mine.News.Count);
        if (save.News.Count > 0) Assert.Equal(save.News[0].Headline, mine.News[0].Headline);
        Assert.Equal(save.Candles.Count, mine.Candles.Count);
        if (save.Candles.Count > 0)
            Assert.Equal(save.Candles[^1].Close, mine.Candles[^1].Close);
    }

    [Fact]
    public void Serialize_IsIdempotent_AfterParseRoundtrip()
    {
        var save = SaveAtDayClose(seed: 77);
        string s1 = SaveJson.Serialize(save);
        string s2 = SaveJson.Serialize(SaveJson.Parse(s1));
        Assert.Equal(s1, s2);   // 解析无信息丢失
        _out.WriteLine($"存档 {s1.Length / 1024:F1} KB");
    }

    // ── 数值/转义边界 ──

    [Fact]
    public void AwkwardValues_SurviveRoundtrip()
    {
        var save = new SaveData
        {
            LevelId = " accumulate\"\\\n\t中文名关卡 ",
            Day = int.MaxValue,
            LastPrice = 123456789012345678901234567m,
            PreviousClose = -0.000000000000000000000000001m,
            Player = new AccountSave(-9876543210.123456789m, int.MinValue, int.MaxValue,
                10.005m, 1, -3.14159265358979m, 0m, int.MaxValue, 0),
            Ai = new AccountSave(0m, 0, 0, 0m, 0, 0m, 0m, 0, 0),
            Institution = new AccountSave(1m, 1, 1, 1m, 1, 1m, 1m, 1, 1),
            AiState = "测试状态",
            InstRiskLevel = "High",
            Cards = new CardEngineSave(
                new List<string> { "做空突袭", "quote\"card", "back\\slash" },
                new List<string>(),   // 空抽牌堆
                new List<string> { "散布利空" },
                12, 11, true),
            Intent = null,   // null 意图
            Heat = 99.9m, InfoHeat = 0.1m,
            ShortablePool = -1, TotalShortable = 999999,
            Sentiment = new SentimentSave(0.12345678901234567890123456m, -1m, 1m, 0.5m, -0.5m),
            WaterArmy = new WaterArmySave(3, true, 7),
            News = new List<SavedNewsDto>
            {
                new("Positive", "标题带\"引号\"与\\反斜杠\n换行😀", -0.25m, 12345),
                new("Pump", "纯中文消息", 0.1m, 0),
            },
            Candles = new List<CandleDto>(),   // 空K线
        };
        string json = SaveJson.Serialize(save);
        var back = SaveJson.Parse(json);

        Assert.Equal(save.LevelId, back.LevelId);
        Assert.Equal(int.MaxValue, back.Day);
        Assert.Equal(save.LastPrice, back.LastPrice);
        Assert.Equal(save.PreviousClose, back.PreviousClose);
        Assert.Equal(save.Player.Cash, back.Player.Cash);
        Assert.Equal(save.Player.Available, back.Player.Available);
        Assert.Equal(save.Player.T1Locked, back.Player.T1Locked);
        Assert.Equal(save.Player.ShortCost, back.Player.ShortCost);
        Assert.Equal(save.Cards.Hand, back.Cards.Hand);
        Assert.Empty(back.Cards.Draw);
        Assert.Equal(12, back.Cards.Energy);
        Assert.True(back.Cards.CustomDeck);
        Assert.Null(back.Intent);
        Assert.Equal(save.Sentiment.Value, back.Sentiment.Value);
        Assert.Equal(save.News[0].Headline, back.News[0].Headline);
        Assert.Equal(save.News[1].Impact, back.News[1].Impact);
        Assert.Empty(back.Candles);
        // 幂等
        Assert.Equal(json, SaveJson.Serialize(back));
    }

    [Fact]
    public void WebStyleFile_WithUnicodeEscapes_Parses()
    {
        // STJ 默认编码器会把非 ASCII 转成 \uXXXX——手写最小样本验证转义解码
        const string stjStyle = "{\"LevelId\":\"accumulate\",\"Day\":2,\"LastPrice\":9.89," +
            "\"PreviousClose\":9.8,\"Player\":{\"Cash\":150000000,\"Available\":100,\"T1Locked\":0," +
            "\"AvgCost\":0,\"ShortQty\":0,\"ShortCost\":0,\"Margin\":0,\"Bought\":0,\"Sold\":0}," +
            "\"Ai\":{\"Cash\":100000000,\"Available\":0,\"T1Locked\":0,\"AvgCost\":0," +
            "\"ShortQty\":0,\"ShortCost\":0,\"Margin\":0,\"Bought\":0,\"Sold\":0}," +
            "\"Institution\":{\"Cash\":1000000000,\"Available\":0,\"T1Locked\":0,\"AvgCost\":0," +
            "\"ShortQty\":0,\"ShortCost\":0,\"Margin\":0,\"Bought\":0,\"Sold\":0}," +
            "\"AiState\":\"Accumulate\",\"InstRiskLevel\":\"Low\"," +
            "\"Cards\":{\"Hand\":[\"\\u505a\\u7a7a\\u7a81\\u88ad\"],\"Draw\":[],\"Discard\":[]," +
            "\"Energy\":4,\"Turn\":0,\"CustomDeck\":false}," +
            "\"Intent\":null,\"Heat\":10.5,\"InfoHeat\":0,\"ShortablePool\":5000000," +
            "\"TotalShortable\":5000000," +
            "\"Sentiment\":{\"Value\":0.5,\"Target\":0.5,\"Confidence\":0.6,\"Herd\":0.4,\"Bias\":0}," +
            "\"WaterArmy\":{\"Level\":0,\"Active\":false,\"ActiveDays\":0}," +
            "\"News\":[],\"Candles\":[]}";
        var s = SaveJson.Parse(stjStyle);
        Assert.Equal("accumulate", s.LevelId);
        Assert.Equal(2, s.Day);
        Assert.Equal(9.89m, s.LastPrice);
        Assert.Equal("做空突袭", s.Cards.Hand[0]);   // \u505a\u7a7a\u7a81\u88ad
        Assert.Null(s.Intent);
        Assert.Equal(10.5m, s.Heat);
    }

    // ── 语义等价:JSON 往返后读档可玩 ──

    [Fact]
    public void LoadFromSave_AfterSaveJsonRoundtrip_MatchesOriginal()
    {
        var save = SaveAtDayClose(seed: 2024);
        var back = SaveJson.Parse(SaveJson.Serialize(save));

        var b = new GameCore(seed: 2024);
        Assert.True(b.LoadFromSave(back));

        // 玩家层精确恢复(与 SaveLoadTests 同口径)
        var snap = b.BuildSnapshot();
        Assert.Equal(save.Day, snap.CurrentDay);
        Assert.Equal(save.Cards.Energy, snap.CardState!.Energy);
        Assert.Equal(save.Cards.Hand.Count, snap.CardState.Hand.Count);
        Assert.Equal(save.Player.Cash, snap.Account.Cash);
        Assert.Equal(save.Player.Available + save.Player.T1Locked, snap.Account.PositionTotal);
        // 读档局健康可玩:跑3段
        for (int s = 0; s < 3; s++)
        {
            if (snap.CardState is { Phase: "Play" } and { CardsPlayed: 0 }) b.EndCardTurn();
            for (int i = 0; i < 400; i++)
            {
                if (b.Loop.IsFinished || b.IsLevelOver) break;
                var st = b.Step();
                if (st?.CardState is { Phase: "Play" }) break;
            }
        }
        Assert.True(b.BuildSnapshot().LastPrice > 0, "读档后应能继续交易");
    }

    // ── 坏档健壮性 ──

    [Theory]
    [InlineData("")]                       // 空
    [InlineData("{")]                      // 截断
    [InlineData("{\"LevelId\":123}")]      // 类型错
    [InlineData("{\"Day\":1}")]            // 缺字段
    [InlineData("{} trailing")]            // 尾部垃圾
    [InlineData("[1,2,3]")]                // 根不是对象
    public void Malformed_ThrowsFormatException(string bad)
        => Assert.Throws<FormatException>(() => SaveJson.Parse(bad));
}
