// 全局状态(借PlanForge模式:单ChangeNotifier拉取式刷新)
library;
import 'dart:async';
import 'package:flutter/foundation.dart';
import '../engine/game_client.dart';

class GameState extends ChangeNotifier {
  final GameClient engine;
  GameState(this.engine);

  Map<String, dynamic> snap = {};
  Timer? _timer;
  bool _autoRun = false;
  String lastMsg = '';

  Map<String, dynamic>? get cs => snap['cardState'] as Map<String, dynamic>?;
  Map<String, dynamic>? get acct => snap['account'] as Map<String, dynamic>?;
  bool get inPlay => (cs?['phase'] as String?) == 'Play';
  bool get autoRun => _autoRun;

  Future<void> startNewGame(String levelId) async {
    await engine.loadLevel(levelId);
    await engine.begin();
    await refresh();
  }

  Future<void> refresh() async {
    snap = await engine.snapshot();
    notifyListeners();
  }

  void toggleAuto() {
    _autoRun = !_autoRun;
    if (_autoRun) {
      _timer = Timer.periodic(const Duration(milliseconds: 600), (_) => tick());
    } else {
      _timer?.cancel();
    }
    notifyListeners();
  }

  Future<void> tick() async {
    try {
      final next = await engine.step();
      if (next != null) snap = next;
      notifyListeners();
    } catch (_) {/* 网络闪断忽略 */}
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
}
