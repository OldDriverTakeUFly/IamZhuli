import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../state/game_state.dart';

/// 账户页: 权益/持仓/现金/监管。
class AccountScreen extends StatelessWidget {
  const AccountScreen({super.key});
  @override
  Widget build(BuildContext context) {
    return Consumer<GameState>(
      builder: (context, s, _) {
        final a = s.acct;
        if (a == null) return const Center(child: Text('未开局'));
        return ListView(children: [
          _big('权益', '${(a['totalEquity'] / 10000).toStringAsFixed(0)}万'),
          _row('现金', '${(a['cash'] / 10000).toStringAsFixed(0)}万'),
          _row('可用', '${(a['availableCash'] / 10000).toStringAsFixed(0)}万'),
          _row('持仓', '${a['positionTotal']}手'),
          _row('T+1锁', '${a['positionT1Locked']}手'),
          _row('空头', a['shortQty'] > 0 ? '${a['shortQty']}手' : '--'),
          _row('监管', '${s.snap['regulatorHeat']} ${s.snap['penaltyLevel']}'),
          _row('情绪', '${s.snap['sentiment']}'),
        ]);
      },
    );
  }

  Widget _big(String k, String v) => Container(
        alignment: Alignment.center, padding: const EdgeInsets.all(20),
        child: Column(children: [
          Text(k, style: const TextStyle(color: Colors.grey)),
          Text(v, style: const TextStyle(fontSize: 32, fontWeight: FontWeight.w900, color: Color(0xFFE8C44A))),
        ]),
      );

  Widget _row(String k, String v) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
        child: Row(children: [Text(k, style: const TextStyle(color: Colors.grey)), const Spacer(), Text(v)]),
      );
}
