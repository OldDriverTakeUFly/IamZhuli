import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../state/game_state.dart';

/// 交易页: 意图chips + 能量/阶段 + 纸牌手牌 + 出牌/结束回合(对齐Web三页式)。
class TradeScreen extends StatelessWidget {
  const TradeScreen({super.key});
  @override
  Widget build(BuildContext context) {
    return Consumer<GameState>(
      builder: (context, s, _) {
        final cs = s.cs;
        if (cs == null) return const Center(child: Text('未开局'));
        final hand = (cs['hand'] as List).cast<Map<String, dynamic>>();
        final inPlay = s.inPlay;
        final played = cs['cardsPlayed'] as int? ?? 0;
        return Column(children: [
          // 意图chips
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(children: [
              for (final it in const [('accumulate', '吸筹'), ('push', '拉升'), ('wash', '洗盘'), ('distribute', '出货'), ('defend', '防御'), ('none', '—')])
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  child: ChoiceChip(
                    label: Text(it.$2),
                    selected: (cs['intent'] ?? 'none').toString().toLowerCase() == it.$1,
                    onSelected: (_) => s.setIntent(it.$1),
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
          // 纸牌手牌(横滑)
          Expanded(
            child: ListView.builder(
              scrollDirection: Axis.horizontal,
              itemCount: hand.length,
              itemBuilder: (context, i) {
                final c = hand[i];
                final canPlay = inPlay && played < (cs['maxCardsPerTurn'] as int? ?? 2);
                return GestureDetector(
                  onTap: canPlay ? () => s.playCard(i) : null,
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
            child: Row(children: [
              Expanded(
                child: FilledButton(
                  onPressed: inPlay ? s.endTurn : null,
                  child: Text(cs['phaseName'] == '盘前申报' ? '完成申报 ▶' : '结束回合 ▶'),
                ),
              ),
            ]),
          ),
          Text(s.lastMsg, style: const TextStyle(color: Color(0xFF81D4FA))),
          const SizedBox(height: 8),
        ]);
      },
    );
  }
}
