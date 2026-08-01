# 同时出牌+集中撮合重构

## 核心变化
从"逐tick连续撮合"改为"回合末集中撮合"。所有人同时出牌(暗牌),回合末统一按最大成交量原则撮合。

## 一、新增:OrderIntent(订单意图,不立即撮合)

**新概念** — 代替直接调 Session.Submit,所有参与者(玩家/AI/散户)的出牌先收集为 OrderIntent:

```csharp
public record OrderIntent(
    ParticipantId Participant, Side Side, OrderType Type,
    decimal Price, int Qty, bool IsShort);
```

回合 Play 阶段:所有人产出 OrderIntent → 存入 `List<OrderIntent> _pendingIntents`。
回合揭牌:把 _pendingIntents 全部 Rest 到清空的订单簿 → CallAuction 定价 → SweepAtPrice 撮合。

## 二、新增:MatchingEngine.SweepAtPrice

现有 CallAuction 只定价不撮合。新增 SweepAtPrice 执行实际撮合:

```csharp
/// 在指定价格一次性吃掉所有交叉订单(买单≥price吃卖单≤price)。
/// 返回成交列表。
public List<Trade> SweepAtPrice(Price auctionPrice)
```

逻辑:遍历所有 bid levels(价格≥auctionPrice)和 ask levels(价格≤auctionPrice),按价格优先+时间优先撮合,直到一方耗尽。

## 三、AI 改为出牌制

给 AIMainForce 和 InstitutionB 加 `ProduceIntents()` 方法(替代 Act 中的 session.Submit):

**AIMainForce**: 状态机决策 → AIState 映射为 CardEffect → 转为 OrderIntent
- Defend → 限价买入(LimitBuy)
- Wash → 市价卖出(MarketSell)
- Distribute → 限价卖出(ScheduledSell/MarketSell)
- Follow → 市价买入(MarketBuy)
- Counter → 市价卖出+做空(MarketSell/ShortSell)

**InstitutionB**: 风险等级 → 做市挂墙+方向操作
- Low/Mid → LimitWall(买卖各挂)
- High → 减仓(MarketSell)
- Critical → 方向操作(MarketSell/ShortSell)

## 四、回合流程重构

```
回合开始(Play阶段):
  1. 玩家出牌(0-2张) → ExecuteCardEffect 改为收集 OrderIntent
  2. AI 出牌 → ProduceIntents → 收集 OrderIntent
  3. 散户出牌 → 根据 stale 价格生成 OrderIntent
  4. 盘面冻结(等所有人出完)

揭牌(EndTurn):
  5. 清空订单簿
  6. 所有 OrderIntent → Rest 到订单簿(限价单)/ 待吃(市价单)
  7. CallAuction → 算出成交量最大化价格 P
  8. SweepAtPrice(P) → 一次性撮合
  9. 结算成交(复用现有 ApplyBuyFill/ApplySellFill/ShortSell/ShortCover)
  10. 清空 _pendingIntents

Resolve阶段(30 tick):
  11. 价格已定(P),散户根据 P 做被动反应(止盈止损等)
  12. 延迟效果(分批出货)逐步执行
  13. 30 tick 后进入下一回合
```

## 五、舆论/信号牌的处理

舆论牌(利好/利空/水军)和信号牌(假大宗)不产生 OrderIntent,而是直接影响情绪/监管。这些在揭牌时立即生效(不改撮合,改情绪),让 Resolve 阶段的散户反应体现效果。

## 六、GameSingleton 集成

- PlayCard 的 ExecuteCardEffect 改为:操盘牌→收集 OrderIntent,舆论/信号牌→立即生效
- EndCardTurn 时:收集 AI intents + 散户 intents → 统一撮合
- Resolve 阶段 OnTick:散户被动反应 + 延迟效果

## 七、分步实施

1. **MatchingEngine.SweepAtPrice**(集中撮合核心)。单测。
2. **OrderIntent 收集机制**(CardEngine 改为收集不执行)。
3. **AI ProduceIntents**(AIState→CardEffect→OrderIntent)。
4. **回合揭牌流程**(EndTurn → 收集→Rest→CallAuction→Sweep→结算)。
5. **散户回合出牌**(从逐tick Act 改为回合 ProduceIntents)。
6. **GameSingleton 集成 + 前端适配**。
7. **测试 + 文档**。

## 八、改动文件

| 文件 | 改动 |
|------|------|
| `Engine/MatchingEngine.cs` | +SweepAtPrice |
| `Engine/OrderBook.cs` | +批量枚举/清理辅助 |
| `Cards/CardEngine.cs` | PlayCard 改为收集 intent |
| `AI/AIMainForce.cs` | +ProduceIntents(AIState→OrderIntent) |
| `AI/InstitutionB.cs` | +ProduceIntents |
| `GameSingleton.cs` | EndCardTurn 改为集中撮合流程 |
| `wwwroot/index.html` | 揭牌动画/结果展示 |

## 九、不做的事
- 不改 SimulationLoop 的 Step()(Resolve 阶段仍逐tick)
- 不改连续交易模式(卡牌模式才用集中撮合)
- 不做暗牌隐藏(AI 出牌对玩家可见,或后续加隐藏)