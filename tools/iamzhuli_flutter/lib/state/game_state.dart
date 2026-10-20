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
  // 结算:关卡结束(isLevelOver)或时间走完(isFinished,最后一日点"下一日"后
  // 服务端不会自动结算)→客户端拉 endLevel(幂等)+score
  Map<String, dynamic>? result;
  List<dynamic> rankings = const [];
  bool _settling = false;
  /// 当前关卡(重开/选关用)
  String levelId = 'accumulate';

  Map<String, dynamic>? get cs => snap['cardState'] as Map<String, dynamic>?;
  Map<String, dynamic>? get acct => snap['account'] as Map<String, dynamic>?;
  bool get inPlay => (cs?['phase'] as String?) == 'Play';
  bool get autoRun => _autoRun;

  Future<void> startNewGame(String levelId) async {
    this.levelId = levelId;
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
    if ((s['isLevelOver'] == true || s['isFinished'] == true) &&
        result == null && !_settling) {
      unawaited(_settle());
    }
    notifyListeners();
  }

  Future<void> _settle() async {
    _settling = true;
    try {
      result = await engine.endLevel();
      final sc = await engine.score();
      rankings = (sc['rankings'] as List?) ?? const [];
    } catch (_) {
      _settling = false;   // 轮询下一拍重试
      return;
    }
    notifyListeners();
  }

  /// 结算后再来一局(同关卡重开)。
  Future<void> restart() => startNewGame(levelId);

  /// 切换关卡(选关入口)。
  Future<void> switchLevel(String id) {
    result = null;
    rankings = const [];
    _settling = false;
    return startNewGame(id);
  }

  /// 存档(服务端/本地因引擎而异;仅日切边界,CanSave 由引擎判定)。
  Future<void> saveGame() async {
    bool ok;
    try {
      ok = await engine.saveGame('slot1');
    } catch (_) {
      ok = false;
    }
    lastMsg = ok ? '✓ 已保存(slot1)' : '✗ 仅日切边界可存档(盘前/收盘后/段0)';
    notifyListeners();
  }

  /// 读档(slot1):恢复存档日状态,结算态清零重估。
  Future<void> loadGame() async {
    bool ok;
    try {
      ok = await engine.loadGame('slot1');
    } catch (_) {
      ok = false;
    }
    if (!ok) {
      lastMsg = '✗ 无存档或读档失败(slot1)';
      notifyListeners();
      return;
    }
    result = null;
    rankings = const [];
    _settling = false;
    await refresh();
    lastMsg = '✓ 已读档 → 第${snap['currentDay']}日';
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

  /// 日切:开始下一交易日(收盘横幅触发;T+1解锁/消息日切/卡牌回合归0)。
  Future<void> nextDay() async {
    await engine.nextDay();
    await refresh();
  }

  Future<void> setIntent(String id) async {
    await engine.setIntent(id);
    await refresh();
  }

  // ── 信息战(消息/水军/信号):结果写入 lastMsg,快照含 activeNews/waterArmy*/infoHeat ──

  Future<void> _act(String doing, Future<Map<String, dynamic>> Function() op) async {
    try {
      final r = await op();
      lastMsg = r['ok'] == true ? '✓ $doing' : '✗ ${r['error']}';
    } catch (_) {
      lastMsg = '✗ $doing失败(连接)';
    }
    notifyListeners();
    await refresh();
  }

  Future<void> publishNews(String type) => _act('已发布消息', () => engine.publishNews(type));
  Future<void> upgradeWaterArmy() => _act('水军网络升级', () => engine.upgradeWaterArmy());
  Future<void> toggleWaterArmy() => _act('水军运营切换', () => engine.toggleWaterArmy());
  Future<void> publishSignal(String type) => _act('已发布信号', () => engine.publishSignal(type));

  @override
  void dispose() {
    _poll?.cancel();
    _stepTimer?.cancel();
    super.dispose();
  }
}
