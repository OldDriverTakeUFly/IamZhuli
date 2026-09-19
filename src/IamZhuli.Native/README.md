# IamZhuli.Native — Flutter/FFI 桥

C ABI 导出(UTF8 JSON进出;返回内存宿主复制后调 ig_free):
ig_init(seed) ig_load_level(id) ig_begin() ig_step() ig_snapshot()
ig_play_card(i) ig_end_turn() ig_set_intent(id) ig_export_save() ig_import_save(json) ig_free(p)

构建: bash tools/portable-clang.sh && source ~/clang-portable/portable-clang-env.sh
      dotnet publish src/IamZhuli.Native -c Release -r linux-x64
产物: bin/Release/net8.0/linux-x64/publish/IamZhuli.Native.so
冒烟: python3 /tmp/bridge-smoke.py 模型(ctypes与dart:ffi同模型)
注意: Android/iOS 的 NativeAOT 库嵌入仍属实验性——手机端等官方支持或走Unity/Avalonia。
