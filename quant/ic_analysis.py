"""因子有效性评估:Rank IC、分层回测、IR/显著性、IC 衰减。

纯计算函数,不碰 IO/网络。输入输出都是 pandas DataFrame/Series。
所有计算基于"面板数据":长表(date, ts_code, 因子值, 未来收益)。

指标说明:
- Rank IC(信息系数):每个时点,按因子值与未来收益分别排序,算 Spearman 秩相关。
  IC 时序的均值>0.03 视为有弱预测力,>0.05 中等,>0.1 强(业界经验)。
- IR(信息比率):IC 均值/标准差,衡量预测力的稳定性。>0.5 算不错。
- t 统计/p 值:检验 IC 均值是否显著区别于 0(p<0.05 显著)。
- 分层回测:按时因子值分 N 档,看各档未来收益是否单调,第1档vs第N档为多空收益。
- IC 衰减:不同持仓周期(1/5/10/20日)的 IC,预测力随时间衰减的快慢。
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from scipy import stats


def compute_forward_returns(panel: pd.DataFrame, periods: list[int]) -> pd.DataFrame:
    """给面板数据加上"未来 N 日收益"列。

    panel 需含:ts_code, trade_date, close(按日期升序)。
    对每只股票分组,用 close.shift(-N)/close - 1 算未来收益。
    """
    df = panel.sort_values(["ts_code", "trade_date"]).reset_index(drop=True)
    for n in periods:
        col = f"fwd_ret_{n}"
        df[col] = df.groupby("ts_code")["close"].transform(
            lambda c: c.shift(-n) / c - 1.0)
    return df


def rank_ic_series(panel: pd.DataFrame, factor_col: str, fwd_col: str,
                   min_stocks: int = 20) -> pd.Series:
    """每个时点的横截面 Rank IC(Spearman 秩相关)。

    对每个 trade_date,取该日所有股票的 (因子值, 未来收益),
    计算 scipy.stats.spearmanr。样本太少(<min_stocks)的时点跳过。

    Returns:
        以 trade_date 为 index 的 IC 时序 Series。
    """
    ic_list = []
    dates = []
    for date, grp in panel.dropna(subset=[factor_col, fwd_col]).groupby("trade_date"):
        if len(grp) < min_stocks:
            continue
        rho, _ = stats.spearmanr(grp[factor_col], grp[fwd_col])
        if not np.isnan(rho):
            ic_list.append(rho)
            dates.append(date)
    return pd.Series(ic_list, index=pd.Index(dates, name="trade_date"),
                     name=f"ic_{factor_col}_{fwd_col}")


def ic_summary(ic: pd.Series) -> dict:
    """IC 时序的汇总统计:均值、标准差、IR、t、p、显著?、胜率。

    IR = IC均值 / IC标准差(信息比率)。
    t/p 用单样本 t 检验(H0: IC均值=0)。
    胜率 = IC>0 的比例(衡量方向一致性)。
    """
    ic = ic.dropna()
    if len(ic) < 2:
        return {"mean": np.nan, "std": np.nan, "ir": np.nan,
                "t": np.nan, "p": np.nan, "significant": False,
                "win_rate": np.nan, "n": len(ic)}
    mean, std = ic.mean(), ic.std(ddof=1)
    ir = mean / std if std > 0 else np.nan
    t_stat, p_val = stats.ttest_1samp(ic, 0.0)
    return {
        "mean": mean, "std": std, "ir": ir,
        "t": t_stat, "p": p_val,
        "significant": bool(p_val < 0.05),
        "win_rate": (ic > 0).mean(),
        "n": len(ic),
    }


def layered_returns(panel: pd.DataFrame, factor_col: str, fwd_col: str,
                    n_layers: int = 5, min_stocks: int = 20) -> pd.DataFrame:
    """分层回测:每个时点按因子值分 n_layers 档,算各档平均未来收益。

    Returns:
        DataFrame: index=trade_date, columns=layer_1..layer_N, 值=该档平均收益。
        layer_1 = 因子值最小的一档,layer_N = 最大的一档。
    """
    rows = []
    for date, grp in panel.dropna(subset=[factor_col, fwd_col]).groupby("trade_date"):
        if len(grp) < min_stocks:
            continue
        # pd.qcut 分档;用 labels=False 拿到 0..N-1;用 duplicates='drop' 防边界重复
        try:
            grp = grp.assign(
                _layer=pd.qcut(grp[factor_col], n_layers, labels=False, duplicates="drop"))
        except ValueError:
            continue
        layer_mean = grp.groupby("_layer")[fwd_col].mean()
        row = {f"layer_{int(k)+1}": v for k, v in layer_mean.items()}
        row["trade_date"] = date
        rows.append(row)
    if not rows:
        return pd.DataFrame()
    out = pd.DataFrame(rows).set_index("trade_date").sort_index()
    return out


def ic_decay(panel: pd.DataFrame, factor_col: str,
             periods: list[int]) -> pd.DataFrame:
    """IC 衰减:不同持仓周期的 Rank IC 汇总,看预测力随时间衰减。

    Returns:
        DataFrame: 每行一个 period,列含 ic_mean/ir/t/p。
    """
    rows = []
    for n in periods:
        fwd_col = f"fwd_ret_{n}"
        if fwd_col not in panel.columns:
            continue
        ic = rank_ic_series(panel, factor_col, fwd_col)
        s = ic_summary(ic)
        s["period"] = n
        rows.append(s)
    return pd.DataFrame(rows).set_index("period")[
        ["mean", "ir", "t", "p", "significant", "n"]].rename(columns={"mean": "ic_mean"})


def cumulative_layer_nav(layered: pd.DataFrame) -> pd.DataFrame:
    """把分层日收益转为累积净值(从1开始),便于画分层净值曲线。

    假设每日收益为简单收益率:(1+r1)(1+r2)... 累乘。
    """
    return (1 + layered).cumprod()


def ic_summary_by_year(panel: pd.DataFrame, factor_col: str, fwd_col: str,
                       min_stocks: int = 20) -> pd.DataFrame:
    """按年分组的 IC 汇总:多年回测的核心输出。

    panel 需含 trade_date(YYYYMMDD)列;本函数从中提取 year 并分组,
    对每年分别算 rank_ic_series → ic_summary。

    Returns:
        DataFrame: index=year, columns=[ic_mean, ir, t, p, significant, win_rate, n_days]
        n_days = 该年有多少个交易日的有效 IC(衡量样本量)。
    """
    df = panel.dropna(subset=[factor_col, fwd_col]).copy()
    if df.empty:
        return pd.DataFrame()
    # trade_date 是 "YYYYMMDD" 字符串,取前4位做 year
    df["_year"] = df["trade_date"].astype(str).str[:4].astype(int)

    rows = []
    for year, grp in df.groupby("_year"):
        ic = rank_ic_series(grp, factor_col, fwd_col, min_stocks=min_stocks)
        s = ic_summary(ic)
        s["year"] = year
        rows.append(s)
    if not rows:
        return pd.DataFrame()
    return pd.DataFrame(rows).set_index("year")[
        ["mean", "ir", "t", "p", "significant", "win_rate", "n"]
    ].rename(columns={"mean": "ic_mean", "n": "n_days"})
