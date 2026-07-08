"""多因子合成:IC 加权(滚动,避免前视)。

核心原则:调权只能用过去的数据,绝不能用未来收益泄漏到权重里。
滚动窗口回看 IC → 算权重 → 合成,每一步都严格"截至 t 时刻"。

合成因子 = Σ_k (w_k × zscore(factor_k)_i,t)
  - 各因子先横截面 z-score 标准化(消除量纲差异)
  - 权重 w_k 用过去 W 天的 IC 均值,符号保留,按绝对值归一
"""
from __future__ import annotations

import numpy as np
import pandas as pd

from ic_analysis import rank_ic_series


def zscore_cross_section(panel: pd.DataFrame, factor_col: str,
                         min_stocks: int = 10) -> pd.Series:
    """横截面 z-score 标准化:每个交易日分组, (x - mean) / std。

    样本太少或 std=0 的日子置 NaN(无法标准化)。
    返回与 panel 等长的 Series。
    """
    result = pd.Series(np.nan, index=panel.index, name=f"{factor_col}_z")
    for date, grp in panel.groupby("trade_date"):
        vals = grp[factor_col]
        if len(vals) < min_stocks:
            continue
        mu, sigma = vals.mean(), vals.std(ddof=0)
        if sigma == 0 or np.isnan(sigma):
            continue
        result.loc[grp.index] = (vals - mu) / sigma
    return result


def rolling_ic_weights(panel: pd.DataFrame, factor_cols: list[str],
                       fwd_col: str = "fwd_ret_1",
                       window: int = 60) -> pd.DataFrame:
    """滚动 IC 权重(避免前视)。

    对每个交易日 t,用 [t-window, t) 内的 IC 时序算各因子均值得权重。
    用 fwd_ret_1(次日收益)算 IC——短周期 IC 反映因子近期有效性,
    用于预测后续 20 天的表现。

    权重规则:w_k = mean_IC_k / Σ|mean_IC_j| (符号保留,绝对值归一)。
    冷启动(前 window 天或 IC 样本不足):等权(1/N)。

    Returns:
        DataFrame: index=trade_date, columns=factor_cols, 值=权重。
    """
    # 先算每个因子的全样本 IC 时序(index=trade_date)
    ic_series = {}
    for fcol in factor_cols:
        ic_series[fcol] = rank_ic_series(panel, fcol, fwd_col)
    ic_df = pd.DataFrame(ic_series).sort_index()

    # 滚动均值 → 权重
    dates = ic_df.index
    weights_rows = []
    n_factors = len(factor_cols)
    for i, date in enumerate(dates):
        if i < window:
            # 冷启动:等权
            w = {fcol: 1.0 / n_factors for fcol in factor_cols}
        else:
            window_ic = ic_df.iloc[i - window:i]   # 不含 i(避免前视)
            mean_ics = window_ic.mean()
            # 按绝对值归一,符号保留;若全为 0/NaN → 等权
            denom = mean_ics.abs().sum()
            if denom == 0 or np.isnan(denom):
                w = {fcol: 1.0 / n_factors for fcol in factor_cols}
            else:
                w = {fcol: mean_ics[fcol] / denom for fcol in factor_cols}
        w["trade_date"] = date
        weights_rows.append(w)

    return pd.DataFrame(weights_rows).set_index("trade_date")


def combine_factors(panel: pd.DataFrame, factor_cols: list[str],
                    weights_df: pd.DataFrame) -> pd.Series:
    """按 IC 权重合成因子。

    对每个调仓日:各因子 z-score → 加权求和 → 合成因子值。
    weights_df 由 rolling_ic_weights 产生(index=trade_date)。

    返回与 panel 等长的 Series,命名为 'composite'。
    权重只在调仓日更新;非调仓日沿用最近一次权重(forward-fill 语义)。
    """
    # 1. 各因子横截面 z-score
    z_cols = []
    for fcol in factor_cols:
        z_col = f"{fcol}_z"
        panel = panel.assign(**{z_col: zscore_cross_section(panel, fcol)})
        z_cols.append(z_col)

    # 2. 权重 forward-fill 到所有交易日(调仓日之间的权重沿用)
    all_dates = sorted(panel["trade_date"].unique())
    weights_aligned = weights_df.reindex(all_dates, method="ffill")

    # 3. 合成:每个交易日,Σ w_k × z_k
    composite = pd.Series(np.nan, index=panel.index, name="composite")
    for date, grp in panel.groupby("trade_date"):
        if date not in weights_aligned.index:
            continue
        w = weights_aligned.loc[date]
        # 各 z 列加权求和(忽略 NaN,某因子缺失时只用其他)
        val = pd.Series(0.0, index=grp.index)
        for fcol, zc in zip(factor_cols, z_cols):
            val = val.add(w[fcol] * grp[zc], fill_value=0.0)
        composite.loc[grp.index] = val

    return composite
