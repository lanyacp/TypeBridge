#!/usr/bin/env bash
# ============================================================
# TypeBridge Linux 版 · deb 打包脚本
#
# 产物：build/typebridge_<版本>_all.deb（Architecture: all，
#       一包通吃 AMD64 / ARM64(飞腾、鲲鹏) / LoongArch64(龙芯) 等架构）
# 用途：信创客户由 IT 从内部软件源统一推送（用户免安装操作）；
#       与脚本安装版(~/.local/share/typebridge)并存不冲突。
#
# 用法（需在 Linux/Debian 系上执行；Windows Git Bash 没有 dpkg-deb）：
#   bash build/pack_deb.sh               自动从引擎源码读版本号并打包
#   bash build/pack_deb.sh --stage-only  只生成目录结构不打包（核验用）
#
# 装完后的形态：
#   /usr/bin/typebridge                                  命令行入口
#   /usr/share/typebridge/                               引擎 + web 资源
#   /usr/share/applications/typebridge.desktop           开始菜单入口
#   /usr/share/icons/hicolor/scalable/apps/typebridge.svg
#
# 客户侧安装/卸载：
#   sudo apt install ./typebridge_x.y_all.deb
#   sudo apt remove typebridge
#
# 内网源（给客户 IT，可选）：
#   mkdir -p /srv/repo && cd /srv/repo
#   reprepro -b . includedeb stable /path/typebridge_x.y_all.deb
#   # 客户端源：
#   # echo "deb [trusted=yes] file:/srv/repo stable main" > /etc/apt/sources.list.d/typebridge.list
# ============================================================
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC_PY="$ROOT_DIR/kylin/typebridge.py"
SRC_WEB="$ROOT_DIR/kylin/web"
SRC_ICON="$ROOT_DIR/kylin/assets/typebridge-icon.svg"

[ -f "$SRC_PY" ] || { echo "!! 找不到 $SRC_PY"; exit 1; }

# 版本号：从引擎源码读 VERSION = "vX.Y"，deb 用纯数字
VER="$(sed -n 's/^VERSION *= *"v\{0,1\}\([^"]*\)".*/\1/p' "$SRC_PY" | head -n1)"
[ -n "$VER" ] || { echo "!! 未能从 typebridge.py 读到 VERSION"; exit 1; }

STAGE="$ROOT_DIR/build/debroot"
PKG="$STAGE/typebridge"
rm -rf "$STAGE"
mkdir -p "$PKG/DEBIAN" \
         "$PKG/usr/bin" \
         "$PKG/usr/share/typebridge" \
         "$PKG/usr/share/applications" \
         "$PKG/usr/share/icons/hicolor/scalable/apps"

# ---- 引擎与资源 ----
cp "$SRC_PY" "$PKG/usr/share/typebridge/typebridge.py"
chmod 0755 "$PKG/usr/share/typebridge/typebridge.py"
if [ -d "$SRC_WEB" ]; then
    cp -r "$SRC_WEB" "$PKG/usr/share/typebridge/web"
fi
if [ -f "$SRC_ICON" ]; then
    cp "$SRC_ICON" "$PKG/usr/share/icons/hicolor/scalable/apps/typebridge.svg"
fi

# ---- 命令行入口 wrapper ----
cat > "$PKG/usr/bin/typebridge" <<'EOF'
#!/bin/sh
# TypeBridge 入口：转交给系统 python3 执行引擎
exec python3 /usr/share/typebridge/typebridge.py "$@"
EOF
chmod 0755 "$PKG/usr/bin/typebridge"

# ---- DEBIAN/control ----
# Depends 只卡 python3；注入工具放 Recommends：缺工具也能装上，
# 引擎会照常启动，selfcheck 会指引用户补装（与产品"服务先起、通道自愈"的理念一致）
cat > "$PKG/DEBIAN/control" <<EOF
Package: typebridge
Version: $VER
Section: utils
Priority: optional
Architecture: all
Maintainer: lanyacp <lanyacp@noreply.gitee.com>
Depends: python3 (>= 3.6)
Recommends: xdotool, xclip, wl-clipboard, zenity
Homepage: https://gitee.com/lanyacp/TypeBridge
Description: TypeBridge 跨屏输入（手机打字/语音/传图直达电脑）
 手机扫码连接电脑，文字与语音输入直达电脑光标处；图片/文件一键传到电脑并自动粘贴。
 纯局域网通信，不经过任何服务器。
 支持 X11 与 Wayland 会话，适配银河麒麟 / 统信 UOS / 通用 Linux。
EOF

# ---- DEBIAN 维护脚本 ----
cat > "$PKG/DEBIAN/postinst" <<'EOF'
#!/bin/sh
echo "TypeBridge 安装完成：开始菜单搜索 TypeBridge 启动，或终端运行 typebridge --selfcheck 做环境自检。"
exit 0
EOF
cat > "$PKG/DEBIAN/postrm" <<'EOF'
#!/bin/sh
# 卸载时清理指向本包命令的用户级开机自启（只删 Exec 指向 /usr/bin/typebridge 的条目）
for f in /home/*/.config/autostart/typebridge.desktop; do
    [ -f "$f" ] || continue
    if grep -q "Exec.*=/usr/bin/typebridge" "$f" 2>/dev/null; then
        rm -f "$f"
    fi
done
exit 0
EOF
chmod 0755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/postrm"

# ---- 桌面入口（deb 版：系统目录）----
cat > "$PKG/usr/share/applications/typebridge.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=TypeBridge 跨屏输入
Comment=手机扫码，文字/图片/文件直达电脑
Exec=/usr/bin/typebridge --open-browser
Icon=typebridge
Terminal=false
Categories=Utility;
StartupNotify=false
EOF
chmod 0644 "$PKG/usr/share/applications/typebridge.desktop"

# ---- 打包 ----
if [ "${1:-}" = "--stage-only" ]; then
    echo "[stage-only] 目录结构已生成于 $PKG："
    find "$PKG" -type f | sort
    echo "[stage-only] 未调用 dpkg-deb（按要求跳过）。"
    exit 0
fi

if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "!! 没找到 dpkg-deb（需在 Linux/Debian 系上打包；Windows Git Bash 没有）。"
    echo "!! 可先用 --stage-only 核验目录结构，再到 Linux 机器上正式打包。"
    exit 1
fi

OUT="$ROOT_DIR/build/typebridge_${VER}_all.deb"
dpkg-deb --build --root-owner-group "$PKG" "$OUT"
echo "[OK] $OUT"
echo "校验: dpkg-deb -I \"$OUT\""
