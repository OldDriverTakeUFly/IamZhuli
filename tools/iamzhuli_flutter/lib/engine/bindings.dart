// FFI 绑定: IamZhuli.Native.so 的 C ABI(UTF8 JSON 进出,与 ctypes冒烟同模型)
import 'dart:convert';
import 'dart:ffi';
import 'package:ffi/ffi.dart';
import 'dart:io';

typedef _InitC = Int32 Function(Int32);
typedef _InitD = int Function(int);
typedef _IntStrC = Int32 Function(Pointer<Uint8>);
typedef _IntStrD = int Function(Pointer<Uint8>);
typedef _PtrC = Pointer<Uint8> Function();
typedef _PtrD = Pointer<Uint8> Function();
typedef _PtrIntC = Pointer<Uint8> Function(Int32);
typedef _PtrIntD = Pointer<Uint8> Function(int);
typedef _PtrStrC = Pointer<Uint8> Function(Pointer<Uint8>);
typedef _PtrStrD = Pointer<Uint8> Function(Pointer<Uint8>);
typedef _FreeC = Void Function(Pointer<Uint8>);
typedef _FreeD = void Function(Pointer<Uint8>);

/// 游戏引擎桥(单实例;返回的JSON字符串由Dart侧复制,Native内存即刻释放)。
class GameEngine {
  final DynamicLibrary _lib;
  late final _InitD _init;
  late final _IntStrD _loadLevel;
  late final _InitD _begin;
  late final _PtrD _step;
  late final _PtrD _snapshot;
  late final _PtrIntD _playCard;
  late final _PtrD _endTurn;
  late final _InitD _nextDay;
  late final _PtrD _endLevel;
  late final _PtrD _score;
  late final _PtrStrD _publishNews;
  late final _PtrD _waterArmyUpgrade;
  late final _PtrD _waterArmyToggle;
  late final _PtrStrD _publishSignal;
  late final _PtrStrD _setIntent;
  late final _PtrD _exportSave;
  late final _PtrStrD _importSave;
  late final _FreeD _free;

  GameEngine(String soPath)
      : _lib = DynamicLibrary.open(soPath) {
    _init = _lib.lookupFunction<_InitC, _InitD>('ig_init');
    _loadLevel = _lib.lookupFunction<_IntStrC, _IntStrD>('ig_load_level');
    _begin = _lib.lookupFunction<_InitC, _InitD>('ig_begin');
    _step = _lib.lookupFunction<_PtrC, _PtrD>('ig_step');
    _snapshot = _lib.lookupFunction<_PtrC, _PtrD>('ig_snapshot');
    _playCard = _lib.lookupFunction<_PtrIntC, _PtrIntD>('ig_play_card');
    _endTurn = _lib.lookupFunction<_PtrC, _PtrD>('ig_end_turn');
    _nextDay = _lib.lookupFunction<_InitC, _InitD>('ig_next_day');
    _endLevel = _lib.lookupFunction<_PtrC, _PtrD>('ig_end_level');
    _score = _lib.lookupFunction<_PtrC, _PtrD>('ig_score');
    _publishNews = _lib.lookupFunction<_PtrStrC, _PtrStrD>('ig_publish_news');
    _waterArmyUpgrade = _lib.lookupFunction<_PtrC, _PtrD>('ig_waterarmy_upgrade');
    _waterArmyToggle = _lib.lookupFunction<_PtrC, _PtrD>('ig_waterarmy_toggle');
    _publishSignal = _lib.lookupFunction<_PtrStrC, _PtrStrD>('ig_publish_signal');
    _setIntent = _lib.lookupFunction<_PtrStrC, _PtrStrD>('ig_set_intent');
    _exportSave = _lib.lookupFunction<_PtrC, _PtrD>('ig_export_save');
    _importSave = _lib.lookupFunction<_PtrStrC, _PtrStrD>('ig_import_save');
    _free = _lib.lookupFunction<_FreeC, _FreeD>('ig_free');
  }

  String _take(Pointer<Uint8> p) {
    if (p == nullptr) return '';
    final s = utf8.decode(p.asTypedList(_strlen(p)).toList());
    _free(p);
    return s;
  }

  static int _strlen(Pointer<Uint8> p) {
    var i = 0;
    while (p[i] != 0) i++;
    return i;
  }

  Pointer<Uint8> _str(String s) {
    final bytes = utf8.encode(s);
    final p = malloc<Uint8>(bytes.length + 1);
    for (var i = 0; i < bytes.length; i++) p[i] = bytes[i];
    p[bytes.length] = 0;
    return p;
  }

  void init(int seed) => _init(seed);
  int loadLevel(String id) => _loadLevel(_str(id));
  void begin() => _begin(0);
  Map<String, dynamic>? step() {
    final s = _take(_step());
    return s.isEmpty ? null : jsonDecode(s) as Map<String, dynamic>;
  }

  Map<String, dynamic> snapshot() => jsonDecode(_take(_snapshot())) as Map<String, dynamic>;
  Map<String, dynamic> playCard(int i) => jsonDecode(_take(_playCard(i))) as Map<String, dynamic>;
  Map<String, dynamic> endTurn() => jsonDecode(_take(_endTurn())) as Map<String, dynamic>;
  void nextDay() => _nextDay(0);
  Map<String, dynamic> endLevel() => jsonDecode(_take(_endLevel())) as Map<String, dynamic>;
  Map<String, dynamic> score() => jsonDecode(_take(_score())) as Map<String, dynamic>;
  Map<String, dynamic> publishNews(String type) => jsonDecode(_take(_publishNews(_str(type)))) as Map<String, dynamic>;
  Map<String, dynamic> upgradeWaterArmy() => jsonDecode(_take(_waterArmyUpgrade())) as Map<String, dynamic>;
  Map<String, dynamic> toggleWaterArmy() => jsonDecode(_take(_waterArmyToggle())) as Map<String, dynamic>;
  Map<String, dynamic> publishSignal(String type) => jsonDecode(_take(_publishSignal(_str(type)))) as Map<String, dynamic>;
  Map<String, dynamic> setIntent(String id) => jsonDecode(_take(_setIntent(_str(id)))) as Map<String, dynamic>;
  Map<String, dynamic> exportSave() => jsonDecode(_take(_exportSave())) as Map<String, dynamic>;
  Map<String, dynamic> importSave(String json) => jsonDecode(_take(_importSave(_str(json)))) as Map<String, dynamic>;
}
