"""Step 0: 验证大单 OBI 对未来收益的预测力(整个项目的门槛)。

问题:大单/特大单资金失衡(OBI)对后续收益有没有预测力?
没有 → 整个蒸馏项目停止(无法从无信号的数据蒸馏行为)。
有   → 进入 Step 1 提取行为指纹。

复用已有 5 年中性化面板(464只×1214日),对原始 OBI 和中性化 OBI 都算:
  - 各周期(1/5/10/20日)Rank IC + IR + t检验
  - IC 衰减曲线
  - 分年度稳定性

门槛:5-10 日 IC 显著(p<0.05, |IC|>0.02)。
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd

import plot_config
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "multiyear_panel_neut.parquet"
PERIODS = [1, 5, 10, 20]
GATE_IC_ABS = 0.02   # 门槛:|IC| 至少 0.02
GATE_P = 0.05        # 门槛:p < 0.05


def main() -> None:
    print("=" * 72)
    print("Step 0: 大单 OBI 信号验证(蒸馏项目门槛)")
    print("=" * 72)

    panel = pd.read_parquet(PANEL_PATH)
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只, "
          f"{panel['trade_date'].nunique()} 日\n")

    results = []
    for fcol in ["obi_moneyflow", "obi_moneyflow_neut"]:
        print(f"--- {fcol} ---")
        for n in PERIODS:
            fwd = f"fwd_ret_{n}"
            ic = ic_analysis.rank_ic_series(panel, fcol, fwd)
            s = ic_analysis.ic_summary(ic)
            s["factor"] = fcol
            s["period"] = n
            sig = "✓" if s["significant"] and abs(s["mean"]) >= GATE_IC_ABS else " "
            print(f"  {n:2d}日  IC={s['mean']:+.4f}  IR={s['ir']:+.3f}  "
                  f"t={s['t']:+.2f}  p={s['p']:.4f}  {sig}")
            results.append(s)

    df = pd.DataFrame(results)

    # 重点看 5-10 日(中期,主力行为典型周期)
    gate_rows = df[df["period"].isin([5, 10])]
    any_pass = ((gate_rows["significant"]) &
                (gate_rows["mean"].abs() >= GATE_IC_ABS)).any()

    print("\n" + "=" * 72)
    print(f"门槛判定(5/10 日, |IC|>={GATE_IC_ABS} 且 p<{GATE_P})")
    print("=" * 72)
    if any_pass:
        passing = gate_rows[(gate_rows["significant"]) &
                            (gate_rows["mean"].abs() >= GATE_IC_ABS)]
        print(f"✓ 通过 — {len(passing)} 个组合达标,信号真实,进入 Step 1")
        print(passing[["factor", "period", "mean", "ir", "p"]].to_string(index=False))
    else:
        print("✗ 未通过 — OBI 对中期收益无可靠预测力,蒸馏项目终止")
        print("(如实结论:大单资金流信号在沪深300大盘股上太弱,无法蒸馏主力行为)")

    # 分年度(看哪个 OBI 更稳,Step 1 用更稳的那个)
    print("\n--- 分年度 IC(选 Step 1 用的 OBI 版本)---")
    for fcol in ["obi_moneyflow", "obi_moneyflow_neut"]:
        by = ic_analysis.ic_summary_by_year(panel, fcol, "fwd_ret_10")
        print(f"\n{fcol} (10日):")
        print(by[["ic_mean", "ir", "p", "significant"]].to_string(
            float_format=lambda x: f"{x:+.4f}"))

    # 输出 + 图
    df.to_csv(OUTPUT_DIR / "obi_signal_validation.csv", index=False)
    _plot_ic_decay(df)
    print(f"\n产物: {OUTPUT_DIR}/obi_signal_validation.csv + obi_ic_decay.png")
    print("\n门槛通过?" , "是 → 继续 Step 1" if any_pass else "否 → 终止")


def _plot_ic_decay(df: pd.DataFrame) -> None:
    fig, ax = plt.subplots(figsize=(8, 5))
    for fcol in df["factor"].unique():
        sub = df[df["factor"] == fcol].sort_values("period")
        ax.plot(sub["period"], sub["mean"], marker="o", label=fcol)
    ax.axhline(0, color="black", linewidth=0.5)
    ax.axhline(GATE_IC_ABS, color="red", linewidth=0.5, linestyle="--",
               label=f"门槛 ±{GATE_IC_ABS}")
    ax.axhline(-GATE_IC_ABS, color="red", linewidth=0.5, linestyle="--")
    ax.set_xlabel("持仓周期(日)")
    ax.set_ylabel("IC 均值")
    ax.set_title("大单 OBI 信号 IC 衰减(Step 0 验证)")
    ax.legend()
    ax.grid(True, alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "obi_ic_decay.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
