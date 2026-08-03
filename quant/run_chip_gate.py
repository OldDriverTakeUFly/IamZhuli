"""Step 1 门槛:cyq_perf 筹码指标的收益预测力(主力蒸馏的关键门槛)。

为什么这次可能成功(区别于之前两次失败):
  - moneyflow 大单桶按订单金额分,主力拆单即可回避 → OBI 信号弱(失败1)
  - 日内冲高回落是强势信号非诱多 → 形态代理错(失败2)
  - cyq_perf 给的是"持仓成本分布"——主力拆单改变订单大小,但改变不了
    筹码总分布。这是结构上无法回避的信号。

5 个衍生指标(业界标准,从 cyq_perf 字段计算):
  1. conc90  = (cost_95 - cost_5) / close    90%筹码集中度(低=单峰密集)
  2. conc70  = (cost_85 - cost_15) / close   70%筹码集中度
  3. winner  = winner_rate                   获利盘比例(%)
  4. costdev = (close - weight_avg) / weight_avg  价格相对平均成本偏离
  5. conc90_chg = conc90 的 20 日差分        集中度收敛=吸筹完成

每个指标在 464只×5年面板上跑 IC/IR/衰减/分年度 + 中性化。
门槛:≥2 指标 5-10 日中性化后 IC 显著(p<0.05, |IC|>0.02)。
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config
import ic_analysis
from data_loader import (load_token, fetch_hs300_constituents_multiyear,
                         fetch_cyq_perf_batch, fetch_industry_map,
                         fetch_daily_basic_batch)

OUTPUT_DIR = Path(__file__).parent / "output"
START, END = "20190101", "20231231"
PERIODS = [1, 5, 10, 20]
GATE_IC_ABS = 0.02
GATE_P = 0.05


def build_chip_panel() -> pd.DataFrame:
    """拉 cyq_perf + daily(close) + 合并,算 5 个筹码指标 + 前瞻收益。"""
    codes = fetch_hs300_constituents_multiyear([2019, 2020, 2021, 2022, 2023])

    print(f"\n拉取 cyq_perf({len(codes)} 只 × 5 年)...")
    cyq = fetch_cyq_perf_batch(codes, START, END)
    if cyq.empty:
        raise RuntimeError("cyq_perf 拉取为空")

    # 拉 daily(只需 close 算指标 + 前瞻收益);复用已缓存的 daily
    from data_loader import fetch_batch
    daily_dict = fetch_batch(codes, START, END, kind="daily")
    daily_frames = []
    for code, d in daily_dict.items():
        df = d[["trade_date", "close"]].copy()
        df["ts_code"] = code
        daily_frames.append(df)
    daily = pd.concat(daily_frames, ignore_index=True)

    # 合并
    panel = cyq.merge(daily, on=["ts_code", "trade_date"], how="inner")
    panel = panel.sort_values(["ts_code", "trade_date"]).reset_index(drop=True)
    print(f"合并面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只")

    # 5 个筹码指标
    panel["conc90"] = (panel["cost_95pct"] - panel["cost_5pct"]) / panel["close"]
    panel["conc70"] = (panel["cost_85pct"] - panel["cost_15pct"]) / panel["close"]
    panel["winner"] = panel["winner_rate"]
    panel["costdev"] = (panel["close"] - panel["weight_avg"]) / panel["weight_avg"]
    # 集中度 20 日变化(收敛为负,发散为正)
    panel["conc90_chg"] = panel.groupby("ts_code")["conc90"].transform(
        lambda s: s - s.shift(20))

    # 前瞻收益
    panel = ic_analysis.compute_forward_returns(panel, PERIODS)
    return panel


def main() -> None:
    print("=" * 72)
    print("Step 1 门槛:cyq_perf 筹码指标的收益预测力")
    print("=" * 72)

    load_token()
    panel = build_chip_panel()
    panel.to_parquet(OUTPUT_DIR / "chip_panel.parquet", index=False)

    # 行业映射 + 市值(中性化用)
    industry_map = fetch_industry_map()
    dbasic = fetch_daily_basic_batch(panel["ts_code"].unique().tolist(), START, END)
    panel = panel.merge(dbasic, on=["ts_code", "trade_date"], how="left")

    factors = ["conc90", "conc70", "winner", "costdev", "conc90_chg"]
    print("\n" + "=" * 72)
    print("原始 IC(未中性化)")
    print("=" * 72)
    raw_results = []
    for f in factors:
        print(f"\n--- {f} ---")
        for n in PERIODS:
            fwd = f"fwd_ret_{n}"
            ic = ic_analysis.rank_ic_series(panel, f, fwd)
            s = ic_analysis.ic_summary(ic)
            s["factor"] = f; s["period"] = n; s["neutralized"] = False
            sig = "✓" if s["significant"] and abs(s["mean"]) >= GATE_IC_ABS else " "
            print(f"  {n:2d}日 IC={s['mean']:+.4f} IR={s['ir']:+.3f} "
                  f"p={s['p']:.4f} {sig}")
            raw_results.append(s)

    # 中性化(剔除行业+市值,看纯信号)——这是关键
    print("\n" + "=" * 72)
    print("中性化后 IC(剔除行业+市值)")
    print("=" * 72)
    neut_results = []
    neut_pass = []   # 记录通过门槛的(因子,周期)
    for f in factors:
        neut_col = f"{f}_neut"
        panel[neut_col] = ic_analysis.neutralize(panel, f, industry_map)
        print(f"\n--- {f} (中性化) ---")
        for n in PERIODS:
            fwd = f"fwd_ret_{n}"
            ic = ic_analysis.rank_ic_series(panel, neut_col, fwd)
            s = ic_analysis.ic_summary(ic)
            s["factor"] = f; s["period"] = n; s["neutralized"] = True
            passed = s["significant"] and abs(s["mean"]) >= GATE_IC_ABS
            sig = "✓" if passed else " "
            if passed and n in (5, 10):   # 门槛只看 5-10 日
                neut_pass.append((f, n, s["mean"]))
            print(f"  {n:2d}日 IC={s['mean']:+.4f} IR={s['ir']:+.3f} "
                  f"p={s['p']:.4f} {sig}")
            neut_results.append(s)

    # 分年度(看通过门槛的因子是否跨期稳定)
    print("\n" + "=" * 72)
    print("分年度 IC(中性化,10日)— 看通过门槛的因子稳定性")
    print("=" * 72)
    pass_factors = list({p[0] for p in neut_pass})
    if not pass_factors:
        # 没通过的,看所有因子里最强的那个的分年度
        best = max(neut_results, key=lambda s: abs(s["mean"]) if s["period"] == 10 else -1)
        pass_factors = [best["factor"]]
        print(f"(无因子通过门槛,展示最强 {best['factor']} 的分年度)")
    for f in pass_factors:
        by = ic_analysis.ic_summary_by_year(panel, f"{f}_neut", "fwd_ret_10")
        print(f"\n{f}_neut (10日):")
        print(by[["ic_mean", "ir", "p", "significant"]].to_string(
            float_format=lambda x: f"{x:+.4f}"))

    # 门槛判定
    print("\n" + "=" * 72)
    print(f"门槛判定(中性化后 5-10 日, |IC|>={GATE_IC_ABS} 且 p<{GATE_P})")
    print("=" * 72)
    # 去重(同因子多周期通过算一个因子)
    pass_factor_set = {p[0] for p in neut_pass}
    if len(pass_factor_set) >= 2:
        print(f"✓ 通过 — {len(pass_factor_set)} 个筹码指标达标: {sorted(pass_factor_set)}")
        for f, n, ic in neut_pass:
            print(f"    {f} {n}日 IC={ic:+.4f}")
        print("\n→ 进入 Step 2:行为蒸馏")
    elif len(pass_factor_set) == 1:
        print(f"⚠️ 仅 1 个达标({pass_factor_set})— 信号弱,谨慎进入 Step 2 或终止")
    else:
        print("✗ 未通过 — 无筹码指标达标,蒸馏项目终止(第三次诚实停)")
        print("结论:即便 cyq_perf 持仓成本分布,在 HS300 大盘股上也无可靠预测力")

    # 输出
    all_res = pd.DataFrame(raw_results + neut_results)
    all_res.to_csv(OUTPUT_DIR / "chip_gate_results.csv", index=False)
    _plot_ic_decay(all_res)
    print(f"\n产物: {OUTPUT_DIR}/chip_gate_results.csv + chip_ic_decay.png")


def _plot_ic_decay(all_res: pd.DataFrame) -> None:
    fig, ax = plt.subplots(figsize=(10, 6))
    for f in all_res["factor"].unique():
        sub = all_res[(all_res["factor"] == f) & (all_res["neutralized"])].sort_values("period")
        ax.plot(sub["period"], sub["mean"], marker="o", label=f"{f}(中性化)")
    ax.axhline(0, color="black", linewidth=0.5)
    ax.axhline(GATE_IC_ABS, color="red", linewidth=0.5, linestyle="--", alpha=0.5)
    ax.axhline(-GATE_IC_ABS, color="red", linewidth=0.5, linestyle="--", alpha=0.5)
    ax.set_xlabel("持仓周期(日)"); ax.set_ylabel("IC 均值")
    ax.set_title("筹码指标中性化后 IC 衰减(Step 1 门槛)")
    ax.legend(fontsize=8); ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "chip_ic_decay.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
