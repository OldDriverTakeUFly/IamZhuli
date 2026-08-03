"""门槛验证:日内冲高回落(诱多代理)形态是否有后续下跌预测力。

问题:日线代理能否捕捉"诱多"?形态定义后的后续收益是否显著为负?
不显著 → 形态无效或日线粒度不够,终止。

诱多(日内冲高回落)代理定义:
  - 冲高幅度 spike = (high - max(open, close)) / max(open, close)
    (盘中冲过比开盘和收盘都高的位置)
  - 上影线占比 upper_shadow_ratio = (high - close) / (high - low)
    (收盘处于当日区间的低位 → 冲高后回落)

判定为"疑似诱多"需同时满足:
  - spike >= SPIKE_THRESH(如 1.5%,盘中明显冲高)
  - upper_shadow_ratio >= SHADOW_THRESH(如 0.6,收盘在区间下部40%)
  - 当日收红或微跌(避免和普通大跌混淆)

门槛:这类事件后续 5/10/20 日收益显著为负(t检验 p<0.05),
      且事件覆盖率 >= 1% 交易日(代理不至于太苛刻)。
"""
from __future__ import annotations

from pathlib import Path
from glob import glob

import numpy as np
import pandas as pd
from scipy import stats

CACHE = Path(__file__).parent / "cache"
OUTPUT_DIR = Path(__file__).parent / "output"
OUTPUT_DIR.mkdir(exist_ok=True)

SPIKE_THRESH = 0.015      # 盘中冲高 1.5%
SHADOW_THRESH = 0.6       # 收盘在区间下部 60% 以上
PERIODS = [1, 5, 10, 20]


def load_all_daily() -> pd.DataFrame:
    """读所有 daily 缓存,拼大表,算冲高回落特征 + 前瞻收益。"""
    files = glob(str(CACHE / "daily_*.parquet"))
    frames = []
    for f in files:
        df = pd.read_parquet(f)
        frames.append(df)
    big = pd.concat(frames, ignore_index=True)
    big = big.sort_values(["ts_code", "trade_date"]).reset_index(drop=True)

    # 冲高回落特征
    body_top = big[["open", "close"]].max(axis=1)
    big["spike"] = (big["high"] - body_top) / body_top.replace(0, np.nan)
    range_hl = (big["high"] - big["low"]).replace(0, np.nan)
    big["upper_shadow_ratio"] = (big["high"] - big["close"]) / range_hl

    # 前瞻收益
    for n in PERIODS:
        big[f"fwd_{n}"] = big.groupby("ts_code")["close"].transform(
            lambda c: c.shift(-n) / c - 1.0)

    return big


def is_lure(df: pd.DataFrame) -> pd.Series:
    """判定疑似诱多:冲高 + 上影线长 + 非大跌日。"""
    return (
        (df["spike"] >= SPIKE_THRESH) &
        (df["upper_shadow_ratio"] >= SHADOW_THRESH) &
        (df["pct_chg"] > -3.0)   # 排除当日已暴跌(那不是诱多,是真跌)
    )


def main() -> None:
    print("=" * 72)
    print("门槛验证:日内冲高回落(诱多代理)形态的后续收益预测力")
    print("=" * 72)
    print(f"形态: spike>={SPIKE_THRESH:.1%}, 上影线>={SHADOW_THRESH:.0%}, 当日非暴跌\n")

    print("加载缓存...")
    df = load_all_daily()
    print(f"  {len(df)} 行, {df['ts_code'].nunique()} 只\n")

    lure = is_lure(df)
    n_lure = lure.sum()
    n_days = len(df)
    coverage = n_lure / n_days
    print(f"疑似诱多事件: {n_lure} 个 ({coverage:.2%} 交易日)")
    print(f"门槛:覆盖率 >= 1% → {'✓' if coverage >= 0.01 else '✗ 太稀疏'}\n")

    if n_lure < 100:
        print(f"✗ 事件太少({n_lure}<100),无法可靠统计,终止")
        return

    # 后续收益:诱多事件 vs 全样本对照
    print("=" * 60)
    print("后续收益对比(诱多事件 vs 全样本均值)")
    print("=" * 60)
    print(f"{'周期':<6} {'诱多事件':>12} {'全样本':>12} {'差异':>10} {'t':>8} {'p':>8} {'判定':>6}")
    any_pass = False
    event_stats = []
    for n in PERIODS:
        fwd = f"fwd_{n}"
        lure_ret = df.loc[lure, fwd].dropna()
        all_ret = df[fwd].dropna()
        if len(lure_ret) < 50:
            continue
        diff = lure_ret.mean() - all_ret.mean()
        t_stat, p = stats.ttest_1samp(lure_ret, all_ret.mean())
        sig = p < 0.05 and diff < -0.005   # 显著且差异<-0.5%
        verdict = "✓" if sig else " "
        if sig:
            any_pass = True
        print(f"{n:>2}日   {lure_ret.mean():>+12.4f} {all_ret.mean():>+12.4f} "
              f"{diff:>+10.4f} {t_stat:>+8.2f} {p:>8.4f} {verdict:>6}")
        event_stats.append({
            "period": n, "n_events": len(lure_ret),
            "lure_mean": lure_ret.mean(), "all_mean": all_ret.mean(),
            "diff": diff, "t": t_stat, "p": p, "significant": sig,
        })

    # 分年度看稳定性(关键:避免只靠某一年)
    print("\n" + "=" * 60)
    print("分年度:诱多事件 10 日后续收益")
    print("=" * 60)
    df_lure = df.loc[lure].copy()
    df_lure["year"] = df_lure["trade_date"].astype(str).str[:4]
    print(f"{'年份':<6} {'事件数':>8} {'10日均收益':>14} {'是否为负':>10}")
    yearly_pass = 0
    yearly_total = 0
    for yr, sub in df_lure.groupby("year"):
        r = sub["fwd_10"].dropna()
        if len(r) < 20:
            continue
        neg = r.mean() < 0
        yearly_total += 1
        if neg:
            yearly_pass += 1
        print(f"{yr:<6} {len(r):>8} {r.mean():>+14.4f} {'✓' if neg else '✗':>10}")

    # 判定
    print("\n" + "=" * 72)
    print("门槛判定")
    print("=" * 72)
    print(f"形态有预测力(至少一周期显著为负): {'通过' if any_pass else '未通过'}")
    print(f"分年度多数为负({yearly_pass}/{yearly_total}): "
          f"{'通过' if yearly_pass >= yearly_total * 0.6 else '未通过'}")
    overall = any_pass and yearly_pass >= yearly_total * 0.6
    if overall:
        print(f"\n✓ 通过 — 日内冲高回落形态有后续下跌预测力,可做成因子")
    else:
        print(f"\n✗ 未通过 — 形态无可靠预测力(或日线粒度不足以捕捉诱多),终止")

    pd.DataFrame(event_stats).to_csv(
        OUTPUT_DIR / "lure_gate_stats.csv", index=False)
    print(f"\n产物: {OUTPUT_DIR}/lure_gate_stats.csv")


if __name__ == "__main__":
    main()
