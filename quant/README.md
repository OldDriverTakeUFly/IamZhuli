# IamZhuli Quant — 真实行情因子计算(Python)

用 tushare 拉真实 A 股行情,在上面计算与 C# 模拟器 `IamZhuli.Factors` 对齐的因子。
本阶段(MVP)只做 **数据接入 → 因子计算 → 输出**,不做策略回测。

## 与 C# 项目的关系

| Python(本目录) | C#(`src/IamZhuli.Factors/`) | 用途 |
|---|---|---|
| `factors.momentum` | `MarketSignalTracker.Momentum` | 真实数据回测 / 模拟器 AI |
| `factors.vwap_deviation` | `VwapFactor.Deviation` | 同上 |
| `factors.obi_from_moneyflow` | `OrderBookImbalanceFactor.Compute` | 同上(语义近似,见下) |

两边逻辑等价、独立维护。改动任一边时请同步另一边(代码注释里有对应文件指引)。

> **OBI 语义差异**:C# 版基于盘口挂单(限价单深度);Python 版基于资金流向(主动成交金额)。方向一致但口径不同,不可直接对比绝对值——真实盘口档位数据需要 tushare level2(高积分)。

## 准备

### 1. 安装依赖

```bash
pip install -r quant/requirements.txt
```

### 2. 配置 token

复制示例文件并填入你的 tushare token(从 https://tushare.pro 个人主页获取):

```bash
cp quant/.env.example quant/.env
# 编辑 quant/.env,写入: TUSHARE_TOKEN=你的token
```

`.env` 已被 gitignore,不会入库。

## 运行

```bash
# 默认: 平安银行(000001.SZ)2024 全年
python3 quant/run.py

# 指定标的和区间
python3 quant/run.py 600519.SH 20230101 20240101   # 贵州茅台 2023 全年
```

输出:
- 终端打印各因子的统计量(均值 / 中位数 / 分位数)
- 因子表保存到 `quant/output/factors_<code>_<start>_<end>.csv`

## 接口积分要求

| 接口 | 积分 | 用途 | 不可用时的影响 |
|---|---|---|---|
| `daily`(日线) | 120 | momentum / vwap | 致命,必须可用 |
| `moneyflow`(资金流向) | 2000 | OBI | 自动降级,跳过 OBI |

moneyflow 不可用时,程序会打印明确提示并继续计算其余两个因子,不中断。

## 缓存

拉取的数据缓存到 `quant/cache/*.parquet`(按 ts_code+日期范围命名),二次运行不重复调 API,省积分额度。删除缓存文件即可强制重新拉取。`cache/` 已 gitignore。

## 目录结构

```
quant/
├── .env              # 你的 token(gitignored)
├── .env.example      # 示例
├── requirements.txt
├── data_loader.py    # tushare 接入 + 缓存
├── factors.py        # 三因子实现
├── run.py            # 主入口
├── cache/            # parquet 缓存(gitignored)
└── output/           # 因子表 csv(gitignored)
```

## 安全提示

`.env` 含 token,**确认它没进 git**:
```bash
git check-ignore -v quant/.env   # 应输出匹配的忽略规则
```

## 后续(本 MVP 不含)

- IC 分析(因子值 vs 未来收益的相关性)
- 分层回测(按因子值分组看收益差)
- 多标的 / 全市场
- 接入 level2 盘口数据做真正的 OBI
