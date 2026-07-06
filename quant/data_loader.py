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
