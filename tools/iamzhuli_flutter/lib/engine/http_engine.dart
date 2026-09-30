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

  Future<void> loadLevel(String id) async { await _post('/api/loadlevel?id=$id'); }
  Future<void> begin() async { await _post('/api/begin'); }
  Future<void> pause() async { await _post('/api/pause'); }
  Future<void> resume() async { await _post('/api/resume'); }
  Future<Map<String, dynamic>> step() => _get('/api/snapshot');
  Future<Map<String, dynamic>> snapshot() => _get('/api/snapshot');
  Future<Map<String, dynamic>> playCard(int i) async => _post('/api/card/play?handIndex=$i');
  Future<Map<String, dynamic>> endTurn() async => _post('/api/card/endturn');
  Future<Map<String, dynamic>> setIntent(String id) async => _post('/api/intent?id=$id');
}
