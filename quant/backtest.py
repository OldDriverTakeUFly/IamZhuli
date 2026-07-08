"""策略回测引擎:多空对冲 + 纯多头,含交易成本。

设计:
  - 调仓周期:每 period(默认20)个交易日一次,与 fwd_ret_period 对齐
  - 选股:每个调仓日,按因子值横截面排序,取 Top N / Bottom N
  - 持有:持有到下次调仓,用 fwd_ret_period 计收益
  - 成本:追踪持仓变化,算换手率,双边各扣 cost_rate

返回:调仓日收益序列(已扣成本)+ 累计净值 + 指标字典。
"""
from __future__ import annotations

import numpy as np
import pandas as pd

TRADING_DAYS = 252


def compute_metrics(returns: pd.Series, periods_per_year: int = TRADING_DAYS) -> dict:
    """从(已扣成本的)收益序列算评估指标。

    returns: 调仓期收益序列(每个调仓周期一个值,非日频)。
    periods_per_year: 一年有多少个调仓周期(252/period)。
    """
    r = returns.dropna()
    if len(r) < 2:
        return {"ann_ret": np.nan, "ann_vol": np.nan, "sharpe": np.nan,
                "max_dd": np.nan, "win_rate": np.nan, "n": len(r),
                "cum_ret": np.nan, "avg_period_ret": np.nan}
    ann_ret = r.mean() * periods_per_year
    ann_vol = r.std(ddof=1) * np.sqrt(periods_per_year)
    sharpe = ann_ret / ann_vol if ann_vol > 0 else np.nan
    nav = (1 + r).cumprod()
    max_dd = (nav / nav.cummax() - 1).min()
    return {
        "ann_ret": ann_ret, "ann_vol": ann_vol, "sharpe": sharpe,
        "max_dd": max_dd, "win_rate": (r > 0).mean(),
        "n": len(r), "cum_ret": nav.iloc[-1] - 1,
        "avg_period_ret": r.mean(),
    }


def _select_top_bottom(grp: pd.DataFrame, factor_col: str, top_n: int):
    """单个调仓日:按因子值排序,返回 (top_set, bottom_set) 的 ts_code 集合。"""
    valid = grp.dropna(subset=[factor_col])
    if len(valid) < top_n * 2:
        return set(), set()
    sorted_df = valid.sort_values(factor_col, ascending=False)
    top = set(sorted_df.head(top_n)["ts_code"])
    bottom = set(sorted_df.tail(top_n)["ts_code"])
    return top, bottom


def _turnover(new_set: set, old_set: set) -> float:
    """单边换手率 = |新-旧|∪|旧-新| 的对称差比例。

    返回单边换手(0~1);双边成本 = 单边换手 × cost_rate × 2。
    """
    if not new_set and not old_set:
        return 0.0
    union = new_set | old_set
    changed = len(new_set ^ old_set)
    return changed / len(union) if union else 0.0


def _factor_sign(panel: pd.DataFrame, factor_col: str, fwd_col: str) -> int:
    """确定因子方向:用全样本 Rank IC 的符号。

    因子方向是先验属性(不是时变择时),用全样本 IC 符号不构成前视泄漏。
    IC>0 → 正向因子(高值预测涨),做多 Top;IC<0 → 反向因子,做多 Bottom。
    """
    from ic_analysis import rank_ic_series
    ic = rank_ic_series(panel, factor_col, fwd_col)
    mean_ic = ic.mean()
    if pd.isna(mean_ic) or mean_ic == 0:
        return 1   # 默认正向
    return 1 if mean_ic > 0 else -1


def run_long_short(panel: pd.DataFrame, factor_col: str,
                   top_n: int = 50, period: int = 20,
                   cost_rate: float = 0.0015) -> dict:
    """多空对冲回测(自动按 IC 符号决定方向)。

    IC>0:做多 Top N、做空 Bottom N。
    IC<0(反转因子):做多 Bottom N、做空 Top N。
    多空收益 = 多头组 fwd_ret 均值 - 空头组 fwd_ret 均值 - 成本。
    成本 = (多头换手 + 空头换手) × cost_rate(单边)。
    """
    fwd_col = f"fwd_ret_{period}"
    if fwd_col not in panel.columns:
        raise ValueError(f"面板缺 {fwd_col} 列")

    sign = _factor_sign(panel, factor_col, fwd_col)
    direction = "正向(多Top空Bottom)" if sign > 0 else "反向(多Bottom空Top)"

    dates = sorted(panel["trade_date"].unique())
    rebalance_dates = dates[::period]

    records = []
    prev_long, prev_short = set(), set()
    for date in rebalance_dates:
        grp = panel[panel["trade_date"] == date]
        top, bottom = _select_top_bottom(grp, factor_col, top_n)
        if not top or not bottom:
            continue

        # 按 IC 符号决定多空端:正向→多Top;反向→多Bottom
        long_set, short_set = (top, bottom) if sign > 0 else (bottom, top)

        long_ret = grp[grp["ts_code"].isin(long_set)][fwd_col].mean()
        short_ret = grp[grp["ts_code"].isin(short_set)][fwd_col].mean()
        ls_ret = long_ret - short_ret

        long_turn = _turnover(long_set, prev_long)
        short_turn = _turnover(short_set, prev_short)
        cost = (long_turn + short_turn) * cost_rate

        records.append({
            "trade_date": date, "gross_ret": ls_ret,
            "cost": cost, "net_ret": ls_ret - cost,
            "long_turn": long_turn, "short_turn": short_turn,
        })
        prev_long, prev_short = long_set, short_set

    df = pd.DataFrame(records).set_index("trade_date")
    periods_per_year = TRADING_DAYS // period
    return {
        "rebalance_log": df,
        "direction": direction,
        "metrics_gross": compute_metrics(df["gross_ret"], periods_per_year),
        "metrics_net": compute_metrics(df["net_ret"], periods_per_year),
        "nav_net": (1 + df["net_ret"]).cumprod(),
    }


def run_long_only(panel: pd.DataFrame, factor_col: str,
                  top_n: int = 50, period: int = 20,
                  cost_rate: float = 0.0015,
                  benchmark_col: str | None = None) -> dict:
    """纯多头回测 + 超额收益(相对基准)。自动按 IC 符号选股。

    IC>0:选 Top N(高因子值的预测涨)。
    IC<0(反转因子):选 Bottom N(低因子值的预测涨)。
    基准:每个调仓日全样本(所有有效股票)的 fwd_ret 均值,代表市场等权。
    """
    fwd_col = f"fwd_ret_{period}"
    if fwd_col not in panel.columns:
        raise ValueError(f"面板缺 {fwd_col} 列")

    sign = _factor_sign(panel, factor_col, fwd_col)
    direction = "正向(选Top)" if sign > 0 else "反向(选Bottom)"

    dates = sorted(panel["trade_date"].unique())
    rebalance_dates = dates[::period]

    records = []
    prev_long = set()
    for date in rebalance_dates:
        grp = panel[panel["trade_date"] == date]
        top, bottom = _select_top_bottom(grp, factor_col, top_n)
        # 按 IC 符号选股:正向选 Top,反向选 Bottom
        long_set = top if sign > 0 else bottom
        if not long_set:
            continue

        long_ret = grp[grp["ts_code"].isin(long_set)][fwd_col].mean()
        bench_ret = grp[fwd_col].mean()   # 全样本等权基准

        long_turn = _turnover(long_set, prev_long)
        cost = long_turn * cost_rate

        records.append({
            "trade_date": date, "long_ret": long_ret, "bench_ret": bench_ret,
            "excess_gross": long_ret - bench_ret,
            "cost": cost,
            "excess_net": (long_ret - bench_ret) - cost,
        })
        prev_long = long_set

    df = pd.DataFrame(records).set_index("trade_date")
    periods_per_year = TRADING_DAYS // period
    return {
        "rebalance_log": df,
        "direction": direction,
        "metrics_long_gross": compute_metrics(df["long_ret"], periods_per_year),
        "metrics_bench": compute_metrics(df["bench_ret"], periods_per_year),
        "metrics_excess_net": compute_metrics(df["excess_net"], periods_per_year),
        "nav_long": (1 + df["long_ret"]).cumprod(),
        "nav_bench": (1 + df["bench_ret"]).cumprod(),
        "nav_excess": (1 + df["excess_net"]).cumprod(),
    }
