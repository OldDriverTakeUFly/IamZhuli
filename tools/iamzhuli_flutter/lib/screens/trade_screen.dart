import 'package:flutter/material.dart';
import '../state/game_state.dart';
import '../ui/charts.dart';

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
    final asks = ((state.snap['asks'] as List?) ?? []).cast<Map<String, dynamic>>();
    final bids = ((state.snap['bids'] as List?) ?? []).cast<Map<String, dynamic>>();
    final a1 = asks.isNotEmpty ? asks.last : null;
    final b1 = bids.isNotEmpty ? bids.first : null;
    final candles = (state.snap['dailyCandles'] as List? ?? []);
    final ts = (state.snap['timeshare'] as List? ?? []);
    final prev = (state.snap['previousClose'] as num?)?.toDouble() ?? 10.0;
    final lp = (state.snap['lastPrice'] as num?)?.toDouble();
    return Column(children: [
      // ── 图表区(分时/K线切换,占屏38%) + 右上迷你盘口 ──
      Expanded(
        flex: 38,
        child: Stack(children: [
          Positioned.fill(
            child: Container(
              margin: const EdgeInsets.fromLTRB(8, 4, 8, 2),
              padding: const EdgeInsets.fromLTRB(0, 26, 34, 0),
              decoration: BoxDecoration(color: const Color(0xFF141821), borderRadius: BorderRadius.circular(12)),
              child: DefaultTabController(
                length: 2,
                child: Column(children: [
                  const TabBar(
                    labelColor: Color(0xFFE8C44A), indicatorColor: Color(0xFFE8C44A),
                    dividerColor: Colors.transparent, tabAlignment: TabAlignment.start,
                    isScrollable: true, tabs: [Tab(text: '分时'), Tab(text: '日K')]),
                  Expanded(
                    child: TabBarView(children: [
                      Padding(padding: const EdgeInsets.only(top: 4), child: TimeshareChart(points: ts, prevClose: prev)),
                      Padding(padding: const EdgeInsets.only(top: 4), child: CandleChart(candles: candles, lastPrice: lp)),
                    ]),
                  ),
                ]),
              ),
            ),
          ),
          Positioned(
            top: 30, right: 14,
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
              decoration: BoxDecoration(color: const Color(0xCC12161E), borderRadius: BorderRadius.circular(10)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.end, children: [
                Text('卖 ${a1 != null ? a1['price'] : '—'}', style: const TextStyle(color: Color(0xFF26A69A), fontSize: 11)),
                Text('${lp ?? '--'}', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w900,
                    color: lp != null && lp >= prev ? const Color(0xFFEF5350) : const Color(0xFF26A69A))),
                Text('买 ${b1 != null ? b1['price'] : '—'}', style: const TextStyle(color: Color(0xFFEF5350), fontSize: 11)),
              ]),
            ),
          ),
        ]),
      ),
      // ── 持仓摘要条 ──
      Container(
        margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        decoration: BoxDecoration(color: const Color(0xFF232A36), borderRadius: BorderRadius.circular(10)),
        child: Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text('权益 ${((state.acct?['totalEquity'] as num? ?? 0) / 10000).toStringAsFixed(0)}万',
              style: const TextStyle(color: Color(0xFFE8C44A), fontSize: 12)),
          Text('持仓 ${state.acct?['positionTotal']}手', style: const TextStyle(fontSize: 12)),
          Text('浮盈 ${((state.acct?['floatingProfit'] as num? ?? 0) >= 0 ? '+' : '')}${((state.acct?['floatingProfit'] as num? ?? 0) / 10000).toStringAsFixed(1)}万',
              style: TextStyle(fontSize: 12, color: (state.acct?['floatingProfit'] as num? ?? 0) >= 0 ? const Color(0xFFEF5350) : const Color(0xFF26A69A))),
          Text('现金 ${((state.acct?['availableCash'] as num? ?? 0) / 10000).toStringAsFixed(0)}万', style: const TextStyle(fontSize: 12)),
        ]),
      ),
      // ── 意图chips ──
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
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
        child: Row(children: [
          Text('段${(cs['turn'] as int? ?? 0) + 1} ${cs['phaseName']}'),
          const Spacer(),
          Text('能量 ${cs['energy']}/${cs['maxEnergy']}', style: const TextStyle(color: Color(0xFFFFE082))),
          const SizedBox(width: 12),
          Text('已出 $played/${cs['maxCardsPerTurn']}'),
        ]),
      ),
      Expanded(
        flex: 30,
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
