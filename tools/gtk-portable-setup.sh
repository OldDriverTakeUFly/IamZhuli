#!/usr/bin/env bash
# 无sudo的Flutter Linux桌面工具链(便携gtk96包+运行库+clang18)
# 用法: bash tools/gtk-portable-setup.sh && source ~/gtk-portable/gtk-env.sh
set -e
D=~/gtk-portable; mkdir -p $D/debs $D/root; cd $D
apt-get install --print-uris -y cmake ninja-build libgtk-3-dev | grep "^'" | sed "s/^'//;s/' .*//" > urls.txt
xargs -P8 -I{} sh -c 'curl -sL -O --output-dir debs "{}"' < urls.txt
for p in libgtk-3-0t64 libgtk-3-common libgdk-pixbuf-2.0-0 libpango-1.0-0 libpangocairo-1.0-0 libpangoft2-1.0-0 libharfbuzz0b libatk1.0-0t64 libatk-bridge2.0-0t64 libcairo2 libcairo-gobject2 libepoxy0 libxkbcommon0 libwayland-client0 libwayland-cursor0 libwayland-egl1 libxtst6 libxi6 libglib2.0-0t64; do apt-get download $p >/dev/null 2>&1 || true; done
for d in *.deb debs/*.deb; do [ -f "$d" ] && dpkg -x "$d" root/ 2>/dev/null || true; done
cat > gtk-env.sh <<ENV
export PORTABLE=$D/root
export PATH=\$PORTABLE/usr/bin:~/clang-portable/root/usr/lib/llvm-18/bin:\$PATH
export CLR_CC=~/clang-portable/root/usr/lib/llvm-18/bin/clang
export LIBRARY_PATH=\$PORTABLE/usr/lib/x86_64-linux-gnu:~/clang-portable/root/usr/lib/x86_64-linux-gnu
export PKG_CONFIG_PATH=\$PORTABLE/usr/lib/x86_64-linux-gnu/pkgconfig:\$PORTABLE/usr/share/pkgconfig
export PKG_CONFIG_SYSROOT_DIR=\$PORTABLE
export LD_LIBRARY_PATH=\$PORTABLE/usr/lib/x86_64-linux-gnu:\$PORTABLE/usr/lib:~/clang-portable/root/usr/lib/x86_64-linux-gnu
ENV
cd ~/clang-portable && apt-get download libclang-common-18-dev >/dev/null 2>&1 && dpkg -x libclang-common-18-dev_*.deb root/ || true
echo "✓ source ~/gtk-portable/gtk-env.sh 后: cd tools/iamzhuli_flutter && flutter build linux --debug"
