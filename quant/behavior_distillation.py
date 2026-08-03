"""Step 2:主力意图状态蒸馏(透明规则聚类 + 可证伪验证)。

用通过 Step 1 门槛的筹码指标定义 5 个主力意图状态,提取转移矩阵。
关键:每个状态用后续收益验证——若"吸筹"后续真涨、"派发"真跌,
说明聚类捕捉到了真实意图;否则聚类无效。

5 状态(基于经典筹码理论 + 实测分位数):
  - ACCUMULATE(吸筹):筹码收敛(conc90_chg>0) + 获利盘低(<25%) + 价格平稳
      逻辑:主力低位慢慢吸,筹码趋于集中,套牢盘多(低获利)
  - PUMP(拉升):成本偏离扩大(costdev>0.2) + 获利盘快速上升
      逻辑:价格拉离成本区,更多人获利
  - DISTRIBUTE(派发):高位 + 获利盘极高(>85%) + 筹码再度收敛
      逻辑:高位筹码从主力转到散户,集中度回升(散户集中接盘)
  - WASH(洗盘):获利盘急降(洗出不坚定筹码)+ 筹码发散
      逻辑:打压洗人,筹码松动
  - QUIET(静默):其余

可证伪:若各状态后续收益不符号理论预期,聚类无效。
"""
from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

import plot_config
import ic_analysis

OUTPUT_DIR = Path(__file__).parent / "output"
PANEL_PATH = OUTPUT_DIR / "chip_panel.parquet"
PERIODS = [5, 10, 20]

# 状态枚举(中文标签便于报告)
STATES = ["ACCUMULATE", "PUMP", "DISTRIBUTE", "WASH", "QUIET"]
STATE_CN = {"ACCUMULATE": "吸筹", "PUMP": "拉升", "DISTRIBUTE": "派发",
            "WASH": "洗盘", "QUIET": "静默"}


def classify_intent(df: pd.DataFrame) -> pd.Series:
    """透明规则聚类:每个 stock-day 归入一个意图状态。

    阈值基于 Step 1 的实测分位数 + 筹码理论。
    返回状态字符串 Series(与 df 等长)。
    """
    c = df["conc90_chg"].fillna(0)
    w = df["winner"].fillna(50)
    d = df["costdev"].fillna(0)
    # 价格趋势(近5日涨跌,用 close 算)— 没有的话用 costdev 近似
    # winner 的变化(获利盘是否在升)
    w_chg = df.groupby("ts_code")["winner"].transform(
        lambda s: s - s.shift(5)).fillna(0)

    state = pd.Series("QUIET", index=df.index, dtype="object")

    # 注意顺序:越特异的越先判。DISTRIBUTE/ACCUMULATE 条件严,先判。
    # 派发:高位(高 costdev)+ 获利盘极高 + 筹码收敛(主力出货给散户,筹码重新集中)
    state.mask((d > 0.2) & (w > 85) & (c > 0.01), "DISTRIBUTE", inplace=True)
    # 吸筹:筹码收敛 + 获利盘低(套牢多)+ 价格平稳(未拉升)
    state.mask((c > 0.02) & (w < 25) & (d.abs() < 0.15), "ACCUMULATE", inplace=True)
    # 拉升:价格远离成本 + 获利盘上升
    state.mask((d > 0.2) & (w_chg > 5) & (w > 30) & (w < 85), "PUMP", inplace=True)
    # 洗盘:获利盘明显下降(洗出)+ 筹码发散
    state.mask((w_chg < -10) & (c < -0.01), "WASH", inplace=True)

    return state


def transition_matrix(states: pd.Series, ts_codes: pd.Series,
                      dates: pd.Series) -> pd.DataFrame:
    """算状态转移矩阵 P(A→B):相邻交易日(同股票)的状态转移频率。

    Returns: DataFrame,index=from_state, columns=to_state, 值=概率(行和=1)。
    """
    df = pd.DataFrame({"state": states.values, "ts_code": ts_codes.values,
                       "date": dates.values})
    df = df.sort_values(["ts_code", "date"])
    df["next_state"] = df.groupby("ts_code")["state"].shift(-1)
    valid = df.dropna(subset=["next_state"])
    # 转移计数
    trans = pd.crosstab(valid["state"], valid["next_state"]).reindex(
        index=STATES, columns=STATES, fill_value=0)
    # 行归一化为概率
    return trans.div(trans.sum(axis=1), axis=0).fillna(0)


def state_durations(states: pd.Series, ts_codes: pd.Series,
                    dates: pd.Series) -> pd.DataFrame:
    """各状态的持续天数分布(连续处于同一状态的 run 长度)。"""
    df = pd.DataFrame({"state": states.values, "ts_code": ts_codes.values,
                       "date": dates.values}).sort_values(["ts_code", "date"])
    runs = []
    for code, grp in df.groupby("ts_code"):
        s = grp["state"].values
        if len(s) == 0:
            continue
        cur = s[0]; length = 1
        for st in s[1:]:
            if st == cur:
                length += 1
            else:
                runs.append({"state": cur, "duration": length})
                cur = st; length = 1
        runs.append({"state": cur, "duration": length})
    return pd.DataFrame(runs)


def main() -> None:
    print("=" * 72)
    print("Step 2:主力意图状态蒸馏(透明聚类 + 可证伪验证)")
    print("=" * 72)

    panel = pd.read_parquet(PANEL_PATH)
    # 补前瞻收益(若没存)
    for n in PERIODS:
        if f"fwd_ret_{n}" not in panel.columns:
            panel = ic_analysis.compute_forward_returns(panel, [n])
    print(f"面板: {len(panel)} 行, {panel['ts_code'].nunique()} 只\n")

    # 1. 聚类
    panel["intent"] = classify_intent(panel)
    counts = panel["intent"].value_counts()
    print("=== 状态分布 ===")
    for s in STATES:
        n = counts.get(s, 0)
        print(f"  {STATE_CN[s]:<4} ({s:<11}): {n:>7} ({n/len(panel):.1%})")

    # 2. 可证伪验证:各状态后续收益是否符合理论预期
    print("\n=== 可证伪验证:各状态后续收益 ===")
    print(f"{'状态':<16} {'5日均收益':>10} {'10日':>10} {'20日':>10} {'预期':<12} {'判定':<8}")
    expectations = {"ACCUMULATE": "+(吸筹后涨)", "PUMP": "+(拉升中)",
                    "DISTRIBUTE": "-(派发后跌)", "WASH": "±(洗盘后回升)",
                    "QUIET": "0(中性)"}
    verdicts = {}
    for s in STATES:
        sub = panel[panel["intent"] == s]
        r5 = sub["fwd_ret_5"].mean()
        r10 = sub["fwd_ret_10"].mean()
        r20 = sub["fwd_ret_20"].mean()
        exp = expectations[s]
        # 判定:预期+的需 r10>0;预期-的需 r10<0;QUIET 需接近0
        if s in ("ACCUMULATE", "PUMP"):
            ok = r10 > 0.003
        elif s == "DISTRIBUTE":
            ok = r10 < -0.003
        elif s == "WASH":
            ok = r20 > 0   # 洗盘后20日应回升
        else:
            ok = abs(r10) < 0.005
        verdicts[s] = ok
        print(f"{STATE_CN[s]:<4}({s:<11}) {r5:>+10.4f} {r10:>+10.4f} {r20:>+10.4f} "
              f"{exp:<12} {'✓' if ok else '✗':<8}")

    n_pass = sum(verdicts.values())
    print(f"\n状态验证通过: {n_pass}/5")

    # 3. 转移矩阵
    print("\n=== 状态转移矩阵 P(A→B)(行=当前,列=次日) ===")
    tm = transition_matrix(panel["intent"], panel["ts_code"], panel["trade_date"])
    print("        " + "  ".join(f"{STATE_CN[s]:>5}" for s in STATES))
    for s in STATES:
        row = "  ".join(f"{tm.loc[s, t]:>5.2f}" for t in STATES)
        print(f"{STATE_CN[s]:>5}  {row}")

    # 4. 持续天数
    print("\n=== 各状态持续天数分布(中位数) ===")
    dur = state_durations(panel["intent"], panel["ts_code"], panel["trade_date"])
    for s in STATES:
        sd = dur[dur["state"] == s]["duration"]
        if len(sd) > 0:
            print(f"  {STATE_CN[s]:<4}({s:<11}): 中位{sd.median():.0f}日 "
                  f"均值{sd.mean():.1f} 最大{sd.max()}")

    # 5. 输出指纹 JSON(供 Step 3 反哺用)
    fingerprint = {
        "meta": {"source": "cyq_perf 464只×5年", "method": "透明规则聚类"},
        "state_distribution": {s: float(counts.get(s, 0) / len(panel)) for s in STATES},
        "transition_matrix": {s: {t: float(tm.loc[s, t]) for t in STATES} for s in STATES},
        "state_returns_10d": {s: float(panel[panel["intent"] == s]["fwd_ret_10"].mean())
                              for s in STATES},
        "state_durations_median": {
            s: float(dur[dur["state"] == s]["duration"].median()) if len(dur[dur["state"] == s]) else 0
            for s in STATES},
    }
    import json
    with open(OUTPUT_DIR / "behavior_fingerprint.json", "w", encoding="utf-8") as f:
        json.dump(fingerprint, f, ensure_ascii=False, indent=2)

    # 图
    _plot_transition(tm)
    _plot_state_returns(panel)
    _plot_transition_heatmap(tm)

    print(f"\n产物:")
    print(f"  behavior_fingerprint.json (结构化指纹)")
    print(f"  intent_transition.png / intent_returns.png / transition_heatmap.png")

    # 总判定
    print("\n" + "=" * 72)
    print("Step 2 判定")
    print("=" * 72)
    if n_pass >= 4:
        print(f"✓ 通过({n_pass}/5)— 多数状态后续收益符合意图,聚类有效")
        print("→ 指纹可用于 Step 3(反哺 C#)或作为独立洞察")
    elif n_pass >= 3:
        print(f"⚠️ 部分通过({n_pass}/5)— 聚类部分有效,需人工审视哪些状态可信")
    else:
        print(f"✗ 未通过({n_pass}/5)— 状态后续收益不符合理论,聚类无效")


def _plot_transition(tm: pd.DataFrame) -> None:
    fig, ax = plt.subplots(figsize=(8, 5))
    x = np.arange(len(STATES))
    width = 0.15
    for i, s in enumerate(STATES):
        ax.bar(x + i * width - 0.3, tm.loc[s, STATES].values, width,
               label=f"从{STATE_CN[s]}")
    ax.set_xticks(x)
    ax.set_xticklabels([STATE_CN[s] for s in STATES])
    ax.set_ylabel("转移概率")
    ax.set_title("主力意图状态转移分布(从状态X到各状态的概率)")
    ax.legend(fontsize=8)
    ax.grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "intent_transition.png", dpi=120)
    plt.close()


def _plot_state_returns(panel: pd.DataFrame) -> None:
    fig, ax = plt.subplots(figsize=(9, 5))
    r5 = [panel[panel["intent"] == s]["fwd_ret_5"].mean() for s in STATES]
    r10 = [panel[panel["intent"] == s]["fwd_ret_10"].mean() for s in STATES]
    r20 = [panel[panel["intent"] == s]["fwd_ret_20"].mean() for s in STATES]
    x = np.arange(len(STATES)); w = 0.25
    ax.bar(x - w, r5, w, label="5日", color="#4FC3F7")
    ax.bar(x, r10, w, label="10日", color="#29B6F6")
    ax.bar(x + w, r20, w, label="20日", color="#0277BD")
    ax.axhline(0, color="black", linewidth=0.5)
    ax.set_xticks(x)
    ax.set_xticklabels([f"{STATE_CN[s]}\n{s}" for s in STATES], fontsize=9)
    ax.set_ylabel("后续平均收益")
    ax.set_title("各意图状态后续收益(可证伪验证)")
    ax.legend()
    ax.grid(True, axis="y", alpha=0.3)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "intent_returns.png", dpi=120)
    plt.close()


def _plot_transition_heatmap(tm: pd.DataFrame) -> None:
    fig, ax = plt.subplots(figsize=(7, 6))
    im = ax.imshow(tm.values, cmap="YlOrRd", aspect="auto", vmin=0, vmax=1)
    ax.set_xticks(range(len(STATES)))
    ax.set_yticks(range(len(STATES)))
    ax.set_xticklabels([STATE_CN[s] for s in STATES])
    ax.set_yticklabels([STATE_CN[s] for s in STATES])
    ax.set_xlabel("转移到"); ax.set_ylabel("从")
    for i in range(len(STATES)):
        for j in range(len(STATES)):
            v = tm.values[i, j]
            ax.text(j, i, f"{v:.2f}", ha="center", va="center",
                    color="white" if v > 0.5 else "black", fontsize=9)
    ax.set_title("意图状态转移矩阵热力图")
    fig.colorbar(im, ax=ax, fraction=0.046)
    plt.tight_layout()
    plt.savefig(OUTPUT_DIR / "transition_heatmap.png", dpi=120)
    plt.close()


if __name__ == "__main__":
    main()
