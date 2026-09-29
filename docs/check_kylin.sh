#!/bin/bash
# ============================================================
# TypeBridge 跨屏输入 · 麒麟 V10 环境检测脚本
#
# 用途：把本脚本拷到目标麒麟 V10 机器上跑一次，直接得出结论
#       这台机器该用哪种文本注入方案，以及可行性如何。
#
# 用法：bash check_kylin.sh
# 说明：第 1 到 5 项是纯只读检测，不会有任何副作用；
#       第 6 项会真的往你打开的窗口里打字，需要你确认后才执行。
# ============================================================

CN='跨屏输入测试 123'

echo "=================================================="
echo " TypeBridge 麒麟环境检测"
echo " $(date '+%Y-%m-%d %H:%M:%S')"
echo "=================================================="
echo

# ---------- 1. 系统与架构 ----------
echo "[1] 系统与架构"
if [ -f /etc/os-release ]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  echo "    发行版 : ${PRETTY_NAME:-未知}"
  echo "    版本号 : ${VERSION_ID:-未知}"
else
  echo "    发行版 : 读不到 /etc/os-release"
fi
ARCH=$(uname -m)
echo "    架构   : $ARCH"
echo "    内核   : $(uname -r)"
case "$ARCH" in
  x86_64)  echo "    CPU 平台：兆芯 / 海光 / Intel / AMD（x86_64）" ;;
  aarch64) echo "    CPU 平台：飞腾 / 鲲鹏（ARM64）" ;;
  loongarch64) echo "    CPU 平台：龙芯（LoongArch）" ;;
  mips64)  echo "    CPU 平台：龙芯旧架构（MIPS）" ;;
  *)       echo "    CPU 平台：$ARCH" ;;
esac
echo

# ---------- 2. 显示会话 ----------
echo "[2] 显示会话（决定注入方案的关键一项）"
SESSION="${XDG_SESSION_TYPE:-未设置}"
echo "    XDG_SESSION_TYPE = $SESSION"
if command -v loginctl >/dev/null 2>&1; then
  SID=$(loginctl 2>/dev/null | awk -v u="$(whoami)" '$3==u {print $1; exit}')
  if [ -n "$SID" ]; then
    T=$(loginctl show-session "$SID" -p Type 2>/dev/null | cut -d= -f2)
    [ -n "$T" ] && echo "    loginctl 会话类型 = $T"
  fi
fi
echo "    桌面环境 = ${XDG_CURRENT_DESKTOP:-未设置}"
echo "    DISPLAY = ${DISPLAY:-（空）}   WAYLAND_DISPLAY = ${WAYLAND_DISPLAY:-（空）}"

if [ "$SESSION" = "x11" ]; then
  echo "    >>> 结论：X11 会话。xdotool 可用，这是最省事的一条路。"
elif [ "$SESSION" = "wayland" ]; then
  echo "    >>> 结论：Wayland 会话。xdotool 失效，需要走 uinput 类工具或剪贴板方案。"
else
  echo "    >>> 无法判定会话类型。如果你是在终端里跑的，请到图形界面的终端里再跑一次。"
fi

# 能否切回 X11
if [ -f /etc/gdm3/custom.conf ]; then
  echo "    退路：检测到 /etc/gdm3/custom.conf"
  echo "          可取消注释 WaylandEnable=false 并 sudo systemctl restart gdm3，"
  echo "          把登录会话切回 X11，之后 xdotool 就能用（需要管理员权限）。"
else
  echo "    退路：未检测到 gdm3 配置，切换 X11 会话的可行性需现场确认。"
fi
echo

# ---------- 3. 可用工具 ----------
echo "[3] 已安装的相关工具"
FOUND=""
for c in xdotool wl-copy xclip xsel ydotool dotool eitype wtype; do
  if command -v "$c" >/dev/null 2>&1; then
    echo "    [有] $c  ->  $(command -v "$c")"
    FOUND="$FOUND $c"
  else
    echo "    [无] $c"
  fi
done
[ -z "$FOUND" ] && echo "    （一个都没有，需要先装）"
echo

# ---------- 4. uinput 权限 ----------
echo "[4] uinput 设备（uinput 类方案的前提，如 ydotool / dotool）"
if [ -e /dev/uinput ]; then
  echo "    [有] /dev/uinput"
  ls -l /dev/uinput | sed 's/^/         /'
  if id -nG "$(whoami)" 2>/dev/null | tr ' ' '\n' | grep -qx input; then
    echo "    [OK] 当前用户已在 input 组"
  else
    echo "    [缺] 当前用户不在 input 组，uinput 类工具会因权限失败"
    echo "         修复：sudo usermod -aG input $(whoami)   然后注销重新登录"
  fi
else
  echo "    [无] /dev/uinput 不存在，uinput 类方案不可用"
fi
if [ -S /tmp/.ydotool_socket ] || [ -S /run/user/$(id -u)/.ydotool_socket ]; then
  echo "    [OK] 检测到 ydotoold 守护进程的 socket（ydotoold 已在跑）"
else
  echo "    [--] 未发现 ydotoold socket（用 ydotool 需要先起 ydotoold）"
fi
echo

# ---------- 5. 软件源能不能装 ----------
echo "[5] 软件源可用性（判断能不能装得上）"
if command -v apt >/dev/null 2>&1; then
  echo "    包管理器：apt（Debian 系，麒麟桌面版是这一系）"
  for p in xdotool wl-clipboard xclip ydotool; do
    CAND=$(apt-cache policy "$p" 2>/dev/null | awk '/Candidate:/{print $2}')
    if [ -n "$CAND" ] && [ "$CAND" != "(none)" ]; then
      echo "    [可装] $p   候选版本 $CAND"
    else
      echo "    [源里没有] $p"
    fi
  done
else
  echo "    未找到 apt（可能是 RPM 系，需要改用 yum/dnf 思路）"
fi
echo

# ---------- 6. 注入实测 ----------
echo "[6] 注入实测（会真的打字到你当前窗口）"
echo "    这一步是整份检测里最有价值的一项。"
printf "    现在做实测吗？(y/N) "
read -r ANS
if [ "$ANS" = "y" ] || [ "$ANS" = "Y" ]; then
  echo
  echo "    请在 8 秒内打开任意能输入文字的地方（文本编辑器、浏览器地址栏之外"
  echo "    的输入框都行，不要用终端，终端里中文和快捷键行为不一样），"
  echo "    把光标停在输入区，然后不要碰鼠标键盘。"
  echo
  for i in 8 7 6 5 4 3 2 1; do
    printf "\r    倒计时 %d ..." "$i"
    sleep 1
  done
  printf "\r    开始注入      \n"
  echo

  TRIED=0
  if command -v xdotool >/dev/null 2>&1; then
    echo "    -> 用 xdotool type 打一句话"
    xdotool type --delay 80 "$CN" 2>&1 | sed 's/^/       /'
    TRIED=1
  fi
  if command -v ydotool >/dev/null 2>&1; then
    echo "    -> 用 ydotool type 打一句话（提醒：它发的是美式键码，中文可能出不来）"
    ydotool type "$CN" 2>&1 | sed 's/^/       /'
    TRIED=1
  fi
  if command -v dotool >/dev/null 2>&1; then
    echo "    -> 用 dotool 打一句话"
    dotool type "$CN" 2>&1 | sed 's/^/       /'
    TRIED=1
  fi
  if command -v wtype >/dev/null 2>&1; then
    echo "    -> 用 wtype 打一句话"
    wtype "$CN" 2>&1 | sed 's/^/       /'
    TRIED=1
  fi

  if [ $TRIED -eq 0 ]; then
    echo "    -> 没有任何键入类工具可用，跳过键入实测"
  fi

  # 剪贴板测试
  if command -v wl-copy >/dev/null 2>&1; then
    printf '%s' "$CN" | wl-copy 2>/dev/null && \
      echo "    -> 已把测试文字放进剪贴板（请手动按一次粘贴快捷键试试）"
  elif command -v xclip >/dev/null 2>&1; then
    printf '%s' "$CN" | xclip -selection clipboard 2>/dev/null && \
      echo "    -> 已把测试文字放进剪贴板（请手动按一次粘贴快捷键试试）"
  elif command -v xsel >/dev/null 2>&1; then
    printf '%s' "$CN" | xsel -b 2>/dev/null && \
      echo "    -> 已把测试文字放进剪贴板（请手动按一次粘贴快捷键试试）"
  fi

  echo
  echo "    请回看刚才那个输入框："
  echo "      · 出现 [跨屏输入测试 123]        -> 键入通道可用"
  echo "      · 出现乱码或问号                 -> 通道能打字但不支持中文"
  echo "      · 什么都没出现                   -> 这条通道不通"
  echo "      · 手动粘贴出了这句中文           -> 剪贴板通道可用"
fi
echo

# ---------- 汇总 ----------
echo "=================================================="
echo " 结论"
echo "=================================================="

HAS_XDOTOOL=0; command -v xdotool >/dev/null 2>&1 && HAS_XDOTOOL=1
HAS_WLCOPY=0;  command -v wl-copy  >/dev/null 2>&1 && HAS_WLCOPY=1
HAS_XCLIP=0;   command -v xclip    >/dev/null 2>&1 && HAS_XCLIP=1
IN_INPUT_GRP=0
id -nG "$(whoami)" 2>/dev/null | tr ' ' '\n' | grep -qx input && IN_INPUT_GRP=1

if [ "$SESSION" = "x11" ]; then
  if [ $HAS_XDOTOOL -eq 1 ]; then
    echo "推荐方案 A（X11 + xdotool）：可行，这是最优组合。"
    echo "  xdotool 原生支持中文，不需要特殊权限，直接键入不占剪贴板。"
  else
    echo "推荐方案 A（X11 + xdotool）：会话是 X11，装上 xdotool 即可。"
    echo "  执行：sudo apt install xdotool"
  fi
elif [ "$SESSION" = "wayland" ]; then
  echo "推荐方案 B（Wayland + 剪贴板）：这是目前最稳的一条路。"
  echo "  做法：中文写进剪贴板，再模拟一次粘贴快捷键。"
  if [ $IN_INPUT_GRP -eq 1 ]; then
    echo "  uinput 权限已具备，模拟粘贴可以走 ydotool 或 dotool。"
  else
    echo "  注意：模拟粘贴需要 uinput 权限，当前用户还没加入 input 组，"
    echo "       需要管理员执行：sudo usermod -aG input $(whoami)  再注销重登。"
  fi
  echo "  备选：如果允许改系统配置，可把会话切回 X11（WaylandEnable=false），"
  echo "       之后就能直接用 xdotool，体验最好。"
else
  echo "会话类型没测出来，请在图形界面的终端里重新跑一次本脚本。"
fi

if [ $HAS_WLCOPY -eq 0 ] && [ $HAS_XCLIP -eq 0 ]; then
  echo
  echo "提示：没看到剪贴板工具。"
  echo "  Wayland 下装 wl-clipboard：sudo apt install wl-clipboard"
  echo "  X11   下装 xclip       ：sudo apt install xclip"
fi

echo
echo "把上面这一整段输出发回给我，我就能给出确定的落地方案。"
echo "=================================================="
