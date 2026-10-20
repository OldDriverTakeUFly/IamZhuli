using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IamZhuli.Presenter;

namespace IamZhuli.Native;

/// <summary>FFI 桥结果(与端点响应同构)。</summary>
public record PlayResult(bool Ok, string Error);

/// <summary>JSON source-gen 上下文(NativeAOT 无反射序列化)。</summary>
[JsonSerializable(typeof(MarketSnapshotDto))]
[JsonSerializable(typeof(AuctionResultDto))]
[JsonSerializable(typeof(LevelResultDto))]
[JsonSerializable(typeof(ScoreSettlement))]
[JsonSerializable(typeof(List<AIDto>))]
[JsonSerializable(typeof(PlayResult))]
[JsonSerializable(typeof(SaveData))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class BridgeJsonContext : JsonSerializerContext { }

/// <summary>
/// C ABI 导出(Flutter dart:ffi / python ctypes 同模型):
/// UTF8 JSON 进出;返回的内存在 Dart 侧复制后必须调 ig_free 释放。
/// 单实例语义(单机游戏);ig_init 重置全局。
/// </summary>
internal static unsafe class Bridge
{
    private static GameCore? _core;

    [UnmanagedCallersOnly(EntryPoint = "ig_init")]
    public static int Init(int seed) { _core = new GameCore(seed); return 0; }

    [UnmanagedCallersOnly(EntryPoint = "ig_load_level")]
    public static int LoadLevel(byte* utf8Id)
    {
        try { _core!.LoadLevelById(ReadUtf8(utf8Id)); return 0; }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_begin")]
    public static int Begin() { try { _core!.BeginTrading(); return 0; } catch { return -1; } }

    /// <summary>推进一步;关卡结束返回null。返回快照JSON(Dart复制后ig_free)。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ig_step")]
    public static byte* Step() { try { return ReturnJson(_core!.Step()); } catch (Exception ex) { return ReturnJson(new PlayResult(false, ex.Message)); } }

    [UnmanagedCallersOnly(EntryPoint = "ig_snapshot")]
    public static byte* Snapshot() => ReturnJson(_core!.BuildSnapshot());

    [UnmanagedCallersOnly(EntryPoint = "ig_play_card")]
    public static byte* PlayCard(int handIndex)
    {
        bool ok = _core!.PlayCard(handIndex, out var error);
        return ReturnJson(new PlayResult(ok, error));
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_end_turn")]
    public static byte* EndTurn() { _core!.EndCardTurn(); return ReturnJson(_core.LastAuction); }

    [UnmanagedCallersOnly(EntryPoint = "ig_next_day")]
    public static int NextDay() { try { _core!.StartNextDay(); return 0; } catch { return -1; } }

    /// <summary>结束关卡并结算(幂等:已结算返回缓存结果)。返回 LevelResultDto JSON。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ig_end_level")]
    public static byte* EndLevel()
    { try { return ReturnJson(_core!.EndLevelDto()); } catch { return null; } }

    /// <summary>积分结算(未结算会先 EndLevel)。返回 ScoreSettlement JSON。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ig_score")]
    public static byte* Score()
    { try { return ReturnJson(_core!.SettleScore()); } catch { return null; } }

    // —— 信息战(消息/水军/信号;盘后与卡牌舆论牌共用底层,时机由 GameCore 闸门) ——

    [UnmanagedCallersOnly(EntryPoint = "ig_publish_news")]
    public static byte* PublishNews(byte* utf8Type)
    {
        try { bool ok = _core!.PublishNews(ReadUtf8(utf8Type), out var error); return ReturnJson(new PlayResult(ok, error)); }
        catch { return null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_waterarmy_upgrade")]
    public static byte* WaterArmyUpgrade()
    {
        try { bool ok = _core!.UpgradeWaterArmy(out var error); return ReturnJson(new PlayResult(ok, error)); }
        catch { return null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_waterarmy_toggle")]
    public static byte* WaterArmyToggle()
    {
        try { bool ok = _core!.ToggleWaterArmy(out var error); return ReturnJson(new PlayResult(ok, error)); }
        catch { return null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_publish_signal")]
    public static byte* PublishSignal(byte* utf8Type)
    {
        try { bool ok = _core!.PublishSignal(ReadUtf8(utf8Type), out var error); return ReturnJson(new PlayResult(ok, error)); }
        catch { return null; }
    }

    /// <summary>AI主力最近内心独白(新→旧)。返回 AIDto 列表 JSON。</summary>
    [UnmanagedCallersOnly(EntryPoint = "ig_ai_thoughts")]
    public static byte* AiThoughts(int count)
    { try { return ReturnJson(_core!.GetAIThoughts(count)); } catch { return null; } }

    [UnmanagedCallersOnly(EntryPoint = "ig_set_intent")]
    public static byte* SetIntent(byte* utf8)
    {
        bool ok = _core!.SetIntent(ReadUtf8(utf8), out var error);
        return ReturnJson(new PlayResult(ok, error));
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_export_save")]
    public static byte* ExportSave() => ReturnJson(_core!.ExportSave());

    [UnmanagedCallersOnly(EntryPoint = "ig_import_save")]
    public static byte* ImportSave(byte* utf8Json)
    {
        try
        {
            var save = JsonSerializer.Deserialize<SaveData>(ReadUtf8(utf8Json), BridgeJsonContext.Default.SaveData)!;
            _core!.LoadFromSave(save);
            return ReturnJson(new PlayResult(true, ""));
        }
        catch (Exception ex) { return ReturnJson(new PlayResult(false, ex.Message)); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_free")]
    public static void Free(byte* p) => Marshal.FreeHGlobal((IntPtr)p);

    // —— UTF8 编解码辅助 ——
    private static string ReadUtf8(byte* p)
    {
        if (p == null) return "";
        int len = 0;
        while (p[len] != 0) len++;
        return Encoding.UTF8.GetString(p, len);
    }

    private static byte* ReturnJson<T>(T? value)
    {
        if (value == null) return null;
        string json = JsonSerializer.Serialize(value, typeof(T), BridgeJsonContext.Default);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return (byte*)p;
    }
}
