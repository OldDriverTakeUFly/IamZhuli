import 'dart:io';
import 'package:flutter/material.dart';
import 'engine/game_client.dart';
import 'state/game_state.dart';
import 'screens/trade_screen.dart';
import 'screens/data_screen.dart';
import 'screens/account_screen.dart';

const kSoPath = String.fromEnvironment('IAMZHULI_SO',
    defaultValue: '/home/leo/Projects/IamZhuli/src/IamZhuli.Native/bin/Release/net8.0/linux-x64/publish/IamZhuli.Native.so');

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const IamZhuliApp());
}

class IamZhuliApp extends StatelessWidget {
  const IamZhuliApp({super.key});
  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: '我是主力',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        brightness: Brightness.dark,
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFFE8C44A), brightness: Brightness.dark),
        scaffoldBackgroundColor: const Color(0xFF14171E),
      ),
      home: const GameShell(),
    );
  }
}

class GameShell extends StatefulWidget {
  const GameShell({super.key});
  @override
  State<GameShell> createState() => _GameShellState();
}

class _GameShellState extends State<GameShell> {
  GameState? _state;
  String? _error;
  int _tab = 0;

  @override
  void initState() {
    super.initState();
    _boot();
  }

  Future<void> _boot() async {
    try {
      final client = await createClient(soPath: kSoPath);
      final gs = GameState(client);
      await gs.startNewGame('accumulate');
      if (mounted) setState(() { _state = gs; });
    } catch (e) {
      if (mounted) setState(() { _error = '$e'; });
    }
  }

  @override
  void dispose() {
    _state?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = _state;
    if (state == null) {
      return Scaffold(
        body: Center(child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const CircularProgressIndicator(),
            if (_error != null) ...[
              const SizedBox(height: 16),
              Text('连接失败: $_error\n\n请确认与电脑同WiFi且服务器已启动',
                  style: const TextStyle(color: Colors.orange)),
            ],
          ]),
        )),
      );
    }
    return AnimatedBuilder(
      animation: state,
      builder: (context, _) {
        final lp = state.snap['lastPrice'];
        return Stack(children: [
          Scaffold(
            appBar: AppBar(
              backgroundColor: const Color(0xFF1A1F2A),
              title: Text('我是主力 · ${lp ?? '--'}',
                  style: const TextStyle(fontSize: 18, fontWeight: FontWeight.bold)),
              actions: [
                IconButton(
                  icon: Icon(state.autoRun ? Icons.pause : Icons.play_arrow),
                  onPressed: state.toggleAuto,
                ),
              ],
            ),
            body: IndexedStack(
              index: _tab,
              children: [TradeScreen(state: state), DataScreen(state: state), AccountScreen(state: state)],
            ),
            bottomNavigationBar: NavigationBar(
              selectedIndex: _tab,
              onDestinationSelected: (i) => setState(() => _tab = i),
              destinations: const [
                NavigationDestination(icon: Icon(Icons.candlestick_chart), label: '交易'),
                NavigationDestination(icon: Icon(Icons.bar_chart), label: '数据'),
                NavigationDestination(icon: Icon(Icons.account_balance_wallet), label: '账户'),
              ],
            ),
          ),
          if (state.result != null) SettlementOverlay(state: state),
        ]);
      },
    );
  }
}

/// 关卡结算覆盖层:胜负/目标/教练点评/三方积分排名 + 再来一局。
/// 由 GameState 轮询检测 isLevelOver/isFinished 自动触发,无需用户操作。
class SettlementOverlay extends StatelessWidget {
  final GameState state;
  const SettlementOverlay({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final r = state.result!;
    final victory = r['isVictory'] == true;
    final stars = (r['stars'] as num?)?.toInt() ?? 0;
    final objectives = (r['objectives'] as List?) ?? const [];
    return Positioned.fill(
      child: Material(
        color: Colors.black87,
        child: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: Container(
                constraints: const BoxConstraints(maxWidth: 480),
                padding: const EdgeInsets.all(22),
                decoration: BoxDecoration(
                  color: const Color(0xFF1B2130),
                  borderRadius: BorderRadius.circular(16),
                  border: Border.all(color: victory ? const Color(0xFFE8C44A) : const Color(0xFF5A6478), width: 1.5),
                ),
                child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  Text(
                    victory ? '操盘胜利 ${'★' * stars}${'☆' * (3 - stars)}' : '操盘失败',
                    textAlign: TextAlign.center,
                    style: TextStyle(fontSize: 24, fontWeight: FontWeight.w900,
                        color: victory ? const Color(0xFFE8C44A) : const Color(0xFFEF5350)),
                  ),
                  if (!victory && (r['failureReason'] as String?)?.isNotEmpty == true)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text('${r['failureReason']}',
                          textAlign: TextAlign.center,
                          style: const TextStyle(fontSize: 13, color: Color(0xFFC97B6C))),
                    ),
                  const SizedBox(height: 14),
                  for (final o in objectives.cast<Map<String, dynamic>>())
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(children: [
                        Text(o['achieved'] == true ? '✔' : '✗',
                            style: TextStyle(color: o['achieved'] == true ? const Color(0xFF26A69A) : const Color(0xFFEF5350))),
                        const SizedBox(width: 8),
                        Expanded(child: Text('${o['description']}(${((o['progress'] as num? ?? 0) * 100).toStringAsFixed(0)}%)',
                            style: const TextStyle(fontSize: 13))),
                      ]),
                    ),
                  const Divider(height: 24, color: Color(0xFF39414F)),
                  const Text('教练点评', style: TextStyle(fontSize: 12, color: Color(0xFF97A3B5))),
                  const SizedBox(height: 4),
                  Text('${r['coachComment']}',
                      style: const TextStyle(fontSize: 13, height: 1.5, color: Color(0xFFD7DCE4))),
                  const Divider(height: 24, color: Color(0xFF39414F)),
                  const Text('三方积分', style: TextStyle(fontSize: 12, color: Color(0xFF97A3B5))),
                  const SizedBox(height: 6),
                  for (final p in state.rankings.cast<Map<String, dynamic>>())
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(children: [
                        SizedBox(
                          width: 28,
                          child: Text('#${p['rank']}',
                              style: TextStyle(fontWeight: FontWeight.w800,
                                  color: (p['rank'] as num?)?.toInt() == 1 ? const Color(0xFFE8C44A) : const Color(0xFF97A3B5))),
                        ),
                        Expanded(child: Text('${p['name']}', style: const TextStyle(fontSize: 14))),
                        Text('${(((p['returnRate'] as num? ?? 0) * 100).toStringAsFixed(1))}%  ',
                            style: TextStyle(fontSize: 12,
                                color: (p['returnRate'] as num? ?? 0) >= 0 ? const Color(0xFFEF5350) : const Color(0xFF26A69A))),
                        Text('${(p['score'] as num?)?.toStringAsFixed(0)}分',
                            style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: Color(0xFFE8C44A))),
                      ]),
                    ),
                  const SizedBox(height: 18),
                  FilledButton(
                    style: FilledButton.styleFrom(
                      backgroundColor: const Color(0xFFE8C44A),
                      foregroundColor: const Color(0xFF14171E),
                      padding: const EdgeInsets.symmetric(vertical: 12),
                    ),
                    onPressed: state.restart,
                    child: const Text('再来一局 ▶', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700)),
                  ),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
