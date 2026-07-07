"""数据接入层:从 tushare 拉真实 A 股行情,带本地 parquet 缓存。

设计要点:
- token 只从环境变量读取,源码不硬编码(见 .env,已 gitignore)。
- daily(日线)接口仅需 120 积分,基本都能调通。
- moneyflow(个股资金流向)需要 2000 积分,账户不够会抛异常,
  这里捕获后返回 None 并打印明确提示,让上层优雅降级(跳过 OBI 因子)。
- 拉到的数据按 ts_code+日期范围缓存为 parquet,二次运行不重复拉,省 API 额度。
"""
from __future__ import annotations

import os
from pathlib import Path

import pandas as pd
from dotenv import load_dotenv

# 模块导入时加载一次 .env(从本文件所在目录读)
load_dotenv(Path(__file__).parent / ".env")

_CACHE_DIR = Path(__file__).parent / "cache"
_CACHE_DIR.mkdir(exist_ok=True)


def load_token() -> str:
    """从环境变量读取 tushare token。缺失时给出明确指引。"""
    token = os.getenv("TUSHARE_TOKEN")
    if not token:
        raise RuntimeError(
            "未找到 TUSHARE_TOKEN。请在 quant/.env 中设置(参考 .env.example)。"
        )
    import tushare as ts
    ts.set_token(token)
    return token


def _cache_path(name: str) -> Path:
    return _CACHE_DIR / f"{name}.parquet"


def _read_cache(name: str) -> pd.DataFrame | None:
    p = _cache_path(name)
    if p.exists():
        return pd.read_parquet(p)
    return None


def _write_cache(name: str, df: pd.DataFrame) -> None:
    df.to_parquet(_cache_path(name), index=False)


def fetch_daily(ts_code: str, start_date: str, end_date: str,
                use_cache: bool = True) -> pd.DataFrame:
    """拉取单只股票的日线行情(OHLCV)。

    Args:
        ts_code: 股票代码,如 "000001.SZ"(平安银行)、"600519.SH"(贵州茅台)。
        start_date/end_date: "YYYYMMDD" 格式。
        use_cache: 命中缓存则直接返回,不调 API。

    Returns:
        按 trade_date 升序的 DataFrame,列:ts_code, trade_date, open, high, low,
        close, vol(手), amount(千元), pct_chg(涨跌幅%) 等。
    """
    cache_name = f"daily_{ts_code}_{start_date}_{end_date}"
    if use_cache:
        cached = _read_cache(cache_name)
        if cached is not None:
            print(f"[cache] 命中日线缓存: {cache_name}")
            return cached

    import tushare as ts
    pro = ts.pro_api()
    print(f"[api] 拉取日线 {ts_code} {start_date}~{end_date} ...")
    df = pro.daily(ts_code=ts_code, start_date=start_date, end_date=end_date)
    df = df.sort_values("trade_date").reset_index(drop=True)
    _write_cache(cache_name, df)
    print(f"[api] 拉到 {len(df)} 条日线,已缓存。")
    return df


def fetch_moneyflow(ts_code: str, start_date: str, end_date: str,
                    use_cache: bool = True) -> pd.DataFrame | None:
    """拉取个股资金流向(含大单/小单买卖金额)。

    需 2000 积分。积分不足时返回 None 并打印提示,上层据此跳过 OBI 因子。
    其他异常(网络等)同样降级为 None,保证主流程不中断。

    Returns:
        按 trade_date 升序的 DataFrame;或 None(不可用时)。
    """
    cache_name = f"moneyflow_{ts_code}_{start_date}_{end_date}"
    if use_cache:
        cached = _read_cache(cache_name)
        if cached is not None:
            print(f"[cache] 命中资金流向缓存: {cache_name}")
            return cached

    try:
        import tushare as ts
        pro = ts.pro_api()
        print(f"[api] 拉取资金流向 {ts_code} {start_date}~{end_date} ...")
        df = pro.moneyflow(ts_code=ts_code, start_date=start_date, end_date=end_date)
        df = df.sort_values("trade_date").reset_index(drop=True)
        _write_cache(cache_name, df)
        print(f"[api] 拉到 {len(df)} 条资金流向,已缓存。")
        return df
    except Exception as e:
        msg = str(e)
        # tushare 积分不足通常包含这些关键字
        if "积分" in msg or "permission" in msg.lower() or "2000" in msg:
            print(f"[warn] moneyflow 接口需要 2000 积分,当前账户权限不足。")
            print(f"[warn] OBI 因子将跳过(不影响 momentum / vwap)。原始错误: {e}")
        else:
            print(f"[warn] 资金流向拉取失败,OBI 因子将跳过。错误: {e}")
        return None


# ──────────────────────────────────────────────────────────────
# 批量拉取(沪深300 因子评估用)
# ──────────────────────────────────────────────────────────────

import time

# 限流间隔:2000积分约200次/分钟,留余量取 ~170次/分钟
_API_SLEEP = 0.35


def fetch_hs300_constituents(trade_date: str = "20240101") -> list[str]:
    """拉取沪深300成分股代码列表。

    用 index_weight 接口(需2000积分)。失败时降级到一组硬编码的大盘股
    (保证流程能跑,但会打印警告)。
    """
    cache_name = f"hs300_{trade_date}"
    cached = _read_cache(cache_name)
    if cached is not None:
        print(f"[cache] 命中沪深300成分股缓存: {len(cached)} 只")
        return cached["con_code"].tolist()

    try:
        import tushare as ts
        pro = ts.pro_api()
        print(f"[api] 拉取沪深300成分股(trade_date={trade_date}) ...")
        df = pro.index_weight(index_code="399300.SZ", start_date=trade_date,
                              end_date=trade_date)
        if df is None or df.empty:
            # 该日无数据(非调仓日),放宽到整个月
            df = pro.index_weight(index_code="399300.SZ",
                                  start_date=trade_date[:6] + "01",
                                  end_date=trade_date[:6] + "28")
            df = df.drop_duplicates("con_code")
        codes = df["con_code"].tolist()
        _write_cache(cache_name, df[["con_code"]])
        print(f"[api] 拉到 {len(codes)} 只成分股,已缓存。")
        return codes
    except Exception as e:
        print(f"[warn] 沪深300成分股拉取失败,降级到硬编码大盘股: {e}")
        # 降级:一组流动性好的大盘股,保证流程能继续
        fallback = ["000001.SZ", "000002.SZ", "000063.SZ", "000333.SZ", "000651.SZ",
                    "000858.SZ", "002594.SZ", "600000.SH", "600036.SH", "600519.SH"]
        return fallback


def fetch_batch(codes: list[str], start_date: str, end_date: str,
                kind: str = "daily", sleep: float = _API_SLEEP) -> dict[str, pd.DataFrame]:
    """批量拉取多只股票的 daily 或 moneyflow。

    - 限流:每次调用后 sleep(避免触发频次限制)
    - 断点续传:命中缓存的标的跳过,不重复拉
    - 失败容错:单只失败记到 cache/_failed_{kind}.csv,不阻塞其余
    - 进度:每 20 只打印一次进度

    Returns:
        {ts_code: DataFrame} 字典,只含成功拉取的标的。
    """
    assert kind in ("daily", "moneyflow")
    fetch_fn = fetch_daily if kind == "daily" else fetch_moneyflow
    results: dict[str, pd.DataFrame] = {}

    failed_path = _CACHE_DIR / f"_failed_{kind}.csv"
    failed_prev = set()
    if failed_path.exists():
        failed_prev = set(pd.read_csv(failed_path)["ts_code"].tolist())

    n = len(codes)
    new_failures: list[str] = []
    for i, code in enumerate(codes, 1):
        # 历史上失败过的标的,本次主动重试(可能之前是临时网络问题)
        df = fetch_fn(code, start_date, end_date, use_cache=True)
        if df is None or (hasattr(df, "empty") and df.empty):
            new_failures.append(code)
        else:
            results[code] = df
        if i % 20 == 0 or i == n:
            ok = len(results)
            print(f"[batch {kind}] 进度 {i}/{n}  成功 {ok}  失败 {len(new_failures)}")
        time.sleep(sleep)

    # 合并失败记录(去重)
    all_failed = (failed_prev | set(new_failures)) - set(results.keys())
    if all_failed:
        pd.DataFrame({"ts_code": sorted(all_failed)}).to_csv(failed_path, index=False)
        print(f"[batch {kind}] 失败标的已记录到 {failed_path.name}(共 {len(all_failed)} 只)")

    return results

