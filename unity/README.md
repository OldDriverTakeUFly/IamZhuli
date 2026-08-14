# IamZhuli Unity 客户端

单机 Unity 版,通过 DLL 直引用全部逻辑层(与 Web 版共用 `IamZhuli.Presenter.GameCore`,零逻辑复制)。
UI 用 **UI Toolkit**(UXML/USS 纯文本),场景用**编辑器脚本一键生成**。

## 三步开场

```bash
# 1. 构建逻辑层 DLL(项目根目录执行)
bash unity/build-dlls.sh
```

```
2. 用 Unity 打开 unity/IamZhuli.Unity/ 目录
   (Unity 2022.3 LTS / Unity 6 / 团结引擎 均可,详见 docs/09-Unity移植方案.md)

3. 菜单栏 → 我是主力 → 一键搭建场景 → 保存后点 ▶ Play
```

首个画面是关卡选择(盘前态,背景可见预演历史K线),选关 → 看简报 → **开始操盘**。

## 已实现(离线完成,待编辑器验收)

- ✅ GameHost 桥:主线程驱动 GameCore,速度 1x/3x/10x、暂停
- ✅ 主界面(UI Toolkit):顶栏时钟/价格/情绪/监管,四栏布局对齐 Web 版
- ✅ 盘口五档 + 成交明细滚动
- ✅ 分时图自绘(价格折线+昨收基准+量柱,Mesh 无第三方依赖)
- ✅ 卡牌模式:手牌(按类别着色/能量费用/可出牌置灰)、回合/能量、揭牌面板(买方→卖方@价,涉及你高亮)
- ✅ 账户(权益/现金/持仓/T+1/浮盈/空头担保)、关卡目标进度
- ✅ 关卡选择 + 盘前简报 + 开始操盘;结算覆盖层(星级+三方排名+再来一局)
- ✅ 连续模式快捷市价单(买入/卖出/撤全部)
- ✅ SceneBuilder:菜单一键建场景(PanelSettings+UIDocument+脚本挂载)

## 待编辑器环境(M4/M5)

- 场景实际生成与首轮编译零错验收
- 日K蜡烛图、筹码峰、复盘回放、组牌界面
- 移动端适配(布局已是 flex,预计改动小)

## 架构

```
GameHost (MonoBehaviour: Update 驱动 + 事件分发)
    │
IamZhuli.Presenter.GameCore (DLL,与 Web 共用:关卡/tick/卡牌回合/揭牌/结算/快照)
    │
IamZhuli.Simulation / Engine / Core (DLL:撮合/散户/AI/监管/复盘)
```

- **UI**: `Assets/IamZhuli/UI/Main.uxml` + `Theme.uss`(暗色主题)
- **控制器**: `Assets/IamZhuli/Scripts/UI/MainUI.cs`(绑定+渲染+输入)
- **自绘图表**: `Scripts/UI/TimeshareElement.cs`(generateVisualContent)
- **场景生成**: `Assets/IamZhuli/Editor/SceneBuilder.cs`

## DLL 更新

逻辑层代码改动后:重新执行 `bash unity/build-dlls.sh`,回 Unity 等待自动重编译。

## 测试(三层,由早到晚)

### 1. 离线静态门禁(不装 Unity,现在就跑)

```bash
bash unity/validate-offline.sh
```

检查 UXML 语法 / asmdef·manifest JSON / C# 版本纪律(禁 C#10+ 特性,保 2022.3 兼容) / 括号平衡 /
**MainUI↔UXML 元素名一致性**(解析 43 个 Q 查询对照 UXML) / UxmlFactory。提交前必跑。

### 2. 编辑器内 Test Runner(装好 Unity 后)

`Window → General → Test Runner`:

- **EditMode**(9 个): `GameCoreSmokeTests`(DLL 加载/预演/手牌/揭牌/整局自动打到结算+三方排名/重试)、
  `UxmlSanityTests`(UXML 元素齐全/主题加载/自绘图表实例化与数据安全)、`SceneBuilderTests`(一键场景接线)
- **PlayMode**(2 个): GameHost 真实帧驱动发快照、卡牌出牌阶段时钟冻结

### 3. CLI 跑器(免开编辑器,CI 可用)

```bash
UNITY_PATH=~/Unity/Hub/Editor/2022.3.22f1/Editor/Unity bash unity/run-tests.sh All
# 或只跑 EditMode(支持无图形 -batchmode):
UNITY_PATH=... bash unity/run-tests.sh EditMode
```

结果 NUnit XML 在 `unity/test-results/`,附摘要打印。

> 逻辑层本身(撮合/散户/AI/卡牌结算)的 149 个 xUnit 测试仍在仓库根目录跑:
> `rtk dotnet test`——Unity 只测"桥与皮",内核测试不重复。
