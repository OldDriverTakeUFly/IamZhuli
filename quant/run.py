"""主入口:拉取真实 A 股行情 → 计算因子 → 打印统计 + 输出 csv。

用法:
    python3 quant/run.py                  # 默认: 平安银行 近1年
    python3 quant/run.py 600519.SH 20230101 20240101   # 指定股票和区间

这是 MVP:只验证"真实数据 → 因子值"这条链路,不做策略回测。
OBI 因子依赖 moneyflow 接口(需 2000 积分);权限不足会自动跳过,不影响其余因子。
"""
from __future__ import annotations

import sys
from pathlib import Path

import pandas as pd

from data_loader import load_token, fetch_daily, fetch_moneyflow
import factors

OUTPUT_DIR = Path(__file__).parent / "output"
OUTPUT_DIR.mkdir(exist_ok=True)


def describe_factor(name: str, s: pd.Series) -> None:
    """打印因子序列的简单统计(非空值数、均值、分位数)。"""
    valid = s.dropna()
    if valid.empty:
        print(f"  {name}: 全部为空(样本不足或数据缺失)")
        return
    print(f"  {name}: 有效值 {len(valid)}/{len(s)} 条")
    print(f"      均值={valid.mean():+.4f}  中位数={valid.median():+.4f}  标准差={valid.std():.4f}")
    qs = valid.quantile([0.05, 0.25, 0.5, 0.75, 0.95])
    qstr = "  ".join(f"{int(q*100):>3}%={v:+.3f}" for q, v in qs.items())
    print(f"      分位 {qstr}")


def main(ts_code: str = "000001.SZ",
         start_date: str = "20240101",
         end_date: str = "20241231") -> None:
    print("=" * 70)
    print(f"IamZhuli 因子计算(真实数据)  标的={ts_code}  区间={start_date}~{end_date}")
    print("=" * 70)

    # 1. 加载 token(失败会抛出明确错误)
    load_token()

    # 2. 拉日线
    daily = fetch_daily(ts_code, start_date, end_date)
    if daily.empty:
        print("[error] 日线数据为空,请检查股票代码或日期区间。")
        return
    print(f"\n日线行情: {len(daily)} 条, {daily['trade_date'].iloc[0]} ~ {daily['trade_date'].iloc[-1]}")
    print(daily[["trade_date", "open", "high", "low", "close", "vol", "amount"]].tail(5).to_string(index=False))

    # 3. 拉资金流向(可能返回 None → 降级)
    mf = fetch_moneyflow(ts_code, start_date, end_date)

    # 4. 计算因子
    print("\n" + "-" * 70)
    print("因子统计")
    print("-" * 70)
    mom = factors.momentum(daily["close"], window=20)
    vwap_dev = factors.vwap_deviation(daily, window=20)
    describe_factor("momentum_20d", mom)
    describe_factor("vwap_dev_20d", vwap_dev)

    obi = None
    if mf is not None and not mf.empty:
        obi = factors.obi_from_moneyflow(mf)
        describe_factor("obi_moneyflow", obi)
    else:
        print("  obi_moneyflow: 跳过(moneyflow 接口不可用,积分不足或网络错误)")

    # 5. 合并输出到一张表
    out = daily[["trade_date", "close", "vol", "amount"]].copy()
    out["momentum_20d"] = mom.values
    out["vwap_dev_20d"] = vwap_dev.values
    if obi is not None:
        # 资金流向按 trade_date 对齐回日线
        mf_aligned = obi.rename("obi").to_frame().assign(
            trade_date=mf["trade_date"].values)
        out = out.merge(mf_aligned, on="trade_date", how="left")

    out_path = OUTPUT_DIR / f"factors_{ts_code}_{start_date}_{end_date}.csv"
    out.to_csv(out_path, index=False)
    print(f"\n[output] 因子表已保存: {out_path}")
    print("\n末尾 5 行:")
    print(out.tail(5).to_string(index=False))
    print("\n" + "=" * 70)
    print("完成。MVP 链路验证: 真实行情 → 因子值 已打通。")
    print("下一步可基于此做 IC 分析 / 分层回测 / 多标的扩展。")
    print("=" * 70)


if __name__ == "__main__":
    # 命令行参数: 可选 ts_code start_date end_date
    args = sys.argv[1:]
    code = args[0] if len(args) > 0 else "000001.SZ"
    start = args[1] if len(args) > 1 else "20240101"
    end = args[2] if len(args) > 2 else "20241231"
    main(code, start, end)
