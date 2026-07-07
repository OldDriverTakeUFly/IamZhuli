"""沪深300 因子多年评估主入口(2019-2023)。

与 run_hs300.py 的区别:
  - 按年取历史成分股(避免幸存者偏差),取多年并集
  - 每只股票一次拉 5 年区间(2019-2023)
  - 核心输出:分年度 IC 表(看因子年际稳定性)

流程:
  1. 取 2019-2023 各年成分股 → 并集(~450只)
  2. 批量拉 daily + moneyflow(20190101-20231231,带断点续传)
  3. 构造面板,加 year 列
  4. 三因子分别算:全样本 IC + 分年度 IC + IC衰减
  5. 输出 csv + 图

诚实声明:
  - 仍为原始 IC,未做行业/市值中性化
  - 成分股按年近似(取1月快照,忽略6月那次调仓),有轻微偏差
  - 未扣交易成本

用法:
  python3 quant/run_multiyear.py
"""
from __future__ import annotations

from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import pandas as pd

from data_loader import (load_token, fetch_hs300_constituents_multiyear,
                         fetch_batch)
import factors
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
OUTPUT_DIR.mkdir(exist_ok=True)
YEARS = [2019, 2020, 2021, 2022, 2023]
START = "20190101"
END = "20231231"
PERIODS = [1, 5, 10, 20]
FACTOR_COLS = ["momentum_20d", "vwap_dev_20d", "obi_moneyflow"]


def build_panel(daily_dict: dict, mf_dict: dict | None) -> pd.DataFrame:
    """拼多年面板并算因子。trade_date 为 YYYYMMDD,year 从中提取。"""
    frames = []
    for code, daily in daily_dict.items():
        df = daily.copy()
        df["ts_code"] = code
        df["momentum_20d"] = factors.momentum(df["close"], window=20)
        df["vwap_dev_20d"] = factors.vwap_deviation(df, window=20)
        frames.append(df[["ts_code", "trade_date", "close", "vol", "amount",
                          "momentum_20d", "vwap_dev_20d"]])
    panel = pd.concat(frames, ignore_index=True)

    if mf_dict:
        mf_frames = []
        for code, mf in mf_dict.items():
            if mf is None or mf.empty:
                continue
            obi = factors.obi_from_moneyflow(mf)
            mf_frames.append(pd.DataFrame({
                "ts_code": code,
                "trade_date": mf["trade_date"].values,
                "obi_moneyflow": obi.values,
            }))
        if mf_frames:
            mf_panel = pd.concat(mf_frames, ignore_index=True)
            panel = panel.merge(mf_panel, on=["ts_code", "trade_date"], how="left")

    panel["year"] = panel["trade_date"].astype(str).str[:4].astype(int)
    return panel


def main() -> None:
    print("=" * 72)
    print(f"沪深300 因子多年评估  {YEARS[0]}-{YEARS[-1]}  (按年成分股,避免幸存者偏差)")
    print("=" * 72)

    load_token()

    # 1. 多年成分股并集
    codes = fetch_hs300_constituents_multiyear(YEARS)
    print(f"\n共 {len(codes)} 只股票(5年并集)。开始批量拉 {START}~{END} ...")

    # 2. 批量拉取(每只一次拉5年)
    daily_dict = fetch_batch(codes, START, END, kind="daily")
    mf_dict = fetch_batch(codes, START, END, kind="moneyflow")
    ok_d = len(daily_dict)
    ok_m = sum(1 for v in mf_dict.values() if v is not None and not v.empty)
    print(f"\n拉取完成: daily {ok_d}/{len(codes)}  moneyflow {ok_m}/{len(codes)}")

    # 3. 面板
    panel = build_panel(daily_dict, mf_dict)
    panel = ic_analysis.compute_forward_returns(panel, PERIODS)
    panel.to_parquet(OUTPUT_DIR / "multiyear_panel.parquet", index=False)
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只股票, "
          f"{panel['trade_date'].nunique()} 个交易日, {len(YEARS)} 年")

    # 4. 三因子评估
    all_rows = []        # 全样本汇总
    by_year_frames = []  # 分年度
    for fcol in FACTOR_COLS:
        if fcol not in panel.columns or panel[fcol].isna().all():
            print(f"\n[{fcol}] 无数据,跳过")
            continue
        print(f"\n[{fcol}]")

        # 全样本(20日)
        ic_full = ic_analysis.rank_ic_series(panel, fcol, "fwd_ret_20")
        s_full = ic_analysis.ic_summary(ic_full)
        s_full["factor"] = fcol
        all_rows.append(s_full)
        print(f"  全样本 IC={s_full['mean']:+.4f}  IR={s_full['ir']:+.3f}  "
              f"t={s_full['t']:+.2f}  p={s_full['p']:.4f}  "
              f"{'显著' if s_full['significant'] else '不显著'}")

        # 分年度(核心)
        by_year = ic_analysis.ic_summary_by_year(panel, fcol, "fwd_ret_20")
        by_year.insert(0, "factor", fcol)
        by_year_frames.append(by_year.reset_index())
        print(f"  分年度 IC:")
        for yr, row in by_year.iterrows():
            sig = "✓" if row["significant"] else " "
            print(f"    {yr}  IC={row['ic_mean']:+.4f}  t={row['t']:+.2f}  "
                  f"p={row['p']:.3f}  {sig}  ({row['n_days']:.0f}日)")

    # 5. 输出
    report = pd.DataFrame(all_rows).set_index("factor")[
        ["mean", "ir", "t", "p", "significant", "win_rate", "n"]]
    report.to_csv(OUTPUT_DIR / "ic_report_multiyear.csv")

    by_year_all = pd.concat(by_year_frames, ignore_index=True)
    by_year_all.to_csv(OUTPUT_DIR / "ic_by_year.csv", index=False)

    # 图:分年度 IC 柱状图
    _plot_ic_by_year(by_year_all)

    # 6. 终端汇总
    print("\n" + "=" * 72)
    print("全样本 IC(2019-2023,fwd_ret_20)")
    print("=" * 72)
    print(report.to_string(float_format=lambda x: f"{x:+.4f}"))
    print("\n" + "=" * 72)
    print("分年度 IC(核心:看年际稳定性)")
    print("=" * 72)
    pivot = by_year_all.pivot_table(index="factor", columns="year", values="ic_mean")
    print(pivot.to_string(float_format=lambda x: f"{x:+.4f}"))
    sig_pivot = by_year_all.pivot_table(index="factor", columns="year", values="significant")
    print("\n显著?(p<0.05):")
    print(sig_pivot.to_string())
    print(f"\n报表已保存到 {OUTPUT_DIR}/")


def _plot_ic_by_year(by_year_all: pd.DataFrame) -> None:
    """画各因子分年度 IC 均值的柱状图(正绿负红,显著标*)。"""
    fig, ax = plt.subplots(figsize=(11, 5))
    factors_list = by_year_all["factor"].unique().tolist()
    years = sorted(by_year_all["year"].unique().tolist())
    import numpy as np
    x = np.arange(len(years))
    width = 0.8 / len(factors_list)
    for i, fcol in enumerate(factors_list):
        sub = by_year_all[by_year_all["factor"] == fcol].set_index("year")
        vals = [sub.loc[y, "ic_mean"] if y in sub.index else 0 for y in years]
        sigs = [sub.loc[y, "significant"] if y in sub.index else False for y in years]
        bars = ax.bar(x + i * width - 0.4 + width / 2, vals, width, label=fcol)
        for bar, s in zip(bars, sigs):
            if s:
                ax.text(bar.get_x() + bar.get_width() / 2,
                        bar.get_height() + 0.002 * (1 if bar.get_height() >= 0 else -1),
                        "*", ha="center", fontsize=11)
    ax.axhline(0, color="black", linewidth=0.5)
    ax.set_xticks(x)
    ax.set_xticklabels(years)
    ax.set_xlabel("年份")
    ax.set_ylabel("IC 均值")
    ax.set_title("各因子分年度 Rank IC(* = p<0.05 显著)")
    ax.legend()
    ax.grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "ic_by_year.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
