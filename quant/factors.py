"""因子计算(真实数据版)。

逻辑与 C# 项目 src/IamZhuli.Factors/ 对齐:
  - momentum          ← MarketSignalTracker.Momentum
  - vwap_deviation    ← VwapFactor.Deviation
  - obi_from_moneyflow← OrderBookImbalanceFactor.Compute(语义近似版)

两边逻辑等价但独立维护:Python 版服务真实数据回测,C# 版服务模拟器。
改动任一边时,记得同步另一边(注释里标了对应文件)。
"""
from __future__ import annotations

import numpy as np
import pandas as pd


def momentum(prices: pd.Series, window: int = 20) -> pd.Series:
    """滚动窗口首尾涨幅:(P_t - P_{t-window}) / P_{t-window}。

    对齐 C#: MarketSignalTracker.Momentum
      (src/IamZhuli.Factors/MarketSignalTracker.cs)
    C# 里 window 默认 30 tick;这里给日线默认 20 个交易日(约一个月),可调。

    前 window-1 个值为 NaN(样本不足)。
    """
    return prices.pct_change(periods=window)


def vwap_deviation(daily: pd.DataFrame, window: int = 20) -> pd.Series:
    """滚动 VWAP 偏离度:(close - VWAP) / VWAP。

    对齐 C#: VwapFactor.Deviation
      (src/IamZhuli.Factors/VwapFactor.cs)

    日线里没有逐笔成交,tushare 的 amount(成交额,千元)与 vol(成交量,手)
    已是聚合值。换算成每股成交均价(VWAP):
      tushare: amount 单位"千元",vol 单位"手"(1 手 = 100 股)
      → 每股成交均价 = amount*1000(元) / (vol*100)(股) = amount / vol * 10  (元/股)
    偏离度 = (close - rolling_vwap) / rolling_vwap。
    """
    # 防除零:vol 为 0 的日子(停牌等)置 NaN
    vwap_intraday = (daily["amount"] / daily["vol"]).replace([np.inf, -np.inf], np.nan) * 10.0
    rolling_vwap = vwap_intraday.rolling(window, min_periods=window // 2).mean()
    return (daily["close"] - rolling_vwap) / rolling_vwap


def obi_from_moneyflow(mf: pd.DataFrame) -> pd.Series:
    """由资金流向构造"主力资金"买卖失衡因子。

    对齐 C#: OrderBookImbalanceFactor.Compute
      (src/IamZhuli.Factors/OrderBookImbalanceFactor.cs)

    语义差异(重要):
      - C# 版基于"盘口挂单"(限价单深度),衡量被动挂单压力。
      - 这里基于"主动成交",衡量主力资金方向。
      两者方向一致(主力净买→正值),但口径不同,不可直接对比绝对值。

    关键修正:不能把 sm/md/lg/elg 四档买卖金额全加起来——
    日级别每一笔成交必然一买一卖配对,四档买卖总额严格相等(实测近乎恒为 0),
    没有信号。有意义的口径是只看"大资金"(大单 lg + 特大单 elg)的净买卖:
      OBI = (大单买 + 特大单买 - 大单卖 - 特大单卖)
            / (大单买 + 特大单买 + 大单卖 + 特大单卖) ∈ [-1, 1]
    正值=大资金净流入(主力买),负值=大资金净流出(主力卖)。
    """
    # 只看大单(lg)+ 特大单(elg)的 amount,这才是"主力资金"
    buy_cols = [c for c in ["buy_lg_amount", "buy_elg_amount"] if c in mf.columns]
    sell_cols = [c for c in ["sell_lg_amount", "sell_elg_amount"] if c in mf.columns]
    if not buy_cols or not sell_cols:
        return pd.Series(np.nan, index=mf.index, name="obi")

    big_buy = mf[buy_cols].sum(axis=1)
    big_sell = mf[sell_cols].sum(axis=1)
    denom = big_buy + big_sell
    obi = (big_buy - big_sell) / denom.replace(0, np.nan)
    obi.name = "obi"
    return obi
