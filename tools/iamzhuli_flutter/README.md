# IamZhuli Flutter 客户端(参考 PlanForge 架构)

架构(借PlanForge模式: 单ChangeNotifier拉取式刷新,数据量小够用):
- lib/engine/bindings.dart — dart:ffi 绑定 ig_* C ABI(UTF8 JSON)
- lib/state/game_state.dart — GameState(ChangeNotifier): tick定时器/出牌/意图
- lib/screens/ — 交易/数据/账户三页(NavigationBar,对齐Web三页式)
- bin/engine_smoke.dart — dart run 冒烟(VM直连FFI,完整游戏循环+存档)

运行与验证:
- dart run bin/engine_smoke.dart        # FFI全链路(需先publish .so,见IamZhuli.Native/README)
- flutter analyze                        # 零error
- flutter run -d linux                   # 需gtk工具链(本机暂缺)
- flutter build apk --debug              # Android SDK(~/Android/Sdk)+JDK21 → APK ✓

边界(诚实): APK内暂无arm64版.so(NativeAOT Android嵌入仍实验性)——
UI壳与bindings已就绪,arm64逻辑库等官方转正后放入即可。
