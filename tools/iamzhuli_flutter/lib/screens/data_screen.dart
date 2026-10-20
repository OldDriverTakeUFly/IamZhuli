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
      // ── AI主力动向(内心独白,新→旧;对手存在感) ──
      const Padding(
        padding: EdgeInsets.fromLTRB(16, 14, 16, 6),
        child: Text('AI主力动向', style: TextStyle(fontSize: 13, fontWeight: FontWeight.w800, color: Color(0xFF97A3B5))),
      ),
      if (state.ai.isEmpty)
        const Padding(
          padding: EdgeInsets.symmetric(horizontal: 16),
          child: Text('尚无动向记录——AI在状态切换或关键tick时会留下思路', style: TextStyle(fontSize: 11, color: Color(0xFF6B7688))),
        )
      else
        for (final t in state.ai.cast<Map<String, dynamic>>())
          Container(
            margin: const EdgeInsets.fromLTRB(12, 0, 12, 6),
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(color: const Color(0xFF1B2130), borderRadius: BorderRadius.circular(8)),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                Text(_stateCn(t['state'] as String? ?? ''),
                    style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w800, color: Color(0xFFE8C44A))),
                const SizedBox(width: 8),
                if ((t['detectedIntent'] as String? ?? 'None') != 'None')
                  Expanded(child: Text('识别你:${_intentCn(t['detectedIntent'] as String)} ${((t['confidence'] as num? ?? 0) * 100).toStringAsFixed(0)}%',
                      style: const TextStyle(fontSize: 10.5, color: Color(0xFFEF9A9A)))),
                const Spacer(),
                Text('D${t['day']}·${t['tickOfDay']}t', style: const TextStyle(fontSize: 10, color: Color(0xFF6B7688))),
              ]),
              const SizedBox(height: 3),
              Text('${t['reason']}', style: const TextStyle(fontSize: 11.5, height: 1.4, color: Color(0xFFC9D1DE))),
            ]),
          ),
      const SizedBox(height: 12),
    ]);
  }

  static const _states = {
    'Observe': '观察', 'Accumulate': '吸筹', 'PushingUp': '拉升',
    'Distributing': '出货', 'Washing': '洗盘', 'Defending': '防御', 'Distribute': '出货',
  };
  static String _stateCn(String s) => _states[s] ?? s;
  static const _intents = {
    'PushingUp': '拉升', 'Accumulating': '吸筹', 'Distributing': '出货',
    'Washing': '洗盘', 'Defending': '防御', 'None': '无',
  };
  static String _intentCn(String s) => _intents[s] ?? s;

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
