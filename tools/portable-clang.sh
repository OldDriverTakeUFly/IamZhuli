#!/usr/bin/env bash
# 便携 clang+zlib(无sudo环境下的 NativeAOT 链接工具链)
# 用法: bash tools/portable-clang.sh && source tools/portable-clang-env.sh
set -e
DIR=~/clang-portable
mkdir -p $DIR && cd $DIR
apt-get download clang clang-18 libclang-cpp18 libllvm18 zlib1g-dev
for d in clang clang-18 libclang-cpp18 libllvm18 zlib1g-dev; do dpkg -x ${d}_*.deb root/; done
cat > portable-clang-env.sh << ENV
export PATH=$DIR/root/usr/lib/llvm-18/bin:\$PATH
export CLR_CC=$DIR/root/usr/lib/llvm-18/bin/clang
export LIBRARY_PATH=$DIR/root/usr/lib/x86_64-linux-gnu
ENV
echo "✓ 完成: source $DIR/portable-clang-env.sh 后 dotnet publish src/IamZhuli.Native -c Release -r linux-x64"
