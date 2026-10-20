import 'package:flutter/material.dart';
import '../state/game_state.dart';

/// 盘外tab:信息战面板(消息发布/水军网络/信号误导)。
/// 数据全部来自轮询快照(activeNews/waterArmy*/infoHeat/情绪),操作走 GameState。
/// 成本/时机文案如实对应逻辑层:消息限收盘后/盘前且问询期禁发;
/// 水军升级一次性 Lv×5万、运营 Lv×1万/日;信号盘中可用但触发信息操纵关注。
class InfoWarScreen extends StatelessWidget {
  final GameState state;
  const InfoWarScreen({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final snap = state.snap;
    if (snap.isEmpty || snap['cardState'] == null && snap['phase'] == 'Waiting') {
      return const Center(child: Text('未开局'));
    }
    final newsGate = snap['isPreMarket'] == true ||
        ((snap['phase'] as String?) == 'Closed' && (snap['isPaused'] as bool? ?? false));
    final over = snap['isLevelOver'] == true || snap['isFinished'] == true;
    final activeNews = ((snap['activeNews'] as List?) ?? []).cast<Map<String, dynamic>>();
    final waLevel = (snap['waterArmyLevel'] as num?)?.toInt() ?? 0;
    final waActive = snap['waterArmyActive'] == true;
    final waDays = (snap['waterArmyDays'] as num?)?.toInt() ?? 0;
    final waDaily = ((snap['waterArmyDailyCost'] as num?) ?? 0) / 10000;
    final infoHeat = (snap['infoHeat'] as num? ?? 0).toDouble();
    final conf = (snap['confidence'] as num? ?? 0).toInt();
    final herd = (snap['herdMood'] as num? ?? 0).toInt();
    final bias = (snap['newsBias'] as num? ?? 0).toDouble();

    return ListView(padding: const EdgeInsets.all(12), children: [
      _heat('信息操纵关注', infoHeat, '问询/罚单来源之一'),
      _heat('交易监管热度', (snap['regulatorHeat'] as num? ?? 0).toDouble(), '${snap['penaltyLevel']}'),
      const SizedBox(height: 8),
      Row(mainAxisAlignment: MainAxisAlignment.spaceAround, children: [
        _stat('散户信心', '$conf'),
        _stat('群体热度', '$herd'),
        _stat('消息偏差', bias.toStringAsFixed(2)),
      ]),
      const Divider(height: 28, color: Color(0xFF39414F)),

      _title('消息发布', '收盘后/盘前可发 · 问询期禁发 · 隔夜效果减半'),
      Wrap(spacing: 8, runSpacing: 8, children: [
        _op(context, '利好\n免费 · 信心+15%', newsGate && !over, () => state.publishNews('positive')),
        _op(context, '利空\n免费 · 信心-15%', newsGate && !over, () => state.publishNews('negative')),
        _op(context, '传闻\n0.5万 · 群体热度+', newsGate && !over, () => state.publishNews('rumor')),
        _op(context, '水军造势\n2万 · 贪婪+8%长期', newsGate && !over, () => state.publishNews('pump')),
      ]),
      if (!newsGate && !over)
        const Padding(padding: EdgeInsets.only(top: 6),
            child: Text('⏳ 盘中不可发消息——等收盘横幅或次日盘前', style: TextStyle(fontSize: 11, color: Color(0xFF8A94A6)))),
      if (activeNews.isNotEmpty) ...[
        const Padding(padding: EdgeInsets.only(top: 10, bottom: 4),
            child: Text('活跃消息', style: TextStyle(fontSize: 12, color: Color(0xFF97A3B5)))),
        for (final n in activeNews)
          Container(
            margin: const EdgeInsets.only(bottom: 4),
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            decoration: BoxDecoration(color: const Color(0xFF1B2130), borderRadius: BorderRadius.circular(8)),
            child: Row(children: [
              Expanded(child: Text('${n['headline']}',
                  maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12))),
              Text('${n['remainingTicks']}t', style: const TextStyle(fontSize: 11, color: Color(0xFF8A94A6))),
            ]),
          ),
      ],
      const Divider(height: 28, color: Color(0xFF39414F)),

      _title('水军网络', '运营中每日自动注入造势消息(贪婪+)'),
      Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: Row(children: [
          Text('Lv $waLevel/5', style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: Color(0xFFE8C44A))),
          const SizedBox(width: 12),
          Text(waActive ? '运营中 · 已${waDays}天' : (waLevel > 0 ? '已停运' : '未建立'),
              style: TextStyle(fontSize: 12, color: waActive ? const Color(0xFF26A69A) : const Color(0xFF8A94A6))),
          const Spacer(),
          Text(waLevel > 0 ? '维持 ${waDaily.toStringAsFixed(0)}万/日' : '',
              style: const TextStyle(fontSize: 12, color: Color(0xFF97A3B5))),
        ]),
      ),
      Wrap(spacing: 8, runSpacing: 8, children: [
        _op(context, waLevel >= 5 ? '已满级' : '升级 Lv${waLevel + 1}\n${(waLevel + 1) * 5}万一次性',
            !over && waLevel < 5, state.upgradeWaterArmy),
        _op(context, waActive ? '停止运营\n效果渐消' : '开启运营\n每日自动造势',
            !over && waLevel > 0, state.toggleWaterArmy),
      ]),
      const Divider(height: 28, color: Color(0xFF39414F)),

      _title('信号误导', '盘中可用 · 每次都推高信息操纵关注'),
      Wrap(spacing: 8, runSpacing: 8, children: [
        _op(context, '假大宗卖出\n10万 · 散户恐慌抛售', !over, () => state.publishSignal('fakebigsell')),
        _op(context, '假大宗买入\n10万 · 散户跟风进场', !over, () => state.publishSignal('fakebigbuy')),
        _op(context, '上龙虎榜\n5万 · 群体热度暴增', !over, () => state.publishSignal('dragonlist')),
      ]),
      const SizedBox(height: 20),
      if (state.lastMsg.isNotEmpty)
          Center(child: Text(state.lastMsg, style: const TextStyle(color: Color(0xFF81D4FA), fontSize: 12))),
    ]);
  }

  Widget _heat(String label, double v, String suffix) {
    final color = v >= 60 ? const Color(0xFFEF5350) : (v >= 30 ? const Color(0xFFE8C44A) : const Color(0xFF26A69A));
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Text(label, style: const TextStyle(fontSize: 12, color: Color(0xFF97A3B5))),
          const Spacer(),
          Text('${v.toStringAsFixed(0)}', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: color)),
          const SizedBox(width: 6),
          Text(suffix, style: const TextStyle(fontSize: 10, color: Color(0xFF8A94A6))),
        ]),
        ClipRRect(
          borderRadius: BorderRadius.circular(3),
          child: LinearProgressIndicator(value: (v / 100).clamp(0.0, 1.0), minHeight: 4, color: color,
              backgroundColor: const Color(0xFF232A36)),
        ),
      ]),
    );
  }

  Widget _stat(String k, String v) => Column(children: [
        Text(v, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800, color: Color(0xFFD7DCE4))),
        Text(k, style: const TextStyle(fontSize: 11, color: Color(0xFF8A94A6))),
      ]);

  Widget _title(String t, String sub) => Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(t, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
          Text(sub, style: const TextStyle(fontSize: 11, color: Color(0xFF8A94A6))),
        ]),
      );

  Widget _op(BuildContext context, String label, bool enabled, VoidCallback fn) {
    return SizedBox(
      width: 158,
      child: OutlinedButton(
        style: OutlinedButton.styleFrom(
          foregroundColor: const Color(0xFFD8C58A),
          side: BorderSide(color: enabled ? const Color(0xFF6B6236) : const Color(0xFF39414F)),
          padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 8),
          minimumSize: const Size(0, 44),
        ),
        onPressed: enabled ? fn : null,
        child: Text(label, textAlign: TextAlign.center, style: const TextStyle(fontSize: 11.5, height: 1.35)),
      ),
    );
  }
}
