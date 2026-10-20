// 引擎抽象: FFI(桌面单机) 或 HTTP(手机回退,连局域网Web服务) 同一接口。
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'bindings.dart';
import 'http_engine.dart';

abstract class GameClient {
  Future<void> loadLevel(String id);
  Future<void> begin();
  Future<Map<String, dynamic>?> step();
  Future<Map<String, dynamic>> snapshot();
  Future<Map<String, dynamic>> playCard(int i);
  Future<Map<String, dynamic>> endTurn();
  Future<void> nextDay();
  Future<Map<String, dynamic>> endLevel();
  Future<Map<String, dynamic>> score();
  Future<bool> saveGame(String slot);
  Future<bool> loadGame(String slot);
  Future<Map<String, dynamic>> publishNews(String type);
  Future<Map<String, dynamic>> upgradeWaterArmy();
  Future<Map<String, dynamic>> toggleWaterArmy();
  Future<Map<String, dynamic>> publishSignal(String type);
  Future<Map<String, dynamic>> setIntent(String id);

  /// 服务端驱动时钟(HTTP模式):true=游戏在服务器自走,客户端只轮询;
  /// ▶/⏸ 应调 [setClockRunning] 暂停/恢复服务端时钟。
  bool get serverDriven;

  /// 暂停/恢复游戏时钟。HTTP=服务端pause/resume;本地FFI=无操作(由客户端step定时器控制)。
  Future<void> setClockRunning(bool running);
}

/// 桌面FFI单机引擎。
class FfiClient implements GameClient {
  final GameEngine _e;
  FfiClient(this._e);
  @override
  bool get serverDriven => false;
  @override
  Future<void> setClockRunning(bool running) async {}   // 本地时钟由GameState的step定时器驱动
  @override
  Future<void> loadLevel(String id) async => _e.loadLevel(id);
  @override
  Future<void> begin() async => _e.begin();
  @override
  Future<Map<String, dynamic>?> step() async => _e.step();
  @override
  Future<Map<String, dynamic>> snapshot() async => _e.snapshot();
  @override
  Future<Map<String, dynamic>> playCard(int i) async => _e.playCard(i);
  @override
  Future<Map<String, dynamic>> endTurn() async => _e.endTurn();
  @override
  Future<void> nextDay() async => _e.nextDay();
  @override
  Future<Map<String, dynamic>> endLevel() async => _e.endLevel();
  @override
  Future<Map<String, dynamic>> score() async => _e.score();
  @override
  Future<bool> saveGame(String slot) async {
    // 本地单机:导出存档JSON落用户目录(~/.iamzhuli/saves),与桥的格式一致
    final home = Platform.environment['HOME'] ?? '.';
    final d = Directory('$home/.iamzhuli/saves');
    await d.create(recursive: true);
    await File('${d.path}/$slot.json').writeAsString(jsonEncode(_e.exportSave()));
    return true;
  }
  @override
  Future<bool> loadGame(String slot) async {
    final home = Platform.environment['HOME'] ?? '.';
    final f = File('$home/.iamzhuli/saves/$slot.json');
    if (!f.existsSync()) return false;
    final r = _e.importSave(await f.readAsString());
    return r['ok'] == true;
  }
  @override
  Future<Map<String, dynamic>> publishNews(String type) async => _e.publishNews(type);
  @override
  Future<Map<String, dynamic>> upgradeWaterArmy() async => _e.upgradeWaterArmy();
  @override
  Future<Map<String, dynamic>> toggleWaterArmy() async => _e.toggleWaterArmy();
  @override
  Future<Map<String, dynamic>> publishSignal(String type) async => _e.publishSignal(type);
  @override
  Future<Map<String, dynamic>> setIntent(String id) async => _e.setIntent(id);
}

/// 工厂: 优先FFI(.so随桌面bundle),失败(手机/无库)回退HTTP局域网服务器。
Future<GameClient> createClient({String? soPath, String? httpBase}) async {
  if (soPath != null && File(soPath).existsSync()) {
    return FfiClient(GameEngine(soPath));
  }
  return _HttpAdapter(HttpEngine(httpBase ?? 'http://192.168.2.179:5023'));
}

class _HttpAdapter implements GameClient {
  final HttpEngine _e;
  _HttpAdapter(this._e);
  @override
  bool get serverDriven => true;
  @override
  Future<void> setClockRunning(bool running) => running ? _e.resume() : _e.pause();
  @override
  Future<void> loadLevel(String id) => _e.loadLevel(id);
  @override
  Future<void> begin() => _e.begin();
  @override
  Future<Map<String, dynamic>?> step() async => await _e.step();
  @override
  Future<Map<String, dynamic>> snapshot() => _e.snapshot();
  @override
  Future<Map<String, dynamic>> playCard(int i) => _e.playCard(i);
  @override
  Future<Map<String, dynamic>> endTurn() => _e.endTurn();
  @override
  Future<void> nextDay() => _e.nextDay();
  @override
  Future<Map<String, dynamic>> endLevel() => _e.endLevel();
  @override
  Future<Map<String, dynamic>> score() => _e.score();
  @override
  Future<bool> saveGame(String slot) async => (await _e.saveGame(slot))['ok'] == true;
  @override
  Future<bool> loadGame(String slot) async => (await _e.loadGame(slot))['ok'] == true;
  @override
  Future<Map<String, dynamic>> publishNews(String type) => _e.publishNews(type);
  @override
  Future<Map<String, dynamic>> upgradeWaterArmy() => _e.upgradeWaterArmy();
  @override
  Future<Map<String, dynamic>> toggleWaterArmy() => _e.toggleWaterArmy();
  @override
  Future<Map<String, dynamic>> publishSignal(String type) => _e.publishSignal(type);
  @override
  Future<Map<String, dynamic>> setIntent(String id) => _e.setIntent(id);
}
