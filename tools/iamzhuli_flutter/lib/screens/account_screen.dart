import 'package:flutter/material.dart';
import '../state/game_state.dart';

class AccountScreen extends StatelessWidget {
  final GameState state;
  const AccountScreen({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final a = state.acct;
    if (a == null) return const Center(child: Text('未开局'));
    return ListView(children: [
      Container(
        alignment: Alignment.center, padding: const EdgeInsets.all(20),
        child: Column(children: [
          const Text('权益', style: TextStyle(color: Colors.grey)),
          Text('${(a['totalEquity'] / 10000).toStringAsFixed(0)}万',
              style: const TextStyle(fontSize: 32, fontWeight: FontWeight.w900, color: Color(0xFFE8C44A))),
        ]),
      ),
      _row('现金', '${(a['cash'] / 10000).toStringAsFixed(0)}万'),
      _row('可用', '${(a['availableCash'] / 10000).toStringAsFixed(0)}万'),
      _row('持仓', '${a['positionTotal']}手'),
      _row('T+1锁', '${a['positionT1Locked']}手'),
      _row('空头', a['shortQty'] > 0 ? '${a['shortQty']}手' : '--'),
      _row('监管', '${state.snap['regulatorHeat']} ${state.snap['penaltyLevel']}'),
      _row('情绪', '${state.snap['sentiment']}'),
    ]);
  }

  Widget _row(String k, String v) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
        child: Row(children: [Text(k, style: const TextStyle(color: Colors.grey)), const Spacer(), Text(v)]),
      );
}
