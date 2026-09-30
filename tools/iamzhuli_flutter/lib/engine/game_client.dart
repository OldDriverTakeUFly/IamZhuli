// 引擎抽象: FFI(桌面单机) 或 HTTP(手机回退,连局域网Web服务) 同一接口。
import 'dart:async';
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
  Future<Map<String, dynamic>> setIntent(String id) => _e.setIntent(id);
}
