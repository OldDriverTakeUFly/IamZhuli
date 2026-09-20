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
        return Scaffold(
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
        );
      },
    );
  }
}
