#!/usr/bin/env bash
# Unity CLI 测试跑器:装好 Unity 后无需开编辑器界面即可跑测试(NUnit XML 结果)。
# 用法:
#   UNITY_PATH=/path/to/Unity bash unity/run-tests.sh [EditMode|PlayMode|All]
# 说明:
#   - EditMode 可无图形(-batchmode);PlayMode 需要 UI Toolkit 渲染,不加 -batchmode
#   - 结果 XML 输出到 unity/test-results/<platform>.xml
#   - 首次运行 Unity 会导入项目资产(几分钟),属正常
set -eu

PLATFORM="${1:-All}"
UNITY_PATH="${UNITY_PATH:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/unity/IamZhuli.Unity"
OUT="$ROOT/unity/test-results"
mkdir -p "$OUT"

if [ -z "$UNITY_PATH" ]; then
    echo "✗ 请指定 Unity 可执行文件: UNITY_PATH=/path/to/Unity bash unity/run-tests.sh"
    echo "  Linux 常见路径: ~/Unity/Hub/Editor/2022.3.22f1/Editor/Unity"
    echo "  团结引擎: ~/Tuanjie/Hub/Editor/<版本>/Editor/Tuanjie"
    exit 1
fi

run_one() {
    local platform="$1" extra="$2"
    echo "=== $platform 测试(可能首次导入资产,请耐心) ==="
    "$UNITY_PATH" \
        -projectPath "$PROJ" \
        -runTests \
        -testPlatform "$platform" \
        -testResults "$OUT/$platform.xml" \
        -logFile "$OUT/$platform.log" \
        $extra \
        -quitOnTestFinished
    echo "结果: $OUT/$platform.xml"
    # 摘要(有 python 就解析)
    python3 - "$OUT/$platform.xml" << 'PY' 2>/dev/null || true
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
total, fails_, skips = r.get("total"), r.get("failures"), r.get("skipped")
print(f"  {total} 个测试, 失败 {fails_}, 跳过 {skips}")
for case in r.iter("test-case"):
    if case.get("result") == "Failed":
        print(f"  ✗ {case.get('fullname')}")
PY
}

case "$PLATFORM" in
    EditMode) run_one EditMode "-batchmode" ;;
    PlayMode) run_one PlayMode "" ;;
    All)
        run_one EditMode "-batchmode"
        run_one PlayMode ""
        ;;
    *) echo "未知平台: $PLATFORM (EditMode|PlayMode|All)"; exit 1 ;;
esac
echo "完成。日志在 $OUT/"
