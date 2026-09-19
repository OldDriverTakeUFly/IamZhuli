// 全局状态(借PlanForge模式:单ChangeNotifier拉取式刷新,数据量小够用)
library;
import 'dart:async';
import 'package:flutter/foundation.dart';
import '../engine/bindings.dart';

class GameState extends ChangeNotifier {
  final GameEngine engine;
  GameState(this.engine);

  Map<String, dynamic> snap = {};
  Timer? _timer;
  bool _autoRun = false;

  Map<String, dynamic>? get cs => snap['cardState'] as Map<String, dynamic>?;
  Map<String, dynamic>? get acct => snap['account'] as Map<String, dynamic>?;
  bool get inPlay => (cs?['phase'] as String?) == 'Play';

  void startNewGame(String levelId, {int seed = 4242}) {
    engine.init(seed);
    engine.loadLevel(levelId);
    engine.begin();
    refresh();
  }

  void refresh() {
    snap = engine.snapshot();
    notifyListeners();
  }

  /// 自动tick(非冻结态推进;Play冻结时仅刷新供UI渲染)。
  void toggleAuto() {
    _autoRun = !_autoRun;
    if (_autoRun) {
      _timer = Timer.periodic(const Duration(milliseconds: 400), (_) => tick());
    } else {
      _timer?.cancel();
    }
    notifyListeners();
  }

  bool get autoRun => _autoRun;

  void tick() {
    if (snap.isEmpty) return;
    final next = engine.step();
    snap = next ?? engine.snapshot();
    notifyListeners();
  }

  void playCard(int i) {
    final r = engine.playCard(i);
    lastMsg = r['ok'] == true ? '✓ 已出牌' : '✗ ${r['error']}';
    refresh();
  }

  void endTurn() {
    final ra = engine.endTurn();
    final vol = ra['volume'] ?? 0;
    lastMsg = vol > 0 ? '🏛 揭牌: ${ra["price"]} × $vol手' : '🏛 无交叉';
    refresh();
  }

  void setIntent(String id) {
    engine.setIntent(id);
    refresh();
  }

  String lastMsg = '';
}
