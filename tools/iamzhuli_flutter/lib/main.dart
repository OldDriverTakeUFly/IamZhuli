import 'package:flutter/material.dart';
import 'engine/bindings.dart';
import 'state/game_state.dart';
import 'screens/trade_screen.dart';
import 'screens/data_screen.dart';
import 'screens/account_screen.dart';

const kSoPath = String.fromEnvironment('IAMZHULI_SO',
    defaultValue: '/home/leo/Projects/IamZhuli/src/IamZhuli.Native/bin/Release/net8.0/linux-x64/publish/IamZhuli.Native.so');

void main() {
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
        colorScheme: ColorScheme.fromSeed(seedColor: Color(0xFFE8C44A), brightness: Brightness.dark),
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
  late final GameState state;
  int _tab = 0;

  @override
  void initState() {
    super.initState();
    state = GameState(GameEngine(kSoPath));
    state.startNewGame('accumulate');
  }

  @override
  void dispose() {
    state.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: state,
      builder: (context, _) => Scaffold(
        appBar: AppBar(
          backgroundColor: const Color(0xFF1A1F2A),
          title: Text(
            '我是主力 · ${_price(state.snap)}',
            style: const TextStyle(fontSize: 18, fontWeight: FontWeight.bold),
          ),
          actions: [
            IconButton(
              icon: Icon(state.autoRun ? Icons.pause : Icons.play_arrow),
              onPressed: state.toggleAuto,
            ),
          ],
        ),
        body: IndexedStack(
          index: _tab,
          children: const [TradeScreen(), DataScreen(), AccountScreen()],
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
    );
  }

  String _price(Map<String, dynamic> snap) {
    final p = snap['lastPrice'];
    return p == null ? '--' : '$p';
  }
}
