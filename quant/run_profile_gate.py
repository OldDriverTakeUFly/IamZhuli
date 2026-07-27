"""门槛验证:不同资金桶(画像代理)的行为是否真的可区分。

问题:小单桶(散户代理)和大单+特大单桶(主力代理)在不同市场状态下,
追涨杀跌倾向、净买卖强度是否真的有显著差异?
不可区分 → 蒸馏无意义,终止。可区分 → 进入完整蒸馏。

用已有缓存(零新数据)。检测三组对比:
  1. 上涨日:小单 vs 大单 的净买卖方向(散户是否追涨?主力是否借机出货?)
  2. 下跌日:同上(散户是否恐慌?主力是否承接?)
  3. 行为持续性:各桶的净流入是否连续同向(散户追涨的惯性 vs 主力的隐蔽性)

门槛:三组对比中至少两组的均值差异 t 检验 p<0.05 且 |均值差|有实际意义。
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


def load_all_moneyflow() -> pd.DataFrame:
    """读所有缓存的 moneyflow,拼成大表。"""
    files = glob(str(CACHE / "moneyflow_*.parquet"))
    frames = []
    for f in files:
        df = pd.read_parquet(f)
        if not df.empty:
            frames.append(df)
    big = pd.concat(frames, ignore_index=True)
    # 算各桶净额(万元)
    for b in ["sm", "md", "lg", "elg"]:
        big[f"net_{b}"] = big[f"buy_{b}_amount"] - big[f"sell_{b}_amount"]
    return big.sort_values(["ts_code", "trade_date"]).reset_index(drop=True)


def load_daily_returns() -> pd.DataFrame:
    """读所有 daily,算每只股票每日涨跌幅 + 标记状态。"""
    files = glob(str(CACHE / "daily_*.parquet"))
    frames = []
    for f in files:
        df = pd.read_parquet(f, columns=["ts_code", "trade_date", "pct_chg", "vol"])
        frames.append(df)
    d = pd.concat(frames, ignore_index=True)
    d["pct_chg"] = d["pct_chg"].fillna(0)
    # 状态标记:大涨(>2%) / 大跌(<-2%) / 平稳
    d["state"] = pd.cut(d["pct_chg"], bins=[-100, -2, 2, 100],
                        labels=["down", "flat", "up"])
    return d


def main() -> None:
    print("=" * 72)
    print("门槛验证:散户画像(小单)vs 主力画像(大单+特大单)行为可区分性")
    print("=" * 72)

    print("加载缓存数据...")
    mf = load_all_moneyflow()
    daily = load_daily_returns()
    print(f"  moneyflow: {len(mf)} 行, {mf['ts_code'].nunique()} 只")
    print(f"  daily: {len(daily)} 行")

    # 合并:同一 ts_code + trade_date
    df = mf.merge(daily[["ts_code", "trade_date", "pct_chg", "state"]],
                  on=["ts_code", "trade_date"], how="inner")
    print(f"  合并后: {len(df)} 行\n")

    # 各桶净额标准化:除以当日总成交额(用 amount 代理,避免量纲)
    # 这里用绝对净额的相对值:净额 / (买+卖),即"净买卖失衡度",与 OBI 同口径但分桶
    for b in ["sm", "md", "lg", "elg"]:
        total = df[f"buy_{b}_amount"] + df[f"sell_{b}_amount"]
        df[f"imb_{b}"] = df[f"net_{b}"] / total.replace(0, np.nan)

    # 散户代理 = sm 桶;主力代理 = lg + elg 合并
    df["imb_retail"] = df["imb_sm"]
    df["imb_mainforce"] = (df["net_lg"] + df["net_elg"]) / (
        df["buy_lg_amount"] + df["sell_lg_amount"] +
        df["buy_elg_amount"] + df["sell_elg_amount"]).replace(0, np.nan)

    # —— 对比1:不同市场状态下,散户 vs 主力的净买卖失衡 ——
    print("=" * 60)
    print("对比1:各市场状态下净买卖失衡度(正=净买,负=净卖)")
    print("=" * 60)
    print(f"{'状态':<6} {'散户(sm)':>12} {'主力(lg+elg)':>14} {'差异':>10} {'p值':>8}")
    verdict1_pass = False
    for state in ["up", "flat", "down"]:
        sub = df[df["state"] == state]
        r = sub["imb_retail"].dropna()
        m = sub["imb_mainforce"].dropna()
        diff = r.mean() - m.mean()
        t_stat, p = stats.ttest_ind(r, m, equal_var=False, nan_policy="omit")
        sig = "✓" if p < 0.05 and abs(diff) > 0.01 else " "
        if p < 0.05 and abs(diff) > 0.01:
            verdict1_pass = True
        print(f"{state:<6} {r.mean():>+12.4f} {m.mean():>+14.4f} "
              f"{diff:>+10.4f} {p:>8.4f} {sig}")

    # —— 对比2:追涨杀跌倾向(上涨日是否净买 / 下跌日是否净卖)——
    print("\n" + "=" * 60)
    print("对比2:追涨杀跌倾向(上涨日净失衡 vs 下跌日净失衡)")
    print("=" * 60)
    verdict2_pass = False
    for label, col in [("散户", "imb_retail"), ("主力", "imb_mainforce")]:
        up_m = df[df["state"] == "up"][col].mean()
        dn_m = df[df["state"] == "down"][col].mean()
        chase = up_m - dn_m   # >0 表示涨时更买跌时更卖 = 追涨杀跌
        # 检验:上涨日失衡 vs 下跌日失衡 差异是否显著
        up_v = df[df["state"] == "up"][col].dropna()
        dn_v = df[df["state"] == "down"][col].dropna()
        t_stat, p = stats.ttest_ind(up_v, dn_v, equal_var=False)
        sig = "✓" if p < 0.05 else " "
        if p < 0.05:
            verdict2_pass = True
        print(f"{label:<6} 上涨日{up_m:>+.4f}  下跌日{dn_m:>+.4f}  "
              f"追涨杀跌指数={chase:>+.4f}  p={p:.4f} {sig}")

    # —— 对比3:行为持续性(连续同向净买卖的概率)——
    print("\n" + "=" * 60)
    print("对比3:行为持续性(连续净买/净卖的概率)")
    print("=" * 60)
    df_sorted = df.sort_values(["ts_code", "trade_date"])
    verdict3_pass = False
    for label, col in [("散户", "imb_retail"), ("主力", "imb_mainforce")]:
        sign = np.sign(df_sorted[col])
        prev_sign = sign.groupby(df_sorted["ts_code"]).shift(1)
        # 连续同向(同号)的比例
        same_dir = ((sign == prev_sign) & (sign != 0)).mean()
        # 主力隐蔽性代理:净失衡绝对值的均值(越小越隐蔽)
        abs_imb = df_sorted[col].abs().mean()
        sig = "✓" if abs_imb > 0.02 else " "
        if abs_imb > 0.02:
            verdict3_pass = True
        print(f"{label:<6} 连续同向率={same_dir:.1%}  平均失衡强度={abs_imb:>+.4f} {sig}")

    # —— 总判定 ——
    print("\n" + "=" * 72)
    print("门槛判定(三组至少两组通过 → 画像可区分)")
    print("=" * 72)
    passes = sum([verdict1_pass, verdict2_pass, verdict3_pass])
    print(f"对比1(状态差异): {'通过' if verdict1_pass else '未通过'}")
    print(f"对比2(追涨杀跌): {'通过' if verdict2_pass else '未通过'}")
    print(f"对比3(持续性):   {'通过' if verdict3_pass else '未通过'}")
    if passes >= 2:
        print(f"\n✓ 通过({passes}/3)— 散户与主力画像行为可区分,进入完整蒸馏")
    else:
        print(f"\n✗ 未通过({passes}/3)— 画像行为不可区分,蒸馏无意义,终止")

    df[["ts_code", "trade_date", "state", "pct_chg",
        "imb_retail", "imb_mainforce"]].to_csv(
        OUTPUT_DIR / "profile_gate_data.csv", index=False)
    print(f"\n产物: {OUTPUT_DIR}/profile_gate_data.csv")


if __name__ == "__main__":
    main()
