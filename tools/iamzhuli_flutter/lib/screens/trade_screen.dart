import 'package:flutter/material.dart';
import '../state/game_state.dart';

class TradeScreen extends StatelessWidget {
  final GameState state;
  const TradeScreen({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final cs = state.cs;
    if (cs == null) return const Center(child: Text('未开局'));
    final hand = (cs['hand'] as List).cast<Map<String, dynamic>>();
    final inPlay = state.inPlay;
    final played = cs['cardsPlayed'] as int? ?? 0;
    return Column(children: [
      SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(children: [
          for (final it in const [
            ('accumulate', '吸筹'), ('push', '拉升'), ('wash', '洗盘'),
            ('distribute', '出货'), ('defend', '防御'), ('none', '—'),
          ])
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 4),
              child: ChoiceChip(
                label: Text(it.$2),
                selected: (cs['intent'] ?? 'none').toString().toLowerCase() == it.$1,
                onSelected: (_) => state.setIntent(it.$1),
              ),
            ),
        ]),
      ),
      Padding(
        padding: const EdgeInsets.all(12),
        child: Row(children: [
          Text('段${(cs['turn'] as int? ?? 0) + 1} ${cs['phaseName']}'),
          const Spacer(),
          Text('能量 ${cs['energy']}/${cs['maxEnergy']}', style: const TextStyle(color: Color(0xFFFFE082))),
          const SizedBox(width: 12),
          Text('已出 $played/${cs['maxCardsPerTurn']}'),
        ]),
      ),
      Expanded(
        child: ListView.builder(
          scrollDirection: Axis.horizontal,
          itemCount: hand.length,
          itemBuilder: (context, i) {
            final c = hand[i];
            return GestureDetector(
              onTap: inPlay ? () => state.playCard(i) : null,
              child: Container(
                width: 118, margin: const EdgeInsets.all(8),
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  gradient: const LinearGradient(colors: [Color(0xFF2A3140), Color(0xFF222835)]),
                  borderRadius: BorderRadius.circular(12),
                  boxShadow: [BoxShadow(color: Colors.black.withOpacity(.4), blurRadius: 8)],
                ),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(c['name'], style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
                  const Spacer(),
                  Text('⚡${c['energyCost']}', style: const TextStyle(color: Color(0xFFFFE082), fontSize: 12)),
                  const SizedBox(height: 4),
                  Text(c['description'], maxLines: 3, overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontSize: 10, color: Color(0xFF97A3B5))),
                ]),
              ),
            );
          },
        ),
      ),
      Padding(
        padding: const EdgeInsets.all(12),
        child: SizedBox(
          width: double.infinity,
          child: FilledButton(
            onPressed: inPlay ? state.endTurn : null,
            child: Text(cs['phaseName'] == '盘前申报' ? '完成申报 ▶' : '结束回合 ▶'),
          ),
        ),
      ),
      Text(state.lastMsg, style: const TextStyle(color: Color(0xFF81D4FA))),
      const SizedBox(height: 8),
    ]);
  }
}
