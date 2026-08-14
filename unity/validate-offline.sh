#!/usr/bin/env bash
# Unity 侧离线静态门禁:不装 Unity 也能提前拦截大部分资产错误。
# 检查:UXML 语法 / JSON 语法 / C# 语法版本纪律 / 括号平衡 / MainUI↔UXML 名称一致性 / UxmlFactory。
# 用法: bash unity/validate-offline.sh
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
U="$ROOT/unity/IamZhuli.Unity"
python3 - "$U" << 'PYEOF'
import json, re, sys, xml.etree.ElementTree as ET
from pathlib import Path

unity = Path(sys.argv[1])
fails, warns = [], []

def fail(msg): fails.append(msg)
def warn(msg): warns.append(msg)

# ── 1. UXML = 合法 XML ──
uxml_path = unity / "Assets/IamZhuli/UI/Main.uxml"
try:
    tree = ET.parse(uxml_path)
    uxml_text = uxml_path.read_text(encoding="utf-8")
except Exception as e:
    fail(f"UXML 解析失败: {e}")
    uxml_text = ""

# ── 2. JSON 资产合法 ──
for j in [unity / "Packages/manifest.json"] + list((unity / "Assets").rglob("*.asmdef")):
    try:
        json.loads(j.read_text(encoding="utf-8"))
    except Exception as e:
        fail(f"JSON 非法: {j.relative_to(unity)}: {e}")

# ── 3. Unity C# 版本纪律(2022.3 = C#9;禁 C#10+/需 shim 的特性) ──
forbidden = [
    (re.compile(r"^\s*namespace\s+[\w.]+\s*;", re.M), "文件域命名空间(C#10)"),
    (re.compile(r"\brecord\s+(struct\s+)?[A-Z]\w*\s*\("), "record 类型(需 IsExternalInit shim)"),
    (re.compile(r"\brequired\s+\w"), "required 成员(C#11)"),
    (re.compile(r"^global using ", re.M | re.I), "global using(C#10)"),
]
cs_files = [p for p in (unity / "Assets").rglob("*.cs")]
for cs in cs_files:
    text = cs.read_text(encoding="utf-8")
    for pat, desc in forbidden:
        if pat.search(text):
            fail(f"{cs.relative_to(unity)}: 使用了 {desc}(Unity 2022.3 不支持)")
    # 括号平衡(粗检,字符串内干扰可接受——只在明显失衡时报)
    if text.count("{") != text.count("}"):
        fail(f"{cs.relative_to(unity)}: 花括号不平衡 {{={text.count('{')} }}={text.count('}')}")
    if text.count("(") != text.count(")"):
        fail(f"{cs.relative_to(unity)}: 圆括号不平衡 (={text.count('(')} )={text.count(')')}")

# ── 4. MainUI 的 Q("name") 必须存在于 UXML ──
if uxml_text:
    mainui = unity / "Assets/IamZhuli/Scripts/UI/MainUI.cs"
    if mainui.exists():
        text = mainui.read_text(encoding="utf-8")
        qnames = set(re.findall(r'\.Q(?:<[^>]+>)?\(\s*"([\w-]+)"\s*\)', text))
        # UXML 里全部 name 属性(含 Styles 内联不算)
        uxml_names = set(re.findall(r'name="([\w-]+)"', uxml_text))
        missing = sorted(n for n in qnames if n not in uxml_names)
        if missing:
            fail(f"MainUI 查询但 UXML 缺少的元素名: {', '.join(missing)}")
        print(f"  名称一致性: MainUI 查询 {len(qnames)} 个元素名,全部在 UXML 中" if not missing else "")

# ── 5. 自绘元素有 UxmlFactory(UXML 才能实例化) ──
ts = unity / "Assets/IamZhuli/Scripts/UI/TimeshareElement.cs"
if ts.exists():
    if "UxmlFactory" not in ts.read_text(encoding="utf-8"):
        fail("TimeshareElement 缺少 UxmlFactory(UXML 无法实例化自定义元素)")

# ── 6. asmdef 引用存在性(Runtime 被测试 asmdef 引用) ──
asm = unity / "Assets/IamZhuli/Tests/Editor/IamZhuli.Tests.Editor.asmdef"
if asm.exists():
    d = json.loads(asm.read_text(encoding="utf-8"))
    if "IamZhuli.Runtime" not in d.get("references", []):
        fail("Editor 测试 asmdef 未引用 IamZhuli.Runtime")

print(f"  检查项: UXML语法 / JSON×{1 + len(list((unity/'Assets').rglob('*.asmdef')))} / C#文件×{len(cs_files)} / 名称一致 / UxmlFactory / asmdef引用")
if warns:
    for w in warns: print(f"  ⚠ {w}")
if fails:
    for f in fails: print(f"  ✗ {f}")
    print(f"FAIL: {len(fails)} 项未通过")
    sys.exit(1)
print("PASS: 离线静态门禁全部通过")
PYEOF
