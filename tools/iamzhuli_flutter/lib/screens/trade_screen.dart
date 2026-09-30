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
    // 日切态:市场时钟收盘+暂停(等玩家开下一日)——横幅替代意图chips/段信息
    final dayClosed = (state.snap['phase'] as String?) == 'Closed' &&
        (state.snap['isPaused'] as bool? ?? false);
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
      // ── 目标进度条(局中可见胜负距离,结算不用盲等) ──
      if (!dayClosed) ...[
        Builder(builder: (context) {
          final objs = ((state.snap['objectives'] as List?) ?? []).cast<Map<String, dynamic>>();
          if (objs.isEmpty) return const SizedBox.shrink();
          return Container(
            margin: const EdgeInsets.fromLTRB(8, 4, 8, 0),
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
            decoration: BoxDecoration(color: const Color(0xFF1B2130), borderRadius: BorderRadius.circular(8)),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              for (final o in objs)
                Row(children: [
                  Text(o['achieved'] == true ? '✔' : '○',
                      style: TextStyle(fontSize: 12, color: o['achieved'] == true ? const Color(0xFF26A69A) : const Color(0xFFE8C44A))),
                  const SizedBox(width: 6),
                  Expanded(child: Text('${o['description']}',
                      maxLines: 1, overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontSize: 12, color: Color(0xFFC9D1DE)))),
                  Text('${(((o['progress'] as num?) ?? 0) * 100).round()}%',
                      style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w800, color: Color(0xFFE8C44A))),
                ]),
            ]),
          );
        }),
      ],
      // ── 上回合揭牌条(集中撮合结果;Web揭牌面板的手机版) ──
      if (cs['lastAuction'] is Map<String, dynamic>) ...[
        Builder(builder: (context) {
          final ra = cs['lastAuction'] as Map<String, dynamic>;
          final trades = (ra['trades'] as List? ?? []).cast<Map<String, dynamic>>();
          int myBuy = 0, mySell = 0;
          bool shortSell = false, shortBuy = false;
          for (final t in trades) {
            if (t['buyer'] == '你') { myBuy += (t['qty'] as num).toInt(); shortBuy = shortBuy || t['buyerIsShort'] == true; }
            if (t['seller'] == '你') { mySell += (t['qty'] as num).toInt(); shortSell = shortSell || t['sellerIsShort'] == true; }
          }
          final vol = (ra['volume'] as num?)?.toInt() ?? 0;
          final mine = myBuy == 0 && mySell == 0 ? ''
              : ' · 你 ${myBuy > 0 ? '买$myBuy手${shortBuy ? '(融)' : ''}' : ''}${myBuy > 0 && mySell > 0 ? '/' : ''}${mySell > 0 ? '卖$mySell手${shortSell ? '(融)' : ''}' : ''}';
          return Container(
            margin: const EdgeInsets.fromLTRB(8, 2, 8, 2),
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
            decoration: BoxDecoration(color: const Color(0xFF1E2430), borderRadius: BorderRadius.circular(8),
              border: Border.all(color: const Color(0x33E8C44A))),
            child: Text(
              vol > 0
                  ? '🏛 段${(ra['turn'] as num).toInt() + 1}揭牌 · 竞价价 ${ra['price']} · ${vol}手$mine'
                  : '🏛 段${(ra['turn'] as num).toInt() + 1}申报 · 无撮合,挂单保留$mine',
              maxLines: 1, overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 11, color: Color(0xFFD8C58A)),
            ),
          );
        }),
      ],
      // ── 意图chips / 日切横幅(互斥) ──
      if (dayClosed)
        Container(
          margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
          decoration: BoxDecoration(color: const Color(0xFF2B2517), borderRadius: BorderRadius.circular(10),
            border: Border.all(color: const Color(0xFFE8C44A))),
          child: Column(children: [
            Row(children: [
              const Text('🏁 收盘', style: TextStyle(color: Color(0xFFE8C44A), fontSize: 13, fontWeight: FontWeight.w700)),
              const SizedBox(width: 8),
              Expanded(child: Text('第${state.snap['currentDay']}日结束 · T+1解锁/消息日切',
                  style: const TextStyle(fontSize: 11, color: Color(0xFFBDB28A)))),
              FilledButton.tonal(
                style: FilledButton.styleFrom(
                  backgroundColor: const Color(0xFFE8C44A), foregroundColor: const Color(0xFF14171E),
                  padding: const EdgeInsets.symmetric(horizontal: 14), minimumSize: const Size(0, 34)),
                onPressed: state.nextDay,
                child: const Text('开始下一日 ▶', style: TextStyle(fontSize: 12)),
              ),
            ]),
            Row(mainAxisAlignment: MainAxisAlignment.end, children: [
              TextButton.icon(
                style: TextButton.styleFrom(
                  foregroundColor: const Color(0xFFD8C58A), minimumSize: const Size(0, 32),
                  padding: const EdgeInsets.symmetric(horizontal: 10), textStyle: const TextStyle(fontSize: 12)),
                onPressed: state.saveGame, icon: const Icon(Icons.save_outlined, size: 16), label: const Text('保存进度')),
              TextButton.icon(
                style: TextButton.styleFrom(
                  foregroundColor: const Color(0xFFD8C58A), minimumSize: const Size(0, 32),
                  padding: const EdgeInsets.symmetric(horizontal: 10), textStyle: const TextStyle(fontSize: 12)),
                onPressed: state.loadGame, icon: const Icon(Icons.folder_open_outlined, size: 16), label: const Text('读档')),
            ]),
          ]),
        )
      else ...[
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
      // 意图效果提示(如实描述基础盘机制,让选择有依据)
      Builder(builder: (context) {
        const hints = {
          'accumulate': '基础盘:每段限价接盘800手(买一下方),低调建仓不惊动监管',
          'push': '基础盘:每段市价买入800手,主动推价但显眼',
          'wash': '基础盘:每段上压600+下托600,双向夹挤清洗浮筹',
          'distribute': '基础盘:每段卖一队尾挂800手,被动派发不砸价',
          'defend': '基础盘:每段上下各护600手,夹缝维稳等热度衰减',
          'none': '无基础盘,成交全靠卡牌与AI自主行为',
        };
        final cur = (cs['intent'] ?? 'none').toString().toLowerCase();
        return Padding(
          padding: const EdgeInsets.fromLTRB(14, 2, 14, 0),
          child: Text(hints[cur] ?? hints['none']!,
              maxLines: 1, overflow: TextOverflow.ellipsis,
              style: const TextStyle(fontSize: 10, color: Color(0xFF8A94A6))),
        );
      }),
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
      ],
      Expanded(
        flex: 30,
        child: ListView.builder(
          scrollDirection: Axis.horizontal,
          itemCount: hand.length,
          itemBuilder: (context, i) {
            final c = hand[i];
            // 禁用态:非出牌阶段/本段已出满/能量不足(Unity端同规则)
            final maxPerTurn = (cs['maxCardsPerTurn'] as num?)?.toInt() ?? 2;
            final energy = (cs['energy'] as num?)?.toInt() ?? 0;
            final cost = ((c['energyCost'] as num?) ?? 0).toInt();
            final canPlay = inPlay && played < maxPerTurn && cost <= energy;
            return GestureDetector(
              onTap: canPlay ? () => state.playCard(i) : null,
              child: Opacity(
                opacity: canPlay ? 1 : 0.4,
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
