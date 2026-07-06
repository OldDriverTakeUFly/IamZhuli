#!/bin/bash
# 构建逻辑层 netstandard2.1 DLL 并复制到 Unity Plugins 目录
# 用法: 在项目根目录执行 bash unity/build-dlls.sh
set -e

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PLUGINS="$ROOT/unity/IamZhuli.Unity/Assets/Plugins"

echo "=== 构建逻辑层 netstandard2.1 ==="
cd "$ROOT"
for proj in IamZhuli.Core IamZhuli.Engine IamZhuli.Factors IamZhuli.Simulation; do
    echo "构建 $proj ..."
    dotnet build "src/$proj" -f netstandard2.1 -c Release --nologo -v q
done

echo ""
echo "=== 复制 DLL 到 Unity Plugins ==="
mkdir -p "$PLUGINS"
for proj in IamZhuli.Core IamZhuli.Engine IamZhuli.Factors IamZhuli.Simulation; do
    src=$(find "$ROOT/src/$proj/bin/Release/netstandard2.1" -name "$proj.dll" | head -1)
    if [ -z "$src" ]; then
        echo "✗ 找不到 $proj.dll"
        exit 1
    fi
    cp "$src" "$PLUGINS/"
    echo "✓ $proj.dll → Plugins/"
done

echo ""
echo "=== 完成 ==="
echo "DLL 已复制到: $PLUGINS"
echo "在 Unity 编辑器中刷新 Assets 即可使用"
