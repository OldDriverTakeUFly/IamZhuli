# 09 - Unity 移植方案

> 状态:执行中(2026-08)。本方案在**没有 Unity 编辑器**的环境下推进:所有可用纯文本+编译验证完成的部分先做完,等编辑器可用时"一键开场"。

## 1. 目标与约束

- **目标**: 单机 Unity 客户端,复用全部逻辑层(撮合/散户/AI/卡牌/监管/复盘),表现层对齐 Web 版。
- **约束**:
  - 开发机无 Unity 编辑器,且国内下载原版 Unity 困难(可能只有团结引擎)。
  - 逻辑层已多目标编译 `net8.0;netstandard2.1`,DLL 通道现成。
  - 不手写 `.unity`/`.prefab` YAML(引用复杂,易坏)——用**编辑器脚本**生成场景。

## 2. 分层架构(核心决策:Presenter 抽取)

移植前的问题:游戏流程(关卡加载/tick推进/卡牌回合/揭牌/结算/快照构建)全部住在 **Web 层的 `GameSingleton`** 里,Unity 要用就得复制一份 → 双份漂移。

**解法**: 抽出引擎无关的 **`src/IamZhuli.Presenter`** 类库:

```
┌─────────────────────────────┬──────────────────────────────┐
│  Web 表现层 (ASP.NET)        │  Unity 表现层 (UI Toolkit)     │
│  GameSingleton(薄壳):        │  GameHost(MonoBehaviour):     │
│   · SemaphoreSlim 异步锁     │   · 协程驱动 tick              │
│   · SignalR 推送             │   · UXML/USS 绑定              │
├─────────────────────────────┴──────────────────────────────┤
│              IamZhuli.Presenter (netstandard2.1)            │
│  GameCore: 关卡/预演/tick/卡牌回合/揭牌/消息战/结算/快照DTO     │
├───────────────────────────────────────────────────────────┤
│  IamZhuli.Simulation / Engine / Core  (现有逻辑层,不变)      │
└───────────────────────────────────────────────────────────┘
```

**线程模型**: `GameCore` 单线程亲和(自身不加锁)。Web 由 GameSingleton 的信号量包住;Unity 主线程直调,天然安全。

**DTO 随迁**: `MarketSnapshotDto`/`AccountDto`/`CardStateDto` 等全部移入 Presenter,两个表现层共用,字段增删只改一处。

## 3. Unity 技术选型

| 项 | 选择 | 理由 |
|----|------|------|
| UI 框架 | **UI Toolkit (UXML/USS)** | 纯文本资产,可离线写全;类 HTML/CSS,Web 版布局可 1:1 翻译;2021.3+ 全支持 |
| 场景生成 | **Editor 菜单脚本 SceneBuilder** | 绕开手写 scene YAML;用户打开 Unity 后 `菜单 → 我是主力 → 一键搭建场景` |
| tick 驱动 | 协程 `yield return null` | 每帧一步,速度可调(1x/3x/10x);卡牌 Play 阶段自动冻结 |
| K线/分时 | UI Toolkit `generateVisualContent` 自绘 Mesh | 无第三方依赖;v1 先分时折线+成交量,v2 日K蜡烛 |
| 图表库 | 不引入 | 团结引擎包生态不确定,自绘最稳 |
| 程序集 | `IamZhuli.Runtime.asmdef` + `IamZhuli.Editor.asmdef` | 编译隔离;Plugins 下逻辑 DLL 保持 Auto-Reference |

## 4. 兼容矩阵

| 编辑器 | 能否打开 | 说明 |
|--------|---------|------|
| Unity 2022.3 LTS | ✓ 直接开 | `ProjectVersion.txt` 固定为 2022.3 |
| Unity 6 (6000.x) | ✓ 提示升级后开 | UI Toolkit API 完全兼容 |
| 团结引擎 1.x | ✓ 直接开 | 2022.3 分支,二进制兼容 |

API 纪律: 只用 2022.3 就有的 API,不用 Unity 6 独占特性。

## 5. 离线可完成 vs 需要编辑器

| 工作 | 离线可完成? | 状态 |
|------|:--:|------|
| Presenter 抽取 + 全量测试 | ✓ | M1 |
| UXML/USS 界面全套 | ✓ | M2 |
| UI 控制器 C#(UI Toolkit 绑定) | ✓ | M2 |
| GameHost(MonoBehaviour 桥) | ✓(不可编译验证) | M2 |
| SceneBuilder 编辑器脚本 | ✓(不可编译验证) | M3 |
| asmdef/build 脚本/文档 | ✓ | M3 |
| 场景生成、真机运行、图表调优 | ✗ 需编辑器 | M4(等环境) |
| 复盘 UI、筹码峰 Unity 版 | ✗ 需编辑器 | M5 |

## 6. 里程碑

- **M1 Presenter 抽取** ✅(1d25131): `src/IamZhuli.Presenter` 建成,`GameCore` 收编 GameSingleton 全部游戏流程;Web 变薄壳;149 测试 + 端到端全过。顺手修 3 个存量 bug(信号牌 null 监管、融券利润双计、保证金凭空增发)。
- **M2 UI Toolkit 界面** ✅(离线): `Main.uxml`/`Theme.uss`(四栏+卡牌手牌+揭牌+关卡选择+结算)、`MainUI` 控制器、`TimeshareElement` 自绘分时、`GameHost` 桥。
- **M3 工程配置** ✅(离线): Runtime/Editor asmdef、SceneBuilder 一键建场景、build-dlls.sh 纳入 Presenter、README 三步开场、ProjectVersion 固定 2022.3.22f1。
- **M4 编辑器接入**(需 Unity): 一键建场景 → 跑通一局 → 图表/布局调优。
- **M5 对齐 Web 功能**(需 Unity): 复盘回放、筹码峰、组牌界面。

## 7. 数据流(Unity)

```
GameHost.Update() ─每帧→ GameCore.Step() → MarketSnapshotDto
                                   ↓
UI 控制器(订阅 GameHost.OnSnapshot)→ UXML 元素刷新
玩家输入(出牌/下单/结束回合) → GameCore.PlayCard/Submit/EndCardTurn
揭牌结果 AuctionResultDto → 揭牌面板(买方→卖方@价)
```

## 8. 验证策略

- M1: `dotnet build` 0 错误、149+ 测试全过、Web 端到端 curl(选关→出牌→揭牌→结算数字吻合)。
- M2/M3: C# 侧无法编译(UnityEngine 引用缺失),靠 API 纪律(只用稳定 API)+ 用户打开编辑器后首轮编译零错的验收标准;UXML/USS 为声明式,风险低。
- **测试三层**:
  1. `unity/validate-offline.sh`——离线静态门禁(UXML/JSON 语法、C# 版本纪律、MainUI↔UXML 43 个元素名一致性),**不装 Unity 即跑**,提交前必过;
  2. Unity Test Framework——EditMode 9 个(GameCore 全流程冒烟/UXML 一致性/场景搭建接线)+ PlayMode 2 个(GameHost 帧驱动/出牌冻结),装好编辑器后在 Test Runner 一键跑;
  3. `unity/run-tests.sh`——Unity CLI 跑器(NUnit XML 结果,EditMode 支持无图形),供 CI。
- 每步 git 提交,可回退。
