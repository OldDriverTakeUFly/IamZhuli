"""Momentum 反转策略 Walk-Forward 样本外验证。

回答:全样本回测的夏普0.71是不是过拟合?

核心修正:之前 _factor_sign 用全样本IC定方向(轻微前视:"怎么事先知道是反转?")。
这里改为严格滚动——每个月初只用过去12个月的信息定当月方向,绝不触及当月及未来。

设计:
  对每个验证月 m(从第13个月起):
    1. 训练窗 = 过去12个月的面板
    2. 算训练窗内 momentum_20d_neut 对 fwd_ret_20 的 IC 均值 → sign_m
    3. 验证月 m:按 sign_m 选股(Top 或 Bottom),算实际收益(扣成本)
    4. 同时记录事后最优方向,算方向命中率

样本外 = 所有验证月收益串起来(每月决策只用过去数据)。

用法: python3 quant/run_walkforward.py
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config
import backtest as bt
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "multiyear_panel_neut.parquet"
FACTOR_COL = "momentum_20d_neut"
TOP_N = 50
PERIOD = 20
COST = 0.0015
TRAIN_MONTHS = 12


def _ym(trade_date: str) -> tuple[int, int]:
    """trade_date 'YYYYMMDD' → (year, month)。"""
    return int(trade_date[:4]), int(trade_date[4:6])


def walk_forward_validate(panel: pd.DataFrame, train_months: int = TRAIN_MONTHS) -> dict:
    """滚动训练窗样本外验证。

    Returns:
        含 oos_returns(月收益 Series)、direction_log(每月判定+命中)、nav 等。
    """
    panel = panel.copy()
    panel["ym"] = panel["trade_date"].astype(str).apply(_ym)
    # 月份排序:按 (year, month) 升序的唯一列表
    all_months = sorted(panel["ym"].unique())
    if len(all_months) <= train_months:
        raise ValueError(f"样本不足:需要 >{train_months} 个月,只有 {len(all_months)}")

    oos_records = []   # 每个验证月一条
    prev_long = set()

    for i in range(train_months, len(all_months)):
        val_ym = all_months[i]
        train_window = all_months[i - train_months:i]   # 过去12月(不含当月)
        train_panel = panel[panel["ym"].isin(train_window)]
        val_panel = panel[panel["ym"] == val_ym]

        if train_panel.empty or val_panel.empty:
            continue

        # 1. 训练窗算 IC → 定当月方向(-1 反转/做Bottom,+1 动量/做Top)
        train_ic = ic_analysis.rank_ic_series(train_panel, FACTOR_COL, f"fwd_ret_{PERIOD}")
        mean_ic = train_ic.mean()
        sign = -1 if mean_ic < 0 else 1   # IC<0→反转,IC>0→动量

        # 2. 样本外:按 sign 在验证月调仓(月内每个 period 日调一次,这里取该月所有调仓日平均)
        #    简化:用验证月内所有 rebalance 日的平均收益
        val_dates = sorted(val_panel["trade_date"].unique())
        month_rets = []
        for date in val_dates:
            grp = val_panel[val_panel["trade_date"] == date]
            top, bottom = bt._select_top_bottom(grp, FACTOR_COL, TOP_N)
            if not top or not bottom:
                continue
            long_set = top if sign > 0 else bottom
            short_set = bottom if sign > 0 else top
            fwd = f"fwd_ret_{PERIOD}"
            long_ret = grp[grp["ts_code"].isin(long_set)][fwd].mean()
            short_ret = grp[grp["ts_code"].isin(short_set)][fwd].mean()
            ls_ret = long_ret - short_ret
            cost = (bt._turnover(long_set, prev_long) +
                    bt._turnover(short_set, set())) * COST
            month_rets.append(ls_ret - cost)
            prev_long = long_set

            # 同时记录事后最优:如果该日 Bottom 实际强,事后方向应为-1
            # (用于算方向命中率)
        if not month_rets:
            continue
        month_net = np.mean(month_rets)

        # 事后最优方向:验证月内,反转(-1)收益 vs 动量(+1)收益谁高
        rev_ret, mom_ret = [], []
        for date in val_dates:
            grp = val_panel[val_panel["trade_date"] == date]
            top, bottom = bt._select_top_bottom(grp, FACTOR_COL, TOP_N)
            if not top or not bottom:
                continue
            fwd = f"fwd_ret_{PERIOD}"
            t_ret = grp[grp["ts_code"].isin(top)][fwd].mean()
            b_ret = grp[grp["ts_code"].isin(bottom)][fwd].mean()
            mom_ret.append(t_ret - b_ret)   # 动量方向收益
            rev_ret.append(b_ret - t_ret)   # 反转方向收益
        if mom_ret and rev_ret:
            best_sign = -1 if np.mean(rev_ret) > np.mean(mom_ret) else 1
            hit = (sign == best_sign)
        else:
            best_sign = sign
            hit = True

        oos_records.append({
            "ym": val_ym, "train_ic": mean_ic, "pred_sign": sign,
            "best_sign": best_sign, "hit": hit,
            "month_ret": month_net, "n_rebal": len(month_rets),
        })

    log = pd.DataFrame(oos_records)
    log["ym_str"] = log["ym"].apply(lambda ym: f"{ym[0]}-{ym[1]:02d}")
    log = log.set_index("ym_str")

    # 串起月收益 → 年化指标(每月一个收益,一年12期)
    oos_returns = log["month_ret"]
    metrics = bt.compute_metrics(oos_returns, periods_per_year=12)
    nav = (1 + oos_returns).cumprod()

    return {
        "log": log,
        "oos_returns": oos_returns,
        "nav": nav,
        "metrics": metrics,
        "hit_rate": log["hit"].mean(),
    }


def main() -> None:
    print("=" * 72)
    print(f"Momentum 反转策略 Walk-Forward 样本外验证")
    print(f"(滚动 {TRAIN_MONTHS} 月训练窗 → 1 月样本外,严格无前视)")
    print("=" * 72)

    panel = pd.read_parquet(PANEL_PATH)
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只\n")

    result = walk_forward_validate(panel, TRAIN_MONTHS)
    log = result["log"]
    m = result["metrics"]

    print(f"验证月数: {len(log)} (覆盖 {log.index[0]} ~ {log.index[-1]})")
    print(f"方向命中率: {result['hit_rate']:.1%}  "
          f"({'优于随机' if result['hit_rate'] > 0.55 else '接近随机' if result['hit_rate'] > 0.5 else '不优于随机'})")
    print(f"\n样本外指标(月频,年化12期):")
    print(f"  年化收益: {m['ann_ret']:+.2%}")
    print(f"  年化波动: {m['ann_vol']:.2%}")
    print(f"  夏普比率: {m['sharpe']:+.2f}  "
          f"({'>0.3 稳健' if m['sharpe'] > 0.3 else '>0 有效但弱' if m['sharpe'] > 0 else '≤0 失效'})")
    print(f"  最大回撤: {m['max_dd']:+.2%}")
    print(f"  月度胜率: {m['win_rate']:.1%}")

    # 分年度
    log["year"] = log["ym"].apply(lambda ym: ym[0])
    print(f"\n分年度样本外:")
    yearly = log.groupby("year").agg(
        ret=("month_ret", "mean"),
        hit=("hit", "mean"),
        n=("month_ret", "count"),
    )
    yearly["ann_ret"] = yearly["ret"] * 12
    print(yearly.to_string(float_format=lambda x: f"{x:+.4f}" if abs(x) < 5 else f"{x:.0f}"))

    # 判定
    print("\n" + "=" * 72)
    print("过拟合判定")
    print("=" * 72)
    oos_sharpe = m["sharpe"]
    in_sample_sharpe = 0.71   # 之前全样本结果
    if np.isnan(oos_sharpe):
        print("样本外数据不足,无法判定")
    elif oos_sharpe <= 0:
        print(f"❌ 证伪:样本外夏普 {oos_sharpe:+.2f} ≤ 0,全样本0.71主要是前视/运气")
    elif oos_sharpe < 0.3:
        print(f"⚠️ 部分过拟合:样本外夏普 {oos_sharpe:+.2f}(全样本0.71的{oos_sharpe/0.71:.0%})")
        print(f"   反转效应真实但弱,实盘预期夏普约 {oos_sharpe:.2f}")
    else:
        print(f"✓ 通过:样本外夏普 {oos_sharpe:+.2f}(全样本0.71的{oos_sharpe/0.71:.0%})")
        print(f"   反转效应稳健,非过拟合")

    # 输出 + 图
    log.drop(columns=["ym", "year"]).to_csv(OUTPUT_DIR / "walkforward_log.csv")
    pd.DataFrame([m] + [{"hit_rate": result["hit_rate"],
                         "in_sample_sharpe": in_sample_sharpe}]).to_csv(
        OUTPUT_DIR / "walkforward_summary.csv", index=False)
    _plot(result, yearly)
    print(f"\n产物: {OUTPUT_DIR}/walkforward_*.csv + walkforward_nav.png")


def _plot(result, yearly) -> None:
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(12, 7))
    # 样本外净值
    result["nav"].plot(ax=ax1, linewidth=1.5, color="steelblue")
    ax1.axhline(1, color="black", linewidth=0.5)
    ax1.set_title("样本外累计净值(Walk-Forward,严格无前视)")
    ax1.set_ylabel("净值")
    ax1.grid(True, alpha=0.3)
    # 分年度收益
    yearly["ann_ret"].plot.bar(ax=ax2, color=["g" if v > 0 else "r" for v in yearly["ann_ret"]])
    ax2.axhline(0, color="black", linewidth=0.5)
    ax2.set_title("分年度样本外年化收益")
    ax2.set_ylabel("年化收益")
    ax2.set_xlabel("年份")
    ax2.grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "walkforward_nav.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
