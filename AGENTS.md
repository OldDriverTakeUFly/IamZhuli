# AGENTS.md — IamZhuli 项目 Agent 协作规范

> 本文件由 ZCode(及兼容 agent)自动加载,定义本项目的工作约定。所有 agent 在本项目内执行任务时遵循以下规范。

## 项目概述

**IamZhuli(我是主力)** —— 一款模拟真实股票操纵的主力视角模拟器。采用"纯 C# 类库做游戏大脑 + Unity 做表现层"的架构。详见 `docs/` 目录下的设计文档。

- **核心语言**:C# (.NET)
- **架构**:逻辑层(类库,可单元测试) 与 表现层(Unity) 完全分离
- **引擎**:Unity(表现层,后期接入)
- **阶段**:第一期 POC(纯交易,详见 `docs/01-游戏设计文档.md` 第 13 节)

## rtk 使用规范(重要 — 节省 token)

本项目已本地部署 **rtk**(CLI 代理,过滤/压缩命令输出以节省 LLM token)。**执行命令时必须优先使用 rtk 包装**,尤其针对输出冗长的命令。

### 必须用 rtk 的场景

| 原命令 | 用法 | 场景 |
|--------|------|------|
| `dotnet build` | `rtk dotnet build` | 编译,输出长 |
| `dotnet test` | `rtk dotnet test` | 跑测试,输出长 |
| `dotnet restore` | `rtk dotnet restore` | 还原依赖 |
| `ls` | `rtk ls` | 列目录 |
| `find` | `rtk find ...` | 查找文件 |
| `grep` | `rtk grep ...` 或 `rtk rg ...` | 搜索内容 |
| `git status/log/diff` | `rtk git status` 等 | git 操作 |
| `cat 大文件` | `rtk read <file>` | 读文件(智能过滤) |

### 无需 rtk 的场景

- `dotnet new`、`dotnet add package`(输出短)
- `mkdir`、`mv`、`rm`、`cp`(无长输出)
- 简单的 `echo`、`pwd`
- 已知输出极短的命令

### 原则

- **不确定输出多长时,默认用 rtk**(代价低,收益高)
- rtk 不改变命令语义,只压缩输出,可放心使用
- 查看 token 节省情况:`rtk gain`

## 代码约定

### C# 代码风格

- **命名**:PascalCase(类、方法、属性);camelCase(局部变量、参数);`_camelCase`(私有字段)。
- **货币用 `decimal`**,**绝不**用 `double`/`float` 算钱(撮合 bug 的万恶之源)。
- **价格/数量**有明确的值类型或带单位的命名(如 `priceCents` 或专用 `Price`/`Quantity` 类型),避免裸 `decimal` 混用。
- **逻辑层零 Unity 依赖**:撮合引擎、AI、规则等核心逻辑放在不引用 UnityEngine 的类库项目里。
- **可测试优先**:核心逻辑必须有单元测试;撮合规则错了游戏就崩,必须可验证。

### 项目结构(随开发推进填充)

```
IamZhuli/
├── AGENTS.md                  # 本文件
├── docs/                      # 设计文档与开发规范
├── src/                       # 源码(逻辑层类库 + 表现层)
└── tests/                     # 单元测试
```

## 文档约定

- 设计文档放 `docs/`,编号前缀(如 `01-`、`02-`)保持顺序。
- 重大设计决策的讨论结果及时回写到 `docs/01-游戏设计文档.md` 对应章节。
- 开发计划与里程碑见 `docs/03-开发计划.md`(待创建)。

## 协作流程

- **不擅自扩大范围**:严格按设计文档的 POC 范围(第一期纯交易)实施,信息战/融资融券/沙盒是后续阶段。
- **改动前读规范**:动手写代码前先读 `docs/02-开发规范.md`。
- **提交规范**(如启用 git):Conventional Commits 中文 scope,如 `feat(engine): 实现订单簿基础撮合`。
