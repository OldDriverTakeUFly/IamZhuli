"""沪深300 因子有效性评估主入口。

流程:
  1. 拉沪深300成分股(~300只)
  2. 批量拉每只的 daily + moneyflow(限流 + 断点续传,预计 5-10 分钟)
  3. 拼成面板:date × ts_code × {三因子值, 未来收益}
  4. 对每个因子算:Rank IC 时序 / 分层回测 / IC衰减 / IR+t检验
  5. 输出 csv 报表 + matplotlib 图表

注意(诚实声明):
  - 这是"原始因子 IC",未做行业/市值中性化(风险调整)。
    若某因子 IC 显著,可能部分来自行业/风格暴露,需进一步中性化才能确认纯因子收益。
  - 未做交易成本建模。分层回测的"多空收益"是理论值,实际扣除成本后会更低。

用法:
  python3 quant/run_hs300.py                  # 默认: 2023全年
  python3 quant/run_hs300.py 20220101 20231231
"""
from __future__ import annotations

import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")   # 无头模式,直接存图不开窗
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

from data_loader import load_token, fetch_hs300_constituents, fetch_batch
import factors
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
OUTPUT_DIR.mkdir(exist_ok=True)
PERIODS = [1, 5, 10, 20]
FACTOR_COLS = ["momentum_20d", "vwap_dev_20d", "obi_moneyflow"]


def build_panel(daily_dict: dict, mf_dict: dict | None,
                start_date: str, end_date: str) -> pd.DataFrame:
    """把多只股票的 daily/moneyflow 拼成单张面板,并计算因子值。

    Returns:
        DataFrame: ts_code, trade_date, close, amount, vol, momentum_20d,
                   vwap_dev_20d, obi_moneyflow(若有)
    """
    frames = []
    for code, daily in daily_dict.items():
        df = daily.copy()
        df["ts_code"] = code
        # 算因子
        df["momentum_20d"] = factors.momentum(df["close"], window=20)
        df["vwap_dev_20d"] = factors.vwap_deviation(df, window=20)
        frames.append(df[["ts_code", "trade_date", "close", "vol", "amount",
                          "momentum_20d", "vwap_dev_20d"]])
    panel = pd.concat(frames, ignore_index=True)

    # 合并 OBI(若有 moneyflow)
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

    return panel


def main(start_date: str = "20230101", end_date: str = "20231231") -> None:
    print("=" * 72)
    print(f"沪深300 因子有效性评估  区间={start_date}~{end_date}")
    print("=" * 72)

    load_token()

    # 1. 成分股
    codes = fetch_hs300_constituents()
    print(f"\n共 {len(codes)} 只成分股。开始批量拉数据(预计 5-10 分钟,带断点续传)...")

    # 2. 批量拉取
    daily_dict = fetch_batch(codes, start_date, end_date, kind="daily")
    mf_dict = fetch_batch(codes, start_date, end_date, kind="moneyflow")
    ok_daily = len(daily_dict)
    ok_mf = sum(1 for v in mf_dict.values() if v is not None and not v.empty)
    print(f"\n拉取完成: daily {ok_daily}/{len(codes)}  moneyflow {ok_mf}/{len(codes)}")

    # 3. 构造面板
    panel = build_panel(daily_dict, mf_dict, start_date, end_date)
    panel = ic_analysis.compute_forward_returns(panel, PERIODS)
    panel.to_parquet(OUTPUT_DIR / "hs300_factors_panel.parquet", index=False)
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只股票, "
          f"{panel['trade_date'].nunique()} 个交易日")

    # 4. 对每个因子做完整评估
    report_rows = []
    decay_frames = []
    layered_frames = {}
    ic_series_dict = {}
    for fcol in FACTOR_COLS:
        if fcol not in panel.columns or panel[fcol].isna().all():
            print(f"\n[{fcol}] 无数据,跳过")
            continue
        print(f"\n[{fcol}] 评估中...")

        # 4a. Rank IC(主周期=20日) + 汇总
        ic20 = ic_analysis.rank_ic_series(panel, fcol, "fwd_ret_20")
        ic_series_dict[fcol] = ic20
        summ = ic_analysis.ic_summary(ic20)
        summ["factor"] = fcol
        report_rows.append(summ)
        print(f"  IC均值={summ['mean']:+.4f}  IR={summ['ir']:+.3f}  "
              f"t={summ['t']:+.2f}  p={summ['p']:.4f}  "
              f"{'显著' if summ['significant'] else '不显著'}  胜率={summ['win_rate']:.2%}")

        # 4b. IC 衰减
        decay = ic_analysis.ic_decay(panel, fcol, PERIODS)
        decay.insert(0, "factor", fcol)
        decay_frames.append(decay.reset_index())

        # 4c. 分层回测(主周期=20日)
        layered = ic_analysis.layered_returns(panel, fcol, "fwd_ret_20", n_layers=5)
        layered_frames[fcol] = layered
        if not layered.empty:
            long_short = (layered.iloc[:, -1] - layered.iloc[:, 0]).mean()
            print(f"  分层多空日均收益(第5档-第1档)={long_short:+.5f}")

    # 5. 输出报表
    report = pd.DataFrame(report_rows).set_index("factor")[
        ["mean", "std", "ir", "t", "p", "significant", "win_rate", "n"]]
    report.to_csv(OUTPUT_DIR / "ic_report.csv")

    decay_all = pd.concat(decay_frames, ignore_index=True)
    decay_all.to_csv(OUTPUT_DIR / "ic_decay.csv", index=False)

    # 分层净值
    for fcol, layered in layered_frames.items():
        if layered.empty:
            continue
        nav = ic_analysis.cumulative_layer_nav(layered)
        nav.to_csv(OUTPUT_DIR / f"layered_nav_{fcol}.csv")

    # 6. 画图
    _plot_ic_series(ic_series_dict)
    _plot_ic_decay(decay_all)
    for fcol, layered in layered_frames.items():
        if not layered.empty:
            _plot_layered_nav(fcol, layered)

    # 7. 终端汇总
    print("\n" + "=" * 72)
    print("评估汇总(IC 基于 fwd_ret_20)")
    print("=" * 72)
    print(report.to_string(float_format=lambda x: f"{x:+.4f}"))
    print(f"\n报表已保存到 {OUTPUT_DIR}/")
    print("  ic_report.csv     — 各因子 IC/IR/t/p 汇总")
    print("  ic_decay.csv      — 不同持仓周期 IC 衰减")
    print("  layered_nav_*.csv — 分层净值")
    print("  *.png             — 可视化图表")


def _plot_ic_series(ic_series_dict: dict) -> None:
    """画三因子的 IC 时序图(带 12 日滚动均值)。"""
    if not ic_series_dict:
        return
    fig, axes = plt.subplots(len(ic_series_dict), 1, figsize=(12, 4 * len(ic_series_dict)),
                             sharex=True)
    if len(ic_series_dict) == 1:
        axes = [axes]
    for ax, (fcol, ic) in zip(axes, ic_series_dict.items()):
        ax.bar(range(len(ic)), ic.values, color=["g" if v > 0 else "r" for v in ic.values],
               width=1.0, alpha=0.5)
        if len(ic) > 12:
            ax.plot(ic.rolling(12, min_periods=3).mean().values, color="blue", linewidth=1.5,
                    label="MA12")
        ax.axhline(0, color="black", linewidth=0.5)
        ax.set_title(f"{fcol}  Rank IC(均值={ic.mean():+.4f})")
        ax.legend(loc="upper right")
        ax.set_ylabel("IC")
    axes[-1].set_xlabel("时点序号(每个交易日)")
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "ic_series.png", dpi=120)
    plt.close()


def _plot_ic_decay(decay_all: pd.DataFrame) -> None:
    """画各因子 IC 随持仓周期的衰减曲线。"""
    fig, ax = plt.subplots(figsize=(8, 5))
    for fcol in decay_all["factor"].unique():
        sub = decay_all[decay_all["factor"] == fcol].sort_values("period")
        ax.plot(sub["period"], sub["ic_mean"], marker="o", label=fcol)
    ax.axhline(0, color="black", linewidth=0.5)
    ax.set_xlabel("持仓周期(交易日)")
    ax.set_ylabel("IC 均值")
    ax.set_title("IC 衰减曲线(预测力随持仓周期变化)")
    ax.legend()
    ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "ic_decay.png", dpi=120)
    plt.close()


def _plot_layered_nav(fcol: str, layered: pd.DataFrame) -> None:
    """画分层累积净值曲线(看各档是否单调分离)。"""
    nav = (1 + layered).cumprod()
    fig, ax = plt.subplots(figsize=(11, 5))
    for col in nav.columns:
        ax.plot(nav.index, nav[col], label=col, linewidth=1.2)
    ax.set_title(f"{fcol}  分层净值(5档,第1档=因子值最小)")
    ax.set_xlabel("日期")
    ax.set_ylabel("累积净值")
    ax.legend(loc="upper left", ncol=5)
    ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / f"layered_nav_{fcol}.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    args = sys.argv[1:]
    start = args[0] if len(args) > 0 else "20230101"
    end = args[1] if len(args) > 1 else "20231231"
    main(start, end)
