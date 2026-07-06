# IamZhuli Unity 移植

Unity 客户端,通过 DLL 直引用逻辑层(单机模式,不需要后端)。

## 快速开始

### 1. 构建 DLL(在项目根目录执行)

```bash
bash unity/build-dlls.sh
```

编译逻辑层 `netstandard2.1` DLL 并复制到 `unity/IamZhuli.Unity/Assets/Plugins/`。

### 2. 安装 Unity

- 需要 **Unity 6 LTS** (6000.0.x)
- 安装 Unity Hub → 添加此版本 → 用 "Open Project" 打开 `unity/IamZhuli.Unity/` 目录

### 3. 首次打开

Unity 编辑器首次打开会:
- 恢复 Packages
- 导入 Plugins 里的 DLL
- 编译 Scripts

如果 DLL 更新了,重新执行 `build-dlls.sh` 然后 Unity 会自动检测变更。

### 4. 搭建场景

当前版本未提供 Scene 文件(二进制资产)。你需要在编辑器里:

1. 新建 Scene
2. 创建空 GameObject,挂载 `GameHost` 脚本
3. 创建 Canvas(UGUI)
4. 按下面布局创建 UI 元素并绑定脚本

## UI 布局指南

### OrderBookPanel(盘口五档)
- 5 个 Text(卖5→卖1,从上到下) → 绑定 `askTexts[0..4]`
- 1 个 Text(现价) → 绑定 `lastPriceText`
- 5 个 Text(买1→买5,从上到下) → 绑定 `bidTexts[0..4]`

### OrderFormPanel(下单)
- 2 个 InputField(价格、数量) → 绑定 `priceInput`、`qtyInput`
- 1 个 Toggle(市价/限价) → 绑定 `isMarketToggle`
- 2 个 Button(买入、卖出) → 绑定 `buyButton`、`sellButton`
- 1 个 Text(反馈) → 绑定 `messageText`

### AccountPanel(账户)
- 多个 Text(权益/现金/持仓/成本/监管/情绪) → 按字段绑定

### TradeListPanel(成交明细)
- 1 个 Text(放在 ScrollView 里) → 绑定 `tradeListText`

## 架构

```
逻辑层 DLL (netstandard2.1)
    ↑ 引用
GameHost.cs (MonoBehaviour,驱动 SimulationLoop)
    ↑ 事件/方法调用
UI 面板 (UGUI Text/Button)
```

- **GameHost** 替代 Web 端的 GameSingleton,用协程驱动 tick
- **MarketSnapshot** struct 供 UI 绑定(类似 Web 的 DTO)
- UI 面板订阅 `GameHost.OnTickEvent` 回调刷新

## 已实现

- ✅ 盘口五档(OrderBookPanel)
- ✅ 下单表单(OrderFormPanel:限价/市价/买/卖)
- ✅ 账户持仓(AccountPanel:权益/现金/持仓/成本/监管/情绪)
- ✅ 成交明细(TradeListPanel)

## 待实现(后续)

- 图表(K线/分时/筹码)——需要第三方图表插件
- 复盘系统 UI
- 盘前/盘后操作面板
- 关卡选择界面

## 文件结构

```
unity/
├── build-dlls.sh                          # DLL 构建脚本
├── README.md                              # 本文件
└── IamZhuli.Unity/
    ├── Assets/
    │   ├── Plugins/                       # 逻辑层 DLL
    │   │   ├── IamZhuli.Core.dll
    │   │   ├── IamZhuli.Engine.dll
    │   │   ├── IamZhuli.Factors.dll
    │   │   └── IamZhuli.Simulation.dll
    │   └── Scripts/
    │       ├── GameHost.cs                # 主控制器
    │       └── UI/
    │           ├── OrderBookPanel.cs      # 盘口五档
    │           ├── OrderFormPanel.cs      # 下单
    │           ├── AccountPanel.cs        # 账户
    │           └── TradeListPanel.cs      # 成交
    ├── Packages/
    │   └── manifest.json
    └── ProjectSettings/
        └── ProjectVersion.txt
```
