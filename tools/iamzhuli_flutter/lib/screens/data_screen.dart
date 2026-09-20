import 'package:flutter/material.dart';
import '../state/game_state.dart';

class DataScreen extends StatelessWidget {
  final GameState state;
  const DataScreen({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final asks = ((state.snap['asks'] as List?) ?? []).cast<Map<String, dynamic>>().reversed.toList();
    final bids = ((state.snap['bids'] as List?) ?? []).cast<Map<String, dynamic>>();
    return ListView(children: [
      for (final a in asks) _row('卖', a, const Color(0xFF26A69A)),
      Container(
        alignment: Alignment.center, padding: const EdgeInsets.all(10),
        child: Text('${state.snap['lastPrice'] ?? '--'}',
            style: const TextStyle(fontSize: 26, fontWeight: FontWeight.w900)),
      ),
      for (final b in bids) _row('买', b, const Color(0xFFEF5350)),
    ]);
  }

  Widget _row(String tag, Map<String, dynamic> lv, Color color) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 5),
        child: Row(children: [
          Text(tag, style: TextStyle(color: color)),
          const SizedBox(width: 12),
          Text('${lv['price']}', style: TextStyle(color: color)),
          const Spacer(),
          Text('${lv['qty']}'),
        ]),
      );
}
