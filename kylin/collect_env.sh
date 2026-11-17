#!/usr/bin/env bash
# ============================================================
# TypeBridge · UOS/统信 环境采集脚本（预研用，只读安全）
#
# 用途：拿到任何一台统信 UOS / deepin / 麒麟机器（实体机或虚拟机），
#       跑一下本脚本，把生成的 typebridge_env_report.txt 发回来，
#       即可远程判断该机器适配情况（缺什么依赖、走哪条注入通道）。
#
# 用法：bash collect_env.sh
# 产物：当前目录下 typebridge_env_report.txt
# 安全：全部为只读探测，唯一会产生动作的是最后的 --selfcheck（也不注入）。
# ============================================================

R="typebridge_env_report.txt"
OUT() { printf '%s\n' "$*" >> "$R"; }
HR()  { OUT "" "----------------------------------------"; }

: > "$R"
OUT "===== TypeBridge 环境采集报告 $(date '+%Y-%m-%d %H:%M:%S') ====="

HR; OUT "[1] 发行版"
OUT "os-release 内容："
cat /etc/os-release 2>/dev/null >> "$R" || OUT "(读不到 /etc/os-release)"
OUT "架构: $(dpkg --print-architecture 2>/dev/null || uname -m)"

HR; OUT "[2] 会话与桌面"
OUT "XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-（空）}"
OUT "XDG_CURRENT_DESKTOP=${XDG_CURRENT_DESKTOP:-（空）}"
OUT "DISPLAY=${DISPLAY:-（空）}  WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-（空）}"
SID=$(loginctl 2>/dev/null | awk -v u="$(whoami)" '$3==u{print $1; exit}')
[ -n "$SID" ] && OUT "loginctl 会话类型: $(loginctl show-session "$SID" -p Type 2>/dev/null | cut -d= -f2)"

HR; OUT "[3] Python"
OUT "$(python3 --version 2>&1 || echo '没找到 python3')"
OUT "路径: $(command -v python3 || echo 无)"

HR; OUT "[4] 依赖包可用性（apt 源里有没有、装没装）"
for p in xdotool xclip wl-clipboard zenity dotool ydotool; do
    OUT "--- $p ---"
    command -v "$p" >/dev/null 2>&1 && OUT "已安装: $(command -v $p)" || OUT "未安装"
    apt-cache policy "$p" 2>/dev/null | sed 's/^/  /' >> "$R" || OUT "  (apt-cache 不可用)"
done

HR; OUT "[5] uinput（dotool 通道前置条件）"
if [ -e /dev/uinput ]; then
    OUT "/dev/uinput 存在，权限: $(ls -l /dev/uinput 2>/dev/null | awk '{print $1, $3, $4}')"
    id | grep -q input && OUT "当前用户已在 input 组" || OUT "当前用户不在 input 组（dotool 需要加组）"
else
    OUT "/dev/uinput 不存在"
fi

HR; OUT "[6] 桌面集成探测"
OUT "xdg-user-dir DOWNLOAD: $(xdg-user-dir DOWNLOAD 2>/dev/null || echo '不可用')"
OUT "桌面目录: $(ls -d ~/桌面 ~/Desktop 2>/dev/null | tr '\n' ' ' || echo '未找到')"
OUT "zenity 弹窗: $(command -v zenity >/dev/null && echo 可用 || echo 不可用)"
OUT "pkexec 提权: $(command -v pkexec >/dev/null && echo 可用 || echo 不可用)"
OUT "gsettings(DDE/GNOME): $(command -v gsettings >/dev/null && echo 可用 || echo 不可用)"

HR; OUT "[7] 引擎自检（不注入；无引擎文件时跳过）"
HERE="$(cd "$(dirname "$0")" && pwd)"
PY=""
for cand in "$HERE/typebridge.py" "$HOME/.local/share/typebridge/typebridge.py" /usr/share/typebridge/typebridge.py; do
    [ -f "$cand" ] && { PY="$cand"; break; }
done
if [ -n "$PY" ]; then
    OUT "引擎: $PY"
    python3 "$PY" --selfcheck 2>&1 >> "$R"
else
    OUT "(本目录和常见安装位置没找到 typebridge.py，跳过自检；把整个 kylin 文件夹一起拷过来再跑一次更完整)"
fi

HR; OUT "采集完成。请把本文件(typebridge_env_report.txt)整个发回。"
echo "已完成：$(pwd)/$R"
