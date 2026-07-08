"""多因子合成(IC加权)+ 策略回测主入口。

流程:
  1. 读 multiyear_panel_neut.parquet(含三因子中性化残差)
  2. 滚动 IC 加权合成 → composite 因子
  3. 跑多空 + 纯多头回测(含 0.15%/边成本)
  4. 对比单因子 momentum 作基准
  5. 输出指标表 + 净值曲线 + 权重图 + 分年度收益

诚实声明:
  - 等权持仓(无组合优化)
  - 无风控(行业/个股暴露限制)
  - 调仓周期固定 20 日,无择时
  - 成本为固定费率近似(未含冲击成本,大资金会更低)
  - 样本内 IC 加权有轻微前视(滚动窗口缓解但未完全消除)

用法:
  python3 quant/run_backtest.py
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config
import factor_combiner as fc
import backtest as bt
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "multiyear_panel_neut.parquet"
NEUT_COLS = ["momentum_20d_neut", "vwap_dev_20d_neut", "obi_moneyflow_neut"]
SINGLE_COMPARE = "momentum_20d_neut"   # 单因子对照(最强的那个)
TOP_N = 50
PERIOD = 20
COST = 0.0015


def main() -> None:
    print("=" * 72)
    print("多因子 IC 加权合成 + 策略回测(2019-2023, 沪深300)")
    print("=" * 72)

    if not PANEL_PATH.exists():
        print(f"[error] 未找到 {PANEL_PATH},请先跑 run_neutralize.py")
        return
    panel = pd.read_parquet(PANEL_PATH)
    print(f"读入面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只股票")

    # 1. IC 加权合成
    print("\n[1/4] 计算滚动 IC 权重(回看60日,避免前视)...")
    weights = fc.rolling_ic_weights(panel, NEUT_COLS, fwd_col="fwd_ret_1", window=60)
    panel["composite"] = fc.combine_factors(panel, NEUT_COLS, weights)
    print(f"  合成因子有效率: {panel['composite'].notna().mean():.1%}")
    print(f"  权重均值(近一年):")
    print(weights.tail(252).mean().to_string(float_format=lambda x: f"  {x:+.3f}"))

    # 合成因子 IC 验证
    ic_comp = ic_analysis.rank_ic_series(panel, "composite", "fwd_ret_20")
    s_comp = ic_analysis.ic_summary(ic_comp)
    ic_single = ic_analysis.rank_ic_series(panel, SINGLE_COMPARE, "fwd_ret_20")
    s_single = ic_analysis.ic_summary(ic_single)
    print(f"\n  合成因子 IC={s_comp['mean']:+.4f}  IR={s_comp['ir']:+.3f}")
    print(f"  单因子   IC={s_single['mean']:+.4f}  IR={s_single['ir']:+.3f}  ({SINGLE_COMPARE})")

    # 2. 多空回测
    print("\n[2/4] 多空对冲回测(自动按 IC 符号决定多空端)...")
    ls_comp = bt.run_long_short(panel, "composite", TOP_N, PERIOD, COST)
    ls_single = bt.run_long_short(panel, SINGLE_COMPARE, TOP_N, PERIOD, COST)
    print(f"  合成因子方向: {ls_comp['direction']}")
    print(f"  单因子方向: {ls_single['direction']}")
    m = ls_comp["metrics_net"]
    ms = ls_single["metrics_net"]
    print(f"  合成多空(扣成本): 年化{m['ann_ret']:+.2%}  夏普{m['sharpe']:+.2f}  "
          f"回撤{m['max_dd']:+.2%}  胜率{m['win_rate']:.1%}")
    print(f"  单因子多空(扣成本): 年化{ms['ann_ret']:+.2%}  夏普{ms['sharpe']:+.2f}  "
          f"回撤{ms['max_dd']:+.2%}  胜率{ms['win_rate']:.1%}")

    # 3. 纯多头回测
    print("\n[3/4] 纯多头回测(自动按 IC 符号选股,对比全样本基准)...")
    lo_comp = bt.run_long_only(panel, "composite", TOP_N, PERIOD, COST)
    lo_single = bt.run_long_only(panel, SINGLE_COMPARE, TOP_N, PERIOD, COST)
    print(f"  合成因子选股: {lo_comp['direction']}")
    print(f"  单因子选股: {lo_single['direction']}")
    me = lo_comp["metrics_excess_net"]
    mb = lo_comp["metrics_bench"]
    mes = lo_single["metrics_excess_net"]
    print(f"  基准(全样本等权): 年化{mb['ann_ret']:+.2%}  夏普{mb['sharpe']:+.2f}")
    print(f"  合成超额(扣成本): 年化{me['ann_ret']:+.2%}  夏普{me['sharpe']:+.2f}  "
          f"回撤{me['max_dd']:+.2%}")
    print(f"  单因子超额(扣成本): 年化{mes['ann_ret']:+.2%}  夏普{mes['sharpe']:+.2f}  "
          f"回撤{mes['max_dd']:+.2%}")

    # 4. 输出
    print("\n[4/4] 输出报表与图表...")
    _save_report(ls_comp, ls_single, lo_comp, lo_single, weights)
    _plot_nav(ls_comp, lo_comp, ls_single, lo_single)
    _plot_weights(weights)
    _plot_yearly_returns(ls_comp, lo_comp)

    print(f"\n所有产物已保存到 {OUTPUT_DIR}/")


def _save_report(ls_comp, ls_single, lo_comp, lo_single, weights) -> None:
    """指标汇总表。"""
    rows = []
    for name, res in [("合成_多空", ls_comp["metrics_net"]),
                      ("单因子_多空", ls_single["metrics_net"]),
                      ("合成_超额", lo_comp["metrics_excess_net"]),
                      ("单因子_超额", lo_single["metrics_excess_net"]),
                      ("基准_多头", lo_comp["metrics_bench"])]:
        rows.append({"策略": name, **res})
    report = pd.DataFrame(rows).set_index("策略")[
        ["ann_ret", "ann_vol", "sharpe", "max_dd", "win_rate", "cum_ret", "n"]]
    report.columns = ["年化收益", "年化波动", "夏普", "最大回撤", "胜率", "累计收益", "调仓次数"]
    report.to_csv(OUTPUT_DIR / "backtest_report.csv")

    # 多空/超额净值
    ls_comp["nav_net"].rename("合成多空").to_frame().join(
        ls_single["nav_net"].rename("单因子多空")).to_csv(
        OUTPUT_DIR / "nav_long_short.csv")
    lo_comp["nav_excess"].rename("合成超额").to_frame().join(
        lo_single["nav_excess"].rename("单因子超额")).join(
        lo_comp["nav_long"].rename("多头净值")).join(
        lo_comp["nav_bench"].rename("基准净值")).to_csv(
        OUTPUT_DIR / "nav_long_only.csv")

    weights.to_csv(OUTPUT_DIR / "ic_weights.csv")

    print("\n" + "=" * 72)
    print("策略指标汇总(扣 0.15%/边 成本)")
    print("=" * 72)
    print(report.to_string(float_format=lambda x: f"{x:+.4f}" if abs(x) < 5 else f"{x:.0f}"))


def _plot_nav(ls_comp, lo_comp, ls_single, lo_single) -> None:
    """净值曲线:多空 + 纯多头超额。"""
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(12, 8))

    # 多空净值
    ls_comp["nav_net"].plot(ax=ax1, label="合成因子", linewidth=1.5)
    ls_single["nav_net"].plot(ax=ax1, label=f"单因子({SINGLE_COMPARE})", linewidth=1.5, alpha=0.7)
    ax1.axhline(1, color="black", linewidth=0.5)
    ax1.set_title("多空对冲组合净值(扣成本)")
    ax1.set_ylabel("累计净值")
    ax1.legend()
    ax1.grid(True, alpha=0.3)

    # 纯多头:超额 + 基准
    lo_comp["nav_excess"].plot(ax=ax2, label="合成因子超额", linewidth=1.5)
    lo_single["nav_excess"].plot(ax=ax2, label=f"单因子超额", linewidth=1.5, alpha=0.7)
    lo_comp["nav_bench"].plot(ax=ax2, label="基准(全样本等权)", linewidth=1.5, alpha=0.7, color="gray")
    ax2.axhline(1, color="black", linewidth=0.5)
    ax2.set_title("纯多头组合:超额净值 vs 基准净值")
    ax2.set_ylabel("累计净值")
    ax2.set_xlabel("调仓日")
    ax2.legend()
    ax2.grid(True, alpha=0.3)

    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "backtest_nav.png", dpi=120)
    plt.close()


def _plot_weights(weights) -> None:
    """三因子滚动 IC 权重变化。"""
    fig, ax = plt.subplots(figsize=(12, 4))
    for col in weights.columns:
        ax.plot(weights.index, weights[col], label=col, linewidth=1)
    ax.axhline(0, color="black", linewidth=0.5)
    ax.set_title("三因子滚动 IC 权重(60日回看,绝对值归一)")
    ax.set_ylabel("权重")
    ax.set_xlabel("日期")
    ax.legend()
    ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "ic_weights.png", dpi=120)
    plt.close()


def _plot_yearly_returns(ls_comp, lo_comp) -> None:
    """分年度收益:多空 + 超额,看哪年赚/亏。"""
    ls_log = ls_comp["rebalance_log"]
    lo_log = lo_comp["rebalance_log"]

    def yearly(series, idx):
        df = pd.DataFrame({"date": idx, "ret": series.values})
        df["year"] = df["date"].astype(str).str[:4]
        return df.groupby("year")["ret"].mean() * (252 // PERIOD)   # 年化

    ls_year = yearly(ls_log["net_ret"], ls_log.index)
    excess_year = yearly(lo_log["excess_net"], lo_log.index)

    fig, ax = plt.subplots(figsize=(10, 5))
    x = np.arange(len(ls_year))
    w = 0.35
    ax.bar(x - w/2, ls_year.values, w, label="多空(扣成本)")
    ax.bar(x + w/2, excess_year.values, w, label="超额(扣成本)")
    ax.axhline(0, color="black", linewidth=0.5)
    ax.set_xticks(x)
    ax.set_xticklabels(ls_year.index)
    ax.set_title("分年度年化收益(已扣成本)")
    ax.set_ylabel("年化收益")
    ax.set_xlabel("年份")
    ax.legend()
    ax.grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "backtest_yearly.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
