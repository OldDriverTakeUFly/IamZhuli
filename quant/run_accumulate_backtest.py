"""吸筹信号因子回测 + 与 momentum 反转的正交性分析。

吸筹因子(连续值,从 Step 2 的三条件合成):
  z(accumulate) = z(conc90_chg) - z(winner) - z(|costdev|)
  即:筹码换手越多、获利盘越低、价格越平稳 → 吸筹强度越高
  (符号:conc90_chg 高=加分;winner 高=减分;|costdev| 高=减分)

回测:中性化后,Top50 多头,扣 0.15%/边成本,与 momentum 反转对比。
正交性:吸筹因子 vs momentum 因子的横截面秩相关(IC between factors),
        选股重叠度 — 判断组合是否有分散价值。
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config
import ic_analysis
import backtest as bt
import factors
from data_loader import fetch_industry_map, fetch_daily_basic_batch

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "chip_panel.parquet"
START, END = "20190101", "20231231"
TOP_N, PERIOD, COST = 50, 20, 0.0015


def build_accumulate_factor(panel: pd.DataFrame) -> pd.Series:
    """合成连续的吸筹强度因子(横截面 z-score 加权)。

    每个交易日:z(conc90_chg) - z(winner) - z(|costdev|)
    高值 = 筹码换手多 + 获利盘低 + 价格平稳 = 强吸筹信号。
    """
    df = panel.copy()
    df["abs_costdev"] = df["costdev"].abs()

    def zscore_cross(grp, col, min_n=10):
        v = grp[col]
        if len(v) < min_n:
            return pd.Series(0.0, index=grp.index)
        mu, sigma = v.mean(), v.std(ddof=0)
        return (v - mu) / sigma if sigma > 0 else pd.Series(0.0, index=grp.index)

    parts = []
    for date, grp in df.groupby("trade_date"):
        z_chg = zscore_cross(grp, "conc90_chg")
        z_win = zscore_cross(grp, "winner")
        z_dev = zscore_cross(grp, "abs_costdev")
        score = z_chg - z_win - z_dev   # 吸筹强度
        parts.append(pd.Series(score.values, index=grp.index))
    return pd.concat(parts).sort_index()


def add_momentum(panel: pd.DataFrame) -> pd.DataFrame:
    """加 momentum_20d 因子(20日涨跌幅),用于正交性对比。"""
    panel["momentum_20d"] = panel.groupby("ts_code")["close"].transform(
        lambda c: c.pct_change(20))
    return panel


def main() -> None:
    print("=" * 72)
    print("吸筹信号因子回测 + 与 momentum 反转正交性分析")
    print("=" * 72)

    panel = pd.read_parquet(PANEL_PATH)
    panel = add_momentum(panel)
    panel["accumulate"] = build_accumulate_factor(panel)
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只\n")

    # 中性化(剔除行业+市值)
    industry_map = fetch_industry_map()
    dbasic = fetch_daily_basic_batch(panel["ts_code"].unique().tolist(), START, END)
    panel = panel.merge(dbasic, on=["ts_code", "trade_date"], how="left")
    panel["accumulate_neut"] = ic_analysis.neutralize(panel, "accumulate", industry_map)
    panel["momentum_neut"] = ic_analysis.neutralize(panel, "momentum_20d", industry_map)
    print(f"吸筹因子有效率: {panel['accumulate_neut'].notna().mean():.1%}")

    # 1. IC 对比
    print("\n" + "=" * 60)
    print("IC 对比(中性化后, fwd_ret_20)")
    print("=" * 60)
    for name, col in [("吸筹因子", "accumulate_neut"), ("momentum反转", "momentum_neut")]:
        ic = ic_analysis.rank_ic_series(panel, col, "fwd_ret_20")
        s = ic_analysis.ic_summary(ic)
        print(f"  {name:<14} IC={s['mean']:+.4f} IR={s['ir']:+.3f} "
              f"t={s['t']:+.2f} p={s['p']:.4f}")

    # 2. 正交性:两因子的横截面秩相关
    print("\n" + "=" * 60)
    print("正交性分析(两因子的横截面秩相关)")
    print("=" * 60)
    from scipy import stats as scistats
    corrs = []
    for date, grp in panel.dropna(subset=["accumulate_neut", "momentum_neut"]).groupby("trade_date"):
        if len(grp) < 20:
            continue
        rho, _ = scistats.spearmanr(grp["accumulate_neut"], grp["momentum_neut"])
        if not np.isnan(rho):
            corrs.append(rho)
    mean_corr = np.mean(corrs) if corrs else np.nan
    print(f"  平均秩相关: {mean_corr:+.4f}")
    if abs(mean_corr) < 0.3:
        print(f"  → |ρ|<0.3,两因子接近正交,组合有分散价值 ✓")
    elif abs(mean_corr) < 0.6:
        print(f"  → 0.3≤|ρ|<0.6,中度相关,组合价值有限")
    else:
        print(f"  → |ρ|≥0.6,高度相关,冗余")

    # 选股重叠度(Top50)
    overlaps = []
    for date, grp in panel.dropna(subset=["accumulate_neut", "momentum_neut"]).groupby("trade_date"):
        if len(grp) < TOP_N * 2:
            continue
        # 吸筹因子:值大=吸筹强(momentum 方向:值小=跌多=反转买入)
        top_acc = set(grp.nlargest(TOP_N, "accumulate_neut")["ts_code"])
        top_mom = set(grp.nsmallest(TOP_N, "momentum_neut")["ts_code"])
        overlaps.append(len(top_acc & top_mom) / TOP_N)
    mean_overlap = np.mean(overlaps) if overlaps else np.nan
    print(f"  Top{TOP_N} 选股重叠率: {mean_overlap:.1%} (随机基准 {1/TOP_N:.1%} → "
          f"{TOP_N}/{panel['ts_code'].nunique()}:{TOP_N/panel['ts_code'].nunique():.1%})")

    # 3. 回测对比
    print("\n" + "=" * 60)
    print(f"回测对比(中性化, Top{TOP_N}, {PERIOD}日调仓, 扣{COST:.2%}/边)")
    print("=" * 60)
    res_acc = bt.run_long_only(panel, "accumulate_neut", TOP_N, PERIOD, COST)
    res_mom = bt.run_long_only(panel, "momentum_neut", TOP_N, PERIOD, COST)
    me = res_acc["metrics_excess_net"]
    mm = res_mom["metrics_excess_net"]
    mb = res_acc["metrics_bench"]
    print(f"  {'策略':<14} {'年化超额':>10} {'夏普':>8} {'回撤':>10} {'胜率':>8}")
    print(f"  {'吸筹因子':<14} {me['ann_ret']:>+10.2%} {me['sharpe']:>+8.2f} "
          f"{me['max_dd']:>+10.2%} {me['win_rate']:>8.1%}")
    print(f"  {'momentum反转':<14} {mm['ann_ret']:>+10.2%} {mm['sharpe']:>+8.2f} "
          f"{mm['max_dd']:>+10.2%} {mm['win_rate']:>8.1%}")
    print(f"  {'基准(等权)':<14} {mb['ann_ret']:>+10.2%} {mb['sharpe']:>+8.2f} "
          f"{mb['max_dd']:>+10.2%} {mb['win_rate']:>8.1%}")

    # 4. 分年度
    print("\n" + "=" * 60)
    print("分年度超额收益(扣成本)")
    print("=" * 60)
    for name, res in [("吸筹", res_acc), ("momentum", res_mom)]:
        log = res["rebalance_log"]
        df_y = pd.DataFrame({"date": log.index, "ret": log["excess_net"].values})
        df_y["year"] = df_y["date"].astype(str).str[:4]
        yr = df_y.groupby("year")["ret"].mean() * 12
        print(f"  {name:<10} " + "  ".join(f"{y}:{v:+.1%}" for y, v in yr.items()))

    # 输出 + 图
    res_acc["nav_excess"].rename("吸筹超额").to_frame().join(
        res_mom["nav_excess"].rename("momentum超额")).join(
        res_acc["nav_bench"].rename("基准")).to_csv(
        OUTPUT_DIR / "accumulate_vs_momentum_nav.csv")
    _plot(res_acc, res_mom, mean_corr, mean_overlap)
    print(f"\n产物: accumulate_vs_momentum_nav.csv + accumulate_backtest.png")


def _plot(res_acc, res_mom, corr, overlap) -> None:
    fig, axes = plt.subplots(2, 1, figsize=(12, 8))
    res_acc["nav_excess"].plot(ax=axes[0], label="吸筹因子超额", linewidth=1.5)
    res_mom["nav_excess"].plot(ax=axes[0], label="momentum反转超额", linewidth=1.5, alpha=0.7)
    res_acc["nav_bench"].plot(ax=axes[0], label="基准", color="gray", linewidth=1.5, alpha=0.6)
    axes[0].axhline(1, color="black", linewidth=0.5)
    axes[0].set_title(f"吸筹因子 vs momentum反转(中性化,扣成本)  "
                      f"秩相关={corr:+.3f} 重叠={overlap:.1%}")
    axes[0].set_ylabel("累计净值"); axes[0].legend(); axes[0].grid(True, alpha=0.3)

    # 分年度对比
    for name, res, color in [("吸筹", res_acc, "steelblue"),
                             ("momentum", res_mom, "orange")]:
        log = res["rebalance_log"]
        df_y = pd.DataFrame({"date": log.index, "ret": log["excess_net"].values})
        df_y["year"] = df_y["date"].astype(str).str[:4]
        yr = df_y.groupby("year")["ret"].mean() * 12
        axes[1].bar([f"{y}\n{name}" for y in yr.index], yr.values,
                    color=color, alpha=0.7)
    axes[1].axhline(0, color="black", linewidth=0.5)
    axes[1].set_title("分年度超额收益(扣成本)")
    axes[1].set_ylabel("年化超额"); axes[1].grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "accumulate_backtest.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
