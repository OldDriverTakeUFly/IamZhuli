// 全局状态(单ChangeNotifier)。
// 刷新模型(修"UI滞后一拍"):
//  1. 常驻轮询——boot即起700ms定时器,服务端自走段推进(Resolve倒计时→下一段)
//     无需用户操作即可见;此前只在操作后refresh,段推进要等下一拍才显示。
//  2. 响应序号守卫——轮询与操作后的refresh并发时,旧响应后到会把快照覆盖回
//     操作前(闪回),只接受最新发出的请求的响应。
//  3. ▶/⏸语义——服务端驱动(HTTP):暂停/恢复服务端游戏时钟,UI持续轮询保持活;
//     本地FFI:启停本地step定时器。
library;
import 'dart:async';
import 'package:flutter/foundation.dart';
import '../engine/game_client.dart';

class GameState extends ChangeNotifier {
  final GameClient engine;
  GameState(this.engine) {
    _autoRun = engine.serverDriven;   // HTTP:服务器默认自走;FFI:默认不推进
    _poll = Timer.periodic(const Duration(milliseconds: 700), (_) => _pollOnce());
  }

  Map<String, dynamic> snap = {};
  Timer? _poll;
  Timer? _stepTimer;      // 仅本地FFI引擎:自动推进
  late bool _autoRun;
  String lastMsg = '';
  int _fetchSeq = 0;      // 响应乱序守卫

  Map<String, dynamic>? get cs => snap['cardState'] as Map<String, dynamic>?;
  Map<String, dynamic>? get acct => snap['account'] as Map<String, dynamic>?;
  bool get inPlay => (cs?['phase'] as String?) == 'Play';
  bool get autoRun => _autoRun;

  Future<void> startNewGame(String levelId) async {
    await engine.loadLevel(levelId);
    await engine.begin();
    await engine.setClockRunning(_autoRun);   // 对齐服务端时钟与本地假设态(上次可能停在暂停)
    await refresh();
  }

  /// 拉取最新快照。多请求并发时只认最新序号的响应。
  Future<void> refresh() async {
    final seq = ++_fetchSeq;
    final s = await engine.snapshot();
    if (seq != _fetchSeq) return;   // 已有更新请求:丢弃旧响应,防覆盖回退
    snap = s;
    notifyListeners();
  }

  void _pollOnce() {
    unawaited(refresh().catchError((_) {}));   // 网络闪断忽略,下轮再试
  }

  void toggleAuto() {
    _autoRun = !_autoRun;
    if (engine.serverDriven) {
      unawaited(engine.setClockRunning(_autoRun).catchError((_) {}));
    } else {
      _stepTimer?.cancel();
      if (_autoRun) {
        _stepTimer = Timer.periodic(const Duration(milliseconds: 600), (_) => _stepOnce());
      }
    }
    notifyListeners();
  }

  Future<void> _stepOnce() async {
    try {
      final seq = ++_fetchSeq;
      final next = await engine.step();
      if (next == null || seq != _fetchSeq) return;
      snap = next;
      notifyListeners();
    } catch (_) {/* 本地引擎异常忽略 */}
  }

  Future<void> playCard(int i) async {
    final r = await engine.playCard(i);
    lastMsg = r['ok'] == true ? '✓ 已出牌' : '✗ ${r['error']}';
    await refresh();
  }

  Future<void> endTurn() async {
    await engine.endTurn();
    await refresh();
  }

  Future<void> setIntent(String id) async {
    await engine.setIntent(id);
    await refresh();
  }

  @override
  void dispose() {
    _poll?.cancel();
    _stepTimer?.cancel();
    super.dispose();
  }
}
