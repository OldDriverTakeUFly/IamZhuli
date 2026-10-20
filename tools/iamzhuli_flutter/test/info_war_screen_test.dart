// 信息战面板widget测试:渲染/时机门禁/操作回调(无真机, Fake客户端注入快照)
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:iamzhuli_flutter/engine/game_client.dart';
import 'package:iamzhuli_flutter/state/game_state.dart';
import 'package:iamzhuli_flutter/screens/info_war_screen.dart';
import 'package:iamzhuli_flutter/screens/data_screen.dart';

/// 假引擎:返回固定快照,记录操作调用。
class FakeClient implements GameClient {
  Map<String, dynamic> snap;
  final List<String> calls = [];
  FakeClient(this.snap);

  @override
  bool get serverDriven => true;
  @override
  Future<void> setClockRunning(bool running) async {}
  @override
  Future<void> loadLevel(String id) async {}
  @override
  Future<void> begin() async {}
  @override
  Future<Map<String, dynamic>?> step() async => snap;
  @override
  Future<Map<String, dynamic>> snapshot() async => snap;
  @override
  Future<Map<String, dynamic>> playCard(int i) async => {'ok': true};
  @override
  Future<Map<String, dynamic>> endTurn() async => {'ok': true};
  @override
  Future<void> nextDay() async {}
  @override
  Future<Map<String, dynamic>> endLevel() async => {'isVictory': false, 'stars': 0, 'objectives': []};
  @override
  Future<Map<String, dynamic>> score() async => {'rankings': []};
  @override
  Future<bool> saveGame(String slot) async => true;
  @override
  Future<bool> loadGame(String slot) async => true;
  @override
  Future<Map<String, dynamic>> publishNews(String type) async { calls.add('news:$type'); return {'ok': true}; }
  @override
  Future<Map<String, dynamic>> upgradeWaterArmy() async { calls.add('wa:upgrade'); return {'ok': true}; }
  @override
  Future<Map<String, dynamic>> toggleWaterArmy() async { calls.add('wa:toggle'); return {'ok': true}; }
  @override
  Future<Map<String, dynamic>> publishSignal(String type) async { calls.add('signal:$type'); return {'ok': true}; }
  @override
  Future<List<dynamic>> aiThoughts(int count) async => [
    {'day': 1, 'tickOfDay': 180, 'state': 'Distribute', 'detectedIntent': 'PushingUp', 'confidence': 0.8, 'reason': '→出货:现价高于成本5%,落袋为安'},
  ];
  @override
  Future<Map<String, dynamic>> setIntent(String id) async => {'ok': true};
}

Map<String, dynamic> snapOf({
  String phase = 'Morning', bool paused = false, bool preMarket = false,
  int waLevel = 2, bool waActive = true,
  List<Map<String, dynamic>>? news,
}) =>
    {
      'phase': phase, 'isPaused': paused, 'isPreMarket': preMarket,
      'isLevelOver': false, 'isFinished': false,
      'infoHeat': 25.0, 'regulatorHeat': 30.0, 'penaltyLevel': 'None',
      'confidence': 55, 'herdMood': 60, 'newsBias': 0.12,
      'waterArmyLevel': waLevel, 'waterArmyActive': waActive,
      'waterArmyDays': 3, 'waterArmyDailyCost': 20000,
      'activeNews': news ?? const [],
      'cardState': {'turn': 3, 'phase': 'Play', 'energy': 4, 'maxEnergy': 12,
          'cardsPlayed': 0, 'maxCardsPerTurn': 2, 'hand': []},
    };

Future<GameState> pumpScreen(WidgetTester tester, Map<String, dynamic> snap, FakeClient client) async {
  final gs = GameState(client);
  gs.snap = snap;   // 直接注入,绕过轮询
  await tester.pumpWidget(MaterialApp(home: Scaffold(body: InfoWarScreen(state: gs))));
  return gs;
}

void main() {
  testWidgets('渲染:关注度/水军状态/成本文案/活跃消息', (tester) async {
    final c = FakeClient(snapOf(news: [
      {'type': 'Positive', 'headline': '利好消息:机构研报看好,目标价上调', 'remainingTicks': 220},
    ]));
    final gs = await pumpScreen(tester, c.snap, c);
    expect(find.text('信息操纵关注'), findsOneWidget);
    expect(find.textContaining('Lv 2/5'), findsOneWidget);
    expect(find.textContaining('运营中 · 已3天'), findsOneWidget);
    expect(find.textContaining('维持 2万/日'), findsOneWidget);
    expect(find.textContaining('升级 Lv3'), findsOneWidget);       // 下一级15万一次性
    expect(find.textContaining('机构研报看好'), findsOneWidget);   // 活跃消息
    expect(find.textContaining('假大宗卖出'), findsOneWidget);     // 信号区
    expect(find.textContaining('上龙虎榜'), findsOneWidget);
    gs.dispose();
  });

  testWidgets('时机门禁:盘中消息按钮禁用,信号可用', (tester) async {
    final c = FakeClient(snapOf());   // phase=Morning 盘中
    final gs = await pumpScreen(tester, c.snap, c);
    final newsBtn = find.textContaining('利好');
    expect(tester.widget<OutlinedButton>(find.ancestor(of: newsBtn, matching: find.byType(OutlinedButton))).enabled, isFalse);
    expect(find.textContaining('盘中不可发消息'), findsOneWidget);
    final sigBtn = find.textContaining('假大宗买入');
    expect(tester.widget<OutlinedButton>(find.ancestor(of: sigBtn, matching: find.byType(OutlinedButton))).enabled, isTrue);
    gs.dispose();
  });

  testWidgets('收盘态:消息按钮启用且可点', (tester) async {
    final c = FakeClient(snapOf(phase: 'Closed', paused: true));
    final gs = await pumpScreen(tester, c.snap, c);
    final newsBtn = find.textContaining('传闻');
    expect(tester.widget<OutlinedButton>(find.ancestor(of: newsBtn, matching: find.byType(OutlinedButton))).enabled, isTrue);
    await tester.tap(newsBtn);
    await tester.pumpAndSettle();
    expect(c.calls, contains('news:rumor'));
    gs.dispose();
  });

  testWidgets('信号点击贯通 + 水军操作', (tester) async {
    final c = FakeClient(snapOf());
    final gs = await pumpScreen(tester, c.snap, c);
    await tester.tap(find.textContaining('上龙虎榜'));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('停止运营'));
    await tester.pumpAndSettle();
    expect(c.calls, containsAll(['signal:dragonlist', 'wa:toggle']));
    gs.dispose();
  });

  testWidgets('水军未建立:开关禁用,升级可用', (tester) async {
    final c = FakeClient(snapOf(waLevel: 0, waActive: false));
    final gs = await pumpScreen(tester, c.snap, c);
    final toggleBtn = find.textContaining('开启运营');
    expect(tester.widget<OutlinedButton>(find.ancestor(of: toggleBtn, matching: find.byType(OutlinedButton))).enabled, isFalse);
    final upBtn = find.textContaining('升级 Lv1');
    expect(tester.widget<OutlinedButton>(find.ancestor(of: upBtn, matching: find.byType(OutlinedButton))).enabled, isTrue);
    gs.dispose();
  });

  testWidgets('AI动向:状态汉化/识别你/独白渲染 + 空态', (tester) async {
    final c = FakeClient(snapOf());
    final gs = GameState(c);
    gs.snap = c.snap;
    gs.ai = [
      {'day': 1, 'tickOfDay': 180, 'state': 'Distribute', 'detectedIntent': 'PushingUp', 'confidence': 0.8, 'reason': '→出货:现价高于成本5%,落袋为安'},
    ];
    await tester.pumpWidget(MaterialApp(home: Scaffold(body: DataScreen(state: gs))));
    expect(find.text('AI主力动向'), findsOneWidget);
    expect(find.text('出货'), findsWidgets);                      // 状态汉化
    expect(find.textContaining('识别你:拉升 80%'), findsOneWidget); // 意图识别
    expect(find.textContaining('落袋为安'), findsOneWidget);        // 独白正文
    gs.dispose();
    // 空态
    final gs2 = GameState(FakeClient(snapOf()));
    gs2.snap = c.snap;
    await tester.pumpWidget(MaterialApp(home: Scaffold(body: DataScreen(state: gs2))));
    expect(find.textContaining('尚无动向记录'), findsOneWidget);
    gs2.dispose();
  });
}
