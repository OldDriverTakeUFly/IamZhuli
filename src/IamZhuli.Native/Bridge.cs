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
        _core!.LoadLevelById(ReadUtf8(utf8Id));
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_begin")]
    public static int Begin() { _core!.BeginTrading(); return 0; }

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
