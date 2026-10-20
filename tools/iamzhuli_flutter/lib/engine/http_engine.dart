// HTTP引擎: 复用Web REST API(手机等无本地逻辑库的端;服务器自走tick)。
import 'dart:convert';
import 'dart:io';

class HttpEngine {
  final String base;
  final HttpClient _http = HttpClient()..connectionTimeout = const Duration(seconds: 3);
  HttpEngine(this.base);

  Future<Map<String, dynamic>> _post(String path) async {
    final r = await _http.postUrl(Uri.parse('$base$path'));
    final resp = await r.close();
    final body = await resp.transform(utf8.decoder).join();
    return body.isEmpty ? {} : jsonDecode(body);   // 204/空体容错
  }

  Future<Map<String, dynamic>> _get(String path) async {
    final r = await _http.getUrl(Uri.parse('$base$path'));
    final resp = await r.close();
    return jsonDecode(await resp.transform(utf8.decoder).join());
  }

  /// 原始GET(数组根的响应,如 /api/ai)。
  Future<dynamic> _getRaw(String path) async {
    final r = await _http.getUrl(Uri.parse('$base$path'));
    final resp = await r.close();
    return jsonDecode(await resp.transform(utf8.decoder).join());
  }

  Future<void> loadLevel(String id) async { await _post('/api/loadlevel?id=$id'); }
  Future<void> begin() async { await _post('/api/begin'); }
  Future<void> pause() async { await _post('/api/pause'); }
  Future<void> resume() async { await _post('/api/resume'); }
  Future<Map<String, dynamic>> step() => _get('/api/snapshot');
  Future<Map<String, dynamic>> snapshot() => _get('/api/snapshot');
  Future<Map<String, dynamic>> playCard(int i) async => _post('/api/card/play?handIndex=$i');
  Future<Map<String, dynamic>> endTurn() async => _post('/api/card/endturn');
  Future<void> nextDay() async { await _post('/api/nextday'); }
  Future<Map<String, dynamic>> endLevel() async => _post('/api/endlevel');
  Future<Map<String, dynamic>> score() => _get('/api/score');
  Future<Map<String, dynamic>> saveGame(String slot) async => _post('/api/save/$slot');
  Future<Map<String, dynamic>> loadGame(String slot) async => _post('/api/load/$slot');
  Future<Map<String, dynamic>> publishNews(String type) async => _post('/api/news?type=$type');
  Future<Map<String, dynamic>> upgradeWaterArmy() async => _post('/api/waterarmy/upgrade');
  Future<Map<String, dynamic>> toggleWaterArmy() async => _post('/api/waterarmy/toggle');
  Future<Map<String, dynamic>> publishSignal(String type) async => _post('/api/signal?type=$type');
  Future<List<dynamic>> aiThoughts(int count) async => (await _getRaw('/api/ai?count=$count') as List).cast<dynamic>();
  Future<Map<String, dynamic>> setIntent(String id) async => _post('/api/intent?id=$id');
}
