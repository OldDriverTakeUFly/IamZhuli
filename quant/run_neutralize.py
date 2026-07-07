"""因子风险中性化:剔除行业+市值影响,对比纯因子 IC。

回答核心问题:
  momentum_20d 的反转效应(IC=-0.030),是真正的选股能力,
  还是仅仅因为它暴露在特定行业/市值上?

流程:
  1. 读已有多面板(multiyear_panel.parquet,不重算因子)
  2. 拉行业映射(stock_basic)+ 流通市值(daily_basic)
  3. 对三因子分别做横截面回归中性化 → 得到 *_neut 列
  4. 对比中性化前后的 IC(全样本 + 分年度)
  5. 输出 IC 衰减比例 + 对比图

中性化方法:每个时点 factor ~ log(circ_mv) + 行业哑变量,取残差。
IC 衰减多 → 原始 IC 主要是风险暴露;基本不变 → 纯选股能力。

用法:
  python3 quant/run_neutralize.py
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config   # 配置中文字体 + 无头模式(副作用导入)

from data_loader import (load_token, fetch_industry_map,
                         fetch_daily_basic_batch)
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "multiyear_panel.parquet"
START, END = "20190101", "20231231"
FACTOR_COLS = ["momentum_20d", "vwap_dev_20d", "obi_moneyflow"]


def main() -> None:
    print("=" * 72)
    print("因子风险中性化(行业 + 市值回归)  对比纯因子 IC")
    print("=" * 72)

    if not PANEL_PATH.exists():
        print(f"[error] 未找到 {PANEL_PATH},请先跑 run_multiyear.py")
        return

    panel = pd.read_parquet(PANEL_PATH)
    print(f"读入面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只股票")

    load_token()

    # 1. 拉行业 + 市值
    industry_map = fetch_industry_map()
    codes = panel["ts_code"].unique().tolist()
    print(f"\n拉取 {len(codes)} 只股票的流通市值(daily_basic)...")
    dbasic = fetch_daily_basic_batch(codes, START, END)

    # 2. 合并 circ_mv 进面板
    panel = panel.merge(dbasic, on=["ts_code", "trade_date"], how="left")
    cov = panel["circ_mv"].notna().mean()
    print(f"市值覆盖率: {cov:.1%}")

    # 3. 中性化三因子
    print("\n中性化中(横截面回归)...")
    for fcol in FACTOR_COLS:
        if fcol not in panel.columns:
            continue
        neut_col = f"{fcol}_neut"
        panel[neut_col] = ic_analysis.neutralize(panel, fcol, industry_map)
        valid = panel[neut_col].notna().mean()
        print(f"  {fcol} → {neut_col}  有效残差率 {valid:.1%}")

    panel.to_parquet(OUTPUT_DIR / "multiyear_panel_neut.parquet", index=False)

    # 4. 对比 IC(全样本 + 分年度)
    print("\n" + "-" * 72)
    print("中性化前后 IC 对比(fwd_ret_20)")
    print("-" * 72)

    compare_rows = []
    by_year_frames = []
    for fcol in FACTOR_COLS:
        neut_col = f"{fcol}_neut"
        if neut_col not in panel.columns or panel[neut_col].isna().all():
            continue

        # 原始 IC
        ic_orig = ic_analysis.rank_ic_series(panel, fcol, "fwd_ret_20")
        s_orig = ic_analysis.ic_summary(ic_orig)
        # 中性化后 IC
        ic_neut = ic_analysis.rank_ic_series(panel, neut_col, "fwd_ret_20")
        s_neut = ic_analysis.ic_summary(ic_neut)

        decay = (s_neut["mean"] / s_orig["mean"]) if s_orig["mean"] != 0 else np.nan
        compare_rows.append({
            "factor": fcol,
            "ic_orig": s_orig["mean"], "ir_orig": s_orig["ir"],
            "ic_neut": s_neut["mean"], "ir_neut": s_neut["ir"],
            "ic_decay_ratio": decay,
            "sig_orig": s_orig["significant"], "sig_neut": s_neut["significant"],
        })
        print(f"\n[{fcol}]")
        print(f"  原始    IC={s_orig['mean']:+.4f}  IR={s_orig['ir']:+.3f}  "
              f"{'显著' if s_orig['significant'] else '不显著'}")
        print(f"  中性化  IC={s_neut['mean']:+.4f}  IR={s_neut['ir']:+.3f}  "
              f"{'显著' if s_neut['significant'] else '不显著'}")
        print(f"  IC 衰减比 = {decay:+.2%}  "
              f"({'纯因子信号弱化' if abs(decay) < 0.7 else '因子有独立选股力'})")

        # 分年度对比
        by_orig = ic_analysis.ic_summary_by_year(panel, fcol, "fwd_ret_20")
        by_neut = ic_analysis.ic_summary_by_year(panel, neut_col, "fwd_ret_20")
        by_neut.insert(0, "factor", fcol)
        by_neut["ic_orig"] = by_orig["ic_mean"].values
        by_year_frames.append(by_neut.reset_index())

    # 5. 输出
    compare = pd.DataFrame(compare_rows).set_index("factor")
    compare.to_csv(OUTPUT_DIR / "ic_neut_compare.csv")

    by_year_all = pd.concat(by_year_frames, ignore_index=True)
    by_year_all.to_csv(OUTPUT_DIR / "ic_neut_by_year.csv", index=False)

    _plot_neut_compare(by_year_all)

    # 6. 终端汇总
    print("\n" + "=" * 72)
    print("中性化前后全样本 IC 对比")
    print("=" * 72)
    print(compare.to_string(float_format=lambda x: f"{x:+.4f}"))
    print(f"\n报表已保存到 {OUTPUT_DIR}/")


def _plot_neut_compare(by_year_all: pd.DataFrame) -> None:
    """画分年度:原始 IC vs 中性化后 IC,并列柱状图。"""
    factors_list = by_year_all["factor"].unique().tolist()
    years = sorted(by_year_all["year"].unique().tolist())
    fig, axes = plt.subplots(1, len(factors_list), figsize=(5 * len(factors_list), 5),
                             sharey=True)
    if len(factors_list) == 1:
        axes = [axes]
    x = np.arange(len(years))
    width = 0.35
    for ax, fcol in zip(axes, factors_list):
        sub = by_year_all[by_year_all["factor"] == fcol].set_index("year")
        orig = [sub.loc[y, "ic_orig"] if y in sub.index else 0 for y in years]
        neut = [sub.loc[y, "ic_mean"] if y in sub.index else 0 for y in years]
        ax.bar(x - width / 2, orig, width, label="原始", color="#888", alpha=0.7)
        ax.bar(x + width / 2, neut, width, label="中性化后", color="#2196F3", alpha=0.8)
        ax.axhline(0, color="black", linewidth=0.5)
        ax.set_xticks(x)
        ax.set_xticklabels(years)
        ax.set_title(fcol)
        ax.set_xlabel("年份")
        ax.grid(True, axis="y", alpha=0.3)
        ax.legend()
    axes[0].set_ylabel("IC 均值")
    plt.suptitle("风险中性化前后 IC 对比(行业+市值)")
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "ic_neut_compare.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
