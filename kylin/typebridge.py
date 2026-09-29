#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TypeBridge 麒麟版（Linux）
手机扫码 → 局域网 WebSocket → 把文字送进电脑前台输入框

设计原则与 Windows 版一致：只做管道，不做大脑。
不做语音识别、不做文字优化，识别全交给手机输入法，本程序只负责把字送到地方。

零第三方依赖，只用 Python 标准库（麒麟 V10 自带 Python 3 即可跑）。

用法：
    python3 typebridge.py                  启动服务（自动选择注入通道）
    python3 typebridge.py --selfcheck      环境与通道自检，不做任何注入
    python3 typebridge.py --test-inject    交互式注入实测（倒数 5 秒后往当前窗口打字）
    python3 typebridge.py --channel xdotool  强制指定通道
    python3 typebridge.py --open-browser   启动后自动打开配对页
    python3 typebridge.py --install-autostart    装开机自启
    python3 typebridge.py --uninstall-autostart  卸开机自启

注入通道（运行时自动挑选，不用手工配）：
    X11 会话        xdotool > dotool > clipboard
    Wayland 会话    dotool  > xdotool > clipboard   （dotool 走 uinput 内核层，对所有应用有效）
    ydotool         只能发美式键码，打不出中文，因此不进自动列表，
                    只在 --channel ydotool 明确指定时使用，或作为剪贴板通道的按键器

作者注：麒麟 V10 的会话类型各版本不一致，所以这里全部做运行时检测，
不写死任何一种通道。见 docs/麒麟系统适配可行性评估.md。
"""

import argparse
import base64
import hashlib
import json
import os
import random
import re
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time

# ============================================================
# 常量
# ============================================================

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
CONF_DIR = os.path.expanduser("~/.config/typebridge")
CONF_FILE = os.path.join(CONF_DIR, "config.json")
LOG_FILE = os.path.join(CONF_DIR, "log.txt")
WEB_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web")
AUTOSTART_DIR = os.path.expanduser("~/.config/autostart")

MAX_TEXT = 10000          # 单次最多 1 万字符
MAX_FRAME = 1024 * 1024   # WS 单帧上限 1MB
BATCH_CHARS = 500         # 键入类通道每批字符数（防单次调用超时）
KEY_BATCH = 200           # 按键类通道每批次数（防命令行参数超长）

# 配对页上的警示条内容。占位符在 pair.html 里故意写成 HTML 注释（<!--WARN-->），
# 这样 Windows 版不替换它的时候，页面上也不会露出 {{WARN}} 这种天书。
WARN_HTML = (
    '<div class="warn">'
    '电脑端还没有可用的文本注入通道，扫上台也送不进字。<br>'
    '请在电脑上运行 <code>python3 typebridge.py --selfcheck</code>，'
    '按提示把工具装好，再重启本程序。'
    '</div>'
)


# ============================================================
# 日志
# ============================================================

class Log(object):
    _lock = threading.Lock()

    @staticmethod
    def info(msg):
        line = "[%s] %s" % (time.strftime("%m-%d %H:%M:%S"), msg)
        with Log._lock:
            try:
                sys.stdout.write(line + "\n")
                sys.stdout.flush()
            except Exception:
                pass
            try:
                os.makedirs(CONF_DIR, exist_ok=True)
                if os.path.exists(LOG_FILE) and os.path.getsize(LOG_FILE) > 1024 * 1024:
                    os.remove(LOG_FILE)
                with open(LOG_FILE, "a", encoding="utf-8") as f:
                    f.write(line + "\n")
            except Exception:
                pass


# ============================================================
# 运行期状态
# ============================================================

class State(object):
    paused = False
    clients = 0
    lock = threading.Lock()


# ============================================================
# 配对码（持久化到 ~/.config/typebridge/config.json）
# ============================================================

class Pairing(object):
    _lock = threading.Lock()
    _code = ""

    @classmethod
    def load_or_new(cls):
        try:
            if os.path.exists(CONF_FILE):
                with open(CONF_FILE, encoding="utf-8") as f:
                    code = (json.load(f) or {}).get("pair_code", "")
                if isinstance(code, str) and re.match(r"^[0-9]{4}$", code):
                    with cls._lock:
                        cls._code = code
                    return code
        except Exception as e:
            Log.info("读取配置失败，将重新生成配对码: %s" % e)
        return cls.new_code()

    @classmethod
    def new_code(cls):
        with cls._lock:
            cls._code = "%04d" % random.randint(1000, 9999)
            cls._save()
            return cls._code

    @classmethod
    def _save(cls):
        try:
            os.makedirs(CONF_DIR, exist_ok=True)
            data = {}
            if os.path.exists(CONF_FILE):
                try:
                    with open(CONF_FILE, encoding="utf-8") as f:
                        data = json.load(f) or {}
                except Exception:
                    data = {}
            data["pair_code"] = cls._code
            with open(CONF_FILE, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            Log.info("保存配对码失败: %s" % e)

    @classmethod
    def code(cls):
        with cls._lock:
            return cls._code


# ============================================================
# 注入通道
# ============================================================

def _run(cmd, stdin_data=None, timeout=15):
    """统一的外部命令调用，永不抛异常，失败返回 (rc, out, err)。"""
    try:
        p = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE if stdin_data is not None else subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        try:
            out, err = p.communicate(input=stdin_data, timeout=timeout)
        except subprocess.TimeoutExpired:
            p.kill()
            out, err = p.communicate()
            return (-9, out or b"", b"timeout")
        return (p.returncode, out or b"", err or b"")
    except FileNotFoundError:
        return (-1, b"", b"command not found")
    except Exception as e:
        return (-2, b"", str(e).encode("utf-8", "replace"))


def _split_lines(text):
    """把文本按换行/制表切出来，这两类交给按键，其余整段键入。"""
    if not text:
        return []
    return re.split(r"([\n\t])", text)


class InjectError(Exception):
    """注入失败。抛出后由 WsSession 捕获，转成 error 消息回传手机端。

    存在的意义：注入通道在 Linux 上比 Windows 脆弱得多（权限被回收、
    X 会话断开、uinput 被占用都可能发生）。如果不把失败传回去，
    手机上会显示「已发送」，而电脑上什么都没打字出来，用户完全不知道为什么。
    """
    pass


class Injector(object):
    """注入通道基类。

    子类的 type_text / press / paste 约定：成功正常返回，失败抛 InjectError。
    """
    name = "base"
    can_type_text = True     # 能否直接键入文本（含中文）
    needs_uinput = False     # 是否依赖 uinput
    reason = ""              # 不可用时说明原因

    def type_text(self, text):
        raise NotImplementedError

    def press(self, key, count=1):
        raise NotImplementedError

    def paste(self):
        """模拟一次粘贴（Ctrl+V）。剪贴板通道需要。"""
        raise NotImplementedError

    def describe(self):
        return self.name


class XdotoolInjector(Injector):
    name = "xdotool"

    def __init__(self, exe):
        self.exe = exe

    @staticmethod
    def available():
        """判定标准是「真能连上 X」，不是「环境变量写着 x11」。

        原因：麒麟 V10 各 SP 的 XDG_SESSION_TYPE 时有时无，从自启脚本或
        su 切用户起的进程常常拿不到这个变量，按变量判会误杀一个本来能用的通道。
        反过来说，Wayland 会话里存在 XWayland 时，xdotool 对 XWayland 应用
        其实是可以工作的，也不该一刀切掉。
        """
        exe = shutil.which("xdotool")
        if not exe:
            return False, "未安装 xdotool"
        if not os.environ.get("DISPLAY"):
            # 这种情况几乎都发生在下面两处：从 ssh 登录跑的，或者被 systemd
            # 直接拉起来的。xdotool 靠 DISPLAY 找 X 服务器，没有就是真的用不了，
            # 所以这里不是误判，而是要把补救办法说清楚。
            return False, "DISPLAY 为空（本程序不在图形会话里。请回到麒麟桌面上，开个终端再运行）"
        rc, _, err = _run([exe, "getdisplaygeometry"], timeout=4)
        if rc == 0:
            return True, ""
        return False, "xdotool 连不上当前 X 显示（%s）" % (
            err.decode("utf-8", "replace").strip()[:60] or "rc=%s" % rc)

    def type_text(self, text):
        for part in _split_lines(text):
            if part == "\n":
                self.press("Enter")
            elif part == "\t":
                self.press("Tab")
            elif part:
                # xdotool type 原生支持 UTF-8，中文没问题。
                # 但 XTEST 逐字注入在部分应用/输入法下会丢字，
                # delay 调大一点能显著减少丢失（治本方案是剪贴板通道）。
                for i in range(0, len(part), BATCH_CHARS):
                    chunk = part[i:i + BATCH_CHARS]
                    self._do(["type", "--delay", "12", "--clearmodifiers", "--", chunk])
                    if i + BATCH_CHARS < len(part):
                        time.sleep(0.02)

    def press(self, key, count=1):
        kmap = {"Enter": "Return", "BackSpace": "BackSpace", "Tab": "Tab"}
        args = ["key"]
        if count > 1:
            args += ["--repeat", str(count), "--delay", "6"]
        args.append(kmap.get(key, key))
        self._do(args)

    def paste(self):
        self._do(["key", "--clearmodifiers", "ctrl+v"])

    def _do(self, args):
        rc, out, err = _run([self.exe] + args)
        if rc != 0:
            msg = err.decode("utf-8", "replace").strip() or ("rc=%s" % rc)
            Log.info("xdotool 返回 %s: %s" % (rc, msg))
            raise InjectError("xdotool %s" % msg[:80])


class DotoolInjector(Injector):
    name = "dotool"
    needs_uinput = True

    def __init__(self, exe):
        self.exe = exe

    @staticmethod
    def available():
        if not shutil.which("dotool"):
            return False, "未安装 dotool"
        if not os.path.exists("/dev/uinput"):
            return False, "/dev/uinput 不存在"
        if not os.access("/dev/uinput", os.W_OK):
            return False, "当前用户对 /dev/uinput 无写权限（需要加入 input 组）"
        return True, ""

    def type_text(self, text):
        for part in _split_lines(text):
            if part == "\n":
                self.press("Enter")
            elif part == "\t":
                self.press("Tab")
            elif part:
                for i in range(0, len(part), BATCH_CHARS):
                    chunk = part[i:i + BATCH_CHARS]
                    # 从 stdin 喂进去，避开命令行参数转义问题
                    self._do(["type"], stdin_data=chunk.encode("utf-8"))
                    if i + BATCH_CHARS < len(part):
                        time.sleep(0.01)

    def press(self, key, count=1):
        kmap = {"Enter": "enter", "BackSpace": "backspace", "Tab": "tab"}
        k = kmap.get(key, key)
        # dotool 支持从 stdin 读多条命令，一次调用完成一批按键，省去 fork 开销。
        # 必须分批：实时同步里删掉一大段文字会产生成百上千次退格，
        # 一次性拼成一整个脚本喂进去既不安全也没必要。
        left = max(1, count)
        while left > 0:
            n = min(left, KEY_BATCH)
            script = "key %s\n" % k * n
            self._do([], stdin_data=script.encode("utf-8"))
            left -= n

    def paste(self):
        self._do([], stdin_data=b"key ctrl+v\n")

    def _do(self, args, stdin_data=None):
        rc, out, err = _run([self.exe] + args, stdin_data=stdin_data)
        if rc != 0:
            msg = err.decode("utf-8", "replace").strip() or ("rc=%s" % rc)
            Log.info("dotool 返回 %s: %s" % (rc, msg))
            raise InjectError("dotool %s" % msg[:80])


class YdotoolInjector(Injector):
    """注意：ydotool 只会发美式键盘码，打不出中文。这里只用它做按键合成。"""
    name = "ydotool"
    can_type_text = False
    needs_uinput = True

    KEYCODE = {"Enter": 28, "BackSpace": 14, "Tab": 15, "ctrl": 29, "v": 47}

    def __init__(self, exe):
        self.exe = exe

    @staticmethod
    def available():
        if not shutil.which("ydotool"):
            return False, "未安装 ydotool"
        if not os.path.exists("/dev/uinput"):
            return False, "/dev/uinput 不存在"
        if not os.access("/dev/uinput", os.W_OK):
            return False, "当前用户对 /dev/uinput 无写权限（需要加入 input 组）"
        return True, ""

    def type_text(self, text):
        # 不声称支持打字，这里只把字母当按键发，中文会丢
        raise InjectError("ydotool 打不出中文，只用来发按键。请改用 dotool 或剪贴板通道")

    def press(self, key, count=1):
        code = self.KEYCODE.get(key)
        if code is None:
            raise InjectError("ydotool 不认识按键 %s" % key)
        left = max(1, count)
        while left > 0:
            # 分批的原因很实在：ydotool 的按键要写成命令行参数，
            # 一次上千个参数会直接超过系统的 ARG_MAX 上限而调用失败。
            n = min(left, KEY_BATCH)
            seq = []
            for _ in range(n):
                seq.append("%d:1" % code)
                seq.append("%d:0" % code)
            rc, out, err = _run([self.exe, "key"] + seq)
            if rc != 0:
                msg = err.decode("utf-8", "replace").strip() or ("rc=%s" % rc)
                Log.info("ydotool 返回 %s: %s" % (rc, msg))
                raise InjectError("ydotool %s" % msg[:80])
            left -= n

    def paste(self):
        rc, out, err = _run([self.exe, "key", "29:1", "47:1", "47:0", "29:0"])
        if rc != 0:
            msg = err.decode("utf-8", "replace").strip() or ("rc=%s" % rc)
            Log.info("ydotool 粘贴失败: %s" % msg)
            raise InjectError("ydotool %s" % msg[:80])


class ClipboardInjector(Injector):
    """剪贴板 + 模拟粘贴。中文绝对不出错，代价是覆盖剪贴板。"""
    name = "clipboard"

    def __init__(self, copy_cmd, presser):
        self.copy_cmd = copy_cmd        # (exe, [args])
        self.presser = presser          # 一个能 press / paste 的注入器

    @staticmethod
    def pick_copier():
        session = os.environ.get("XDG_SESSION_TYPE", "")
        if session == "wayland" and shutil.which("wl-copy"):
            return (shutil.which("wl-copy"), [])
        if shutil.which("xclip"):
            return (shutil.which("xclip"), ["-selection", "clipboard"])
        if shutil.which("xsel"):
            return (shutil.which("xsel"), ["-b", "-i"])
        if shutil.which("wl-copy"):
            return (shutil.which("wl-copy"), [])
        return None

    @staticmethod
    def pick_presser():
        """剪贴板方案需要一个能发 Ctrl+V 的家伙。"""
        ok, _ = XdotoolInjector.available()
        if ok:
            return XdotoolInjector(shutil.which("xdotool"))
        ok, _ = DotoolInjector.available()
        if ok:
            return DotoolInjector(shutil.which("dotool"))
        ok, _ = YdotoolInjector.available()
        if ok:
            return YdotoolInjector(shutil.which("ydotool"))
        return None

    @staticmethod
    def available():
        if ClipboardInjector.pick_copier() is None:
            return False, "缺少剪贴板工具（wl-clipboard / xclip / xsel 都没有）"
        if ClipboardInjector.pick_presser() is None:
            return False, "缺少模拟按键工具（xdotool / dotool / ydotool 都没有）"
        return True, ""

    def type_text(self, text):
        if not text:
            return
        exe, args = self.copy_cmd
        Log.info("剪贴板通道：写入 %d 字符（%s）" % (len(text), " ".join([exe] + args)))
        # 关键：xclip / xsel / wl-copy 写入后必须一直运行才能「持有」剪贴板内容，
        # 等它们退出必然超时（15s），注入也会跟着失败。
        # 所以这里 Popen 启动后只喂数据、不等待；旧的持有者会在被替换时自动退出。
        p = subprocess.Popen(
            [exe] + args,
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        try:
            p.stdin.write(text.encode("utf-8"))
            p.stdin.close()
        except (BrokenPipeError, OSError) as e:
            try:
                p.kill()
            except OSError:
                pass
            raise InjectError("写剪贴板失败（%s）" % e)
        # 等 X/Wayland 完成剪贴板所有权转移，再模拟粘贴；0.05s 太短会粘到旧内容
        time.sleep(0.3)
        # 工具如果立刻异常退出（而不是常驻持有），说明没写进去
        if p.poll() is not None and p.returncode != 0:
            raise InjectError("写剪贴板失败（工具异常退出 rc=%s）" % p.returncode)
        Log.info("剪贴板已就绪，模拟粘贴")
        # 注意：终端类窗口里 Ctrl+V 不是粘贴，得用 Ctrl+Shift+V。
        # 这是剪贴板通道的固有短板，无法在通用层面绕开。
        self.presser.paste()

    def press(self, key, count=1):
        self.presser.press(key, count)

    def paste(self):
        self.presser.paste()


# ============================================================
# 通道选择
# ============================================================

BUILDERS = [
    ("xdotool", XdotoolInjector),
    ("dotool", DotoolInjector),
    ("clipboard", ClipboardInjector),
    # ydotool 故意不在自动列表里：它打不出中文，只适合发按键。
    # 需要它请用 --channel ydotool 明确指定。
]

BY_NAME = dict(BUILDERS)
ALL_CHANNELS = "xdotool / dotool / clipboard / ydotool"


def describe_env():
    session = os.environ.get("XDG_SESSION_TYPE", "未设置")
    lines = [
        "会话类型 XDG_SESSION_TYPE = %s" % session,
        "桌面环境 XDG_CURRENT_DESKTOP = %s" % os.environ.get("XDG_CURRENT_DESKTOP", "未设置"),
        "DISPLAY = %s    WAYLAND_DISPLAY = %s" % (
            os.environ.get("DISPLAY", "（空）"), os.environ.get("WAYLAND_DISPLAY", "（空）")),
    ]
    return session, lines


def _make_channel(name):
    """按通道名构造注入器，环境不满足返回 None。

    必须先查 available() 再构造。少了这一步的后果很难看：
    注入器对象能建出来，但底层的工具压根没装，
    于是每次注入都失败，而选通道那一环看起来一切正常。
    """
    if name == "ydotool":
        ok, why = YdotoolInjector.available()
        if ok:
            return YdotoolInjector(shutil.which("ydotool"))
        return None
    cls = BY_NAME.get(name)
    if cls is None:
        return None
    ok, why = cls.available()
    if not ok:
        return None
    if name == "clipboard":
        return ClipboardInjector(ClipboardInjector.pick_copier(),
                                 ClipboardInjector.pick_presser())
    return cls(shutil.which(name))


def channel_order():
    """自动挑选的优先级。

    X11：clipboard > xdotool > dotool。理由：xdotool type 对中文用
    Unicode keysym 逐字发送，在 WPS / 部分 Qt 应用里会掉字（真机实测）；
    剪贴板整段粘贴 100% 完整，代价只是覆盖剪贴板。dotool 需要 uinput
    权限，放最后。
    Wayland：dotool > clipboard > xdotool（dotool 走内核 uinput 对所有
    应用有效；xdotool 只能管 XWayland 那部分；剪贴板是兜底）。
    """
    session = os.environ.get("XDG_SESSION_TYPE", "")
    if session == "wayland":
        return ["dotool", "clipboard", "xdotool"]
    return ["clipboard", "xdotool", "dotool"]


def build_injector(force=None):
    """挑选注入通道。force 为通道名时强制使用，否则按环境自动挑。"""
    if force:
        inj = _make_channel(force)
        if inj is None:
            if force in BY_NAME or force == "ydotool":
                cls = BY_NAME.get(force) or YdotoolInjector
                ok, why = cls.available()
                Log.info("强制指定 %s 但它不可用：%s" % (force, why))
            else:
                Log.info("未知通道名：%s（可选 %s）" % (force, ALL_CHANNELS))
            return None
        return inj

    for name in channel_order():
        inj = _make_channel(name)
        if inj is not None:
            return inj
    return None


def channel_report():
    """给自检用的：逐条通道报告可用性。不产生任何注入。"""
    session, env_lines = describe_env()
    rows = []
    for name in ("xdotool", "dotool", "clipboard", "ydotool"):
        cls = BY_NAME.get(name) or YdotoolInjector
        ok, why = cls.available()
        rows.append((name, ok, why))
    return session, env_lines, rows


# ============================================================
# 帧缓冲读取器（HTTP 头与 WS 帧共用，避免预读吃掉升级后的数据）
# ============================================================

class FrameReader(object):
    def __init__(self, sock):
        self.sock = sock
        self.buf = b""

    def read_exact(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("连接已关闭")
            self.buf += chunk
        out = self.buf[:n]
        self.buf = self.buf[n:]
        return out

    def read_byte(self):
        return self.read_exact(1)[0]


# ============================================================
# WebSocket 会话（手写 RFC6455，与 Windows 版协议完全一致）
# ============================================================

class WsSession(object):
    _current = None
    _lock = threading.Lock()

    def __init__(self, sock, reader, injector):
        self.sock = sock
        self.reader = reader
        self.injector = injector
        self.authed = False
        self.closed = False
        self.send_lock = threading.Lock()

    # ---- 会话互斥：新连接顶掉旧连接 ----
    @classmethod
    def set_current(cls, sess):
        with cls._lock:
            old = cls._current
            cls._current = sess
        if old is not None and old is not sess:
            Log.info("新连接接入，断开旧连接")
            old.kick()

    @classmethod
    def clear_current(cls, sess):
        with cls._lock:
            if cls._current is sess:
                cls._current = None

    # ---- 握手 ----
    def handshake(self, key):
        accept = base64.b64encode(
            hashlib.sha1((key + GUID).encode("ascii")).digest()
        ).decode("ascii")
        resp = (
            "HTTP/1.1 101 Switching Protocols\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"
        )
        self.sock.sendall(resp.encode("ascii"))

    def kick(self):
        self.closed = True
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        try:
            self.sock.close()
        except Exception:
            pass

    # ---- 帧读写 ----
    def send_frame(self, opcode, payload):
        if self.closed:
            return
        n = len(payload)
        header = bytearray()
        header.append(0x80 | opcode)
        if n < 126:
            header.append(n)
        elif n < 65536:
            header.append(126)
            header += struct.pack("!H", n)
        else:
            header.append(127)
            header += struct.pack("!Q", n)
        try:
            with self.send_lock:
                self.sock.sendall(bytes(header) + payload)
        except Exception:
            self.closed = True

    def send_json(self, obj):
        self.send_frame(0x1, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def send_error(self, msg):
        self.send_json({"type": "error", "msg": msg})

    def read_frame(self):
        b0 = self.reader.read_byte()
        b1 = self.reader.read_byte()
        opcode = b0 & 0x0F
        masked = (b1 & 0x80) != 0
        length = b1 & 0x7F
        if length == 126:
            length = struct.unpack("!H", self.reader.read_exact(2))[0]
        elif length == 127:
            length = struct.unpack("!Q", self.reader.read_exact(8))[0]
        if length > MAX_FRAME:
            raise ConnectionError("帧过大")
        mask = self.reader.read_exact(4) if masked else None
        data = self.reader.read_exact(length) if length else b""
        if mask:
            data = bytes(c ^ mask[i & 3] for i, c in enumerate(data))
        return opcode, data

    # ---- 主循环 ----
    def run(self):
        WsSession.set_current(self)
        try:
            while not self.closed:
                try:
                    opcode, payload = self.read_frame()
                except Exception:
                    break
                if opcode == 0x8:      # close
                    break
                if opcode == 0x9:      # ping -> pong
                    self.send_frame(0xA, payload)
                    continue
                if opcode == 0xA:      # pong
                    continue
                if opcode not in (0x1, 0x2):
                    continue
                try:
                    msg = json.loads(payload.decode("utf-8"))
                except Exception:
                    break
                if not self.handle(msg):
                    break
        finally:
            self.closed = True
            if self.authed:
                with State.lock:
                    State.clients = max(0, State.clients - 1)
            WsSession.clear_current(self)
            try:
                self.sock.close()
            except Exception:
                pass
            Log.info("连接断开")

    def handle(self, msg):
        """返回 False 表示应断开连接。"""
        if not isinstance(msg, dict):
            return False
        mtype = msg.get("type")

        if not self.authed:
            if mtype != "hello":
                self.send_error("请先配对")
                return False
            code = msg.get("code") or ""
            if code != Pairing.code():
                self.send_error("配对码错误")
                Log.info("配对失败，收到[%s] 期望[%s]" % (code, Pairing.code()))
                return False
            self.authed = True
            with State.lock:
                State.clients += 1
            self.send_json({"type": "ready"})
            Log.info("手机配对成功")
            return True

        if mtype == "text":
            text = msg.get("text") or ""
            if not text:
                return True
            if len(text) > MAX_TEXT:
                self.send_error("单次最多1万字符")
                return True
            if State.paused:
                self.send_error("注入已暂停")
                return True
            if self._inject(lambda: self.injector.type_text(text)):
                Log.info("注入 %d 字符" % len(text))
                self.send_ack(len(text))
            return True

        if mtype == "sync":
            # 实时同步：一次完成「退格 N 次 + 注入增量」
            if State.paused:
                return True
            text = msg.get("text") or ""
            try:
                back = int(msg.get("back") or 0)
            except Exception:
                back = 0
            back = max(0, min(back, MAX_TEXT))
            if len(text) > MAX_TEXT:
                self.send_error("单次最多1万字符")
                return True
            if not back and not text:
                self.send_ack(0)
                return True

            def _do_sync():
                if back:
                    self.injector.press("BackSpace", back)
                if text:
                    self.injector.type_text(text)

            if self._inject(_do_sync):
                Log.info("同步 退格%d 注入%d字符" % (back, len(text)))
                self.send_ack(len(text))
            return True

        if mtype == "enter":
            if State.paused:
                self.send_ack(0)
                return True
            if self._inject(lambda: self.injector.press("Enter")):
                self.send_ack(0)
            return True

        if mtype == "backspace":
            if not State.paused:
                try:
                    count = int(msg.get("count") or 1)
                except Exception:
                    count = 1
                count = max(1, min(count, MAX_TEXT))
                if self._inject(lambda: self.injector.press("BackSpace", count)):
                    self.send_ack(count)
                self.send_ack(count)
            return True

        if mtype == "ping":
            self.send_json({"type": "pong"})
            return True

        return True   # 未知消息忽略，不断开

    def _inject(self, fn):
        """统一的注入入口。返回 True 表示真的送进去了。

        没有这个统一入口的话，注入失败只会写进电脑本地的日志，
        而手机上照旧显示「已发送」，用户看到的是一台没反应的电脑和
        一个说「成功」的手机，根本无从判断哪里出了问题。
        """
        if self.injector is None:
            self.send_error("电脑端没有找到文本注入通道，请在电脑上运行 --selfcheck 查看")
            return False
        try:
            fn()
            return True
        except InjectError as e:
            Log.info("注入失败: %s" % e)
            self.send_error("注入失败：%s" % e)
            return False
        except Exception as e:
            Log.info("注入异常: %r" % e)
            self.send_error("注入异常：%s" % str(e)[:60])
            return False

    def send_ack(self, chars):
        self.send_json({
            "type": "ack",
            "ok": True,
            "chars": chars,
            "fg": foreground_title(),
        })


# ============================================================
# 前台窗口标题（Linux 下用 xdotool 或 xprop 取）
# ============================================================

def foreground_title():
    xdo = shutil.which("xdotool")
    if xdo and os.environ.get("DISPLAY"):
        rc, out, _ = _run([xdo, "getactivewindow", "getwindowname"], timeout=3)
        if rc == 0:
            return out.decode("utf-8", "replace").strip() or "(无标题窗口)"
    xprop = shutil.which("xprop")
    if xprop and os.environ.get("DISPLAY"):
        rc, out, _ = _run([xprop, "-root", "_NET_ACTIVE_WINDOW"], timeout=3)
        if rc == 0:
            wid = out.decode("utf-8", "replace").strip().split()[-1]
            if wid and wid != "0x0":
                rc2, out2, _ = _run([xprop, "-id", wid, "_NET_WM_NAME", "WM_NAME"], timeout=3)
                if rc2 == 0:
                    txt = out2.decode("utf-8", "replace").split("=", 1)
                    if len(txt) == 2:
                        return txt[1].strip().strip('"')
    return "(取不到窗口标题)"


# ============================================================
# 网卡 IP 探测
# ============================================================

def pick_ip():
    """用 UDP connect 让内核选出口网卡，不真的发包。"""
    for probe in ("8.8.8.8", "223.5.5.5"):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.settimeout(1)
            s.connect((probe, 80))
            ip = s.getsockname()[0]
            if ip and not ip.startswith("127."):
                return ip
        except Exception:
            pass
        finally:
            s.close()
    # 退路：从 ip 命令解析
    rc, out, _ = _run(["ip", "-4", "addr", "show", "scope", "global"], timeout=3)
    if rc == 0:
        for m in re.finditer(r"inet\s+([0-9.]+)/", out.decode("utf-8", "replace")):
            ip = m.group(1)
            if not ip.startswith("127."):
                return ip
    return None


def open_browser(url):
    """在麒麟桌面上打开默认浏览器。找不到浏览器就静默返回 False。"""
    for opener in ("xdg-open", "x-www-browser", "firefox", "chromium",
                   "chromium-browser", "google-chrome", "ukui-default-browser"):
        exe = shutil.which(opener)
        if exe:
            _run([exe, url], timeout=5)
            return True
    return False


def port_in_use(port):
    """探测本机端口是否已被占用。

    必须有这个检查：用户手动跑了一个实例、开机自启又拉起来一个的时候，
    第二个实例的 bind 会在线程里抛异常，而主线程照旧在 sleep 循环，
    表现出来的样子就是「程序在跑，但手机连不上」，还找不到原因。
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0
    except Exception:
        return False
    finally:
        s.close()


# ============================================================
# HTTP + WS 服务
# ============================================================

class Server(object):
    def __init__(self, port, injector):
        self.port = port
        self.injector = injector
        self.ready = False
        self.failed = False

    def serve_forever(self):
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            srv.bind(("0.0.0.0", self.port))
        except OSError as e:
            # 兜底。正常情况下 main 里的端口探测会先把这种情况拦住，
            # 但探测和 bind 之间有窗口期，抢到了就得说清楚，不能让进程假装在跑。
            self.failed = True
            Log.info("!! 端口 %d 绑定失败：%s" % (self.port, e))
            Log.info("!! 可能已经有另一个 TypeBridge 在跑了，试试 --port 换一个端口。")
            return
        srv.listen(16)
        self.ready = True
        Log.info("HTTP/WS 服务已监听 0.0.0.0:%d" % self.port)
        while True:
            try:
                sock, addr = srv.accept()
            except Exception as e:
                Log.info("accept 异常: %s" % e)
                continue
            t = threading.Thread(target=self.handle_client, args=(sock, addr))
            t.daemon = True
            t.start()

    def handle_client(self, sock, addr):
        try:
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            reader = FrameReader(sock)

            # 逐字节读 HTTP 头，防止吃掉 WS 升级后的数据
            head = b""
            while not head.endswith(b"\r\n\r\n"):
                head += reader.read_exact(1)
                if len(head) > 65536:
                    sock.close()
                    return

            text = head.decode("latin-1")
            first = text.split("\r\n", 1)[0]
            parts = first.split(" ")
            if len(parts) < 2:
                sock.close()
                return
            method, path = parts[0], parts[1]

            # ---- WebSocket ----
            if path.startswith("/ws"):
                if method != "GET":
                    sock.close()
                    return
                key = None
                for line in text.split("\r\n")[1:]:
                    if ":" in line:
                        k, v = line.split(":", 1)
                        if k.strip().lower() == "sec-websocket-key":
                            key = v.strip()
                            break
                if not key:
                    sock.close()
                    return
                sess = WsSession(sock, reader, self.injector)
                sess.handshake(key)
                sess.run()
                return

            if method != "GET":
                self.write(sock, 405, "text/plain; charset=utf-8", "method not allowed")
                sock.close()
                return

            qs = ""
            if "?" in path:
                path, qs = path.split("?", 1)

            if path == "/":
                self.write(sock, 200, "text/html; charset=utf-8", self.asset("index.html"))
            elif path == "/pair":
                ip = pick_ip() or "127.0.0.1"
                url = "http://%s:%d/m?code=%s" % (ip, self.port, Pairing.code())
                html = self.asset("pair.html").replace("{{URL}}", url).replace("{{CODE}}", Pairing.code())
                html = html.replace("<!--WARN-->", WARN_HTML if self.injector is None else "")
                self.write(sock, 200, "text/html; charset=utf-8", html)
            elif path == "/m":
                self.write(sock, 200, "text/html; charset=utf-8", self.asset("mobile.html"))
            elif path == "/qrcode.js":
                self.write(sock, 200, "application/javascript; charset=utf-8", self.asset("qrcode.js"))
            else:
                self.write(sock, 404, "text/plain; charset=utf-8", "not found")
            sock.close()
        except Exception:
            try:
                sock.close()
            except Exception:
                pass

    @staticmethod
    def asset(name):
        try:
            with open(os.path.join(WEB_DIR, name), "r", encoding="utf-8") as f:
                return f.read()
        except Exception as e:
            return "<h1>资源缺失 %s: %s</h1>" % (name, e)

    @staticmethod
    def write(sock, code, ctype, body):
        data = body.encode("utf-8")
        reason = {200: "OK", 404: "Not Found", 405: "Method Not Allowed"}.get(code, "OK")
        head = (
            "HTTP/1.1 %d %s\r\n"
            "Content-Type: %s\r\n"
            "Content-Length: %d\r\n"
            "Cache-Control: no-store\r\n"
            "Connection: close\r\n\r\n" % (code, reason, ctype, len(data))
        )
        sock.sendall(head.encode("ascii") + data)


# ============================================================
# 开机自启（XDG autostart）
# ============================================================

def install_autostart(port):
    os.makedirs(AUTOSTART_DIR, exist_ok=True)
    path = os.path.join(AUTOSTART_DIR, "typebridge.desktop")
    script = os.path.abspath(__file__)
    # 带上 --open-browser：开机后服务在后台跑，用户看不到二维码，
    # 顺手把配对页弹出来，开机就能扫码。
    content = (
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=TypeBridge\n"
        "Comment=手机扫码跨屏输入\n"
        "Exec=%s %s --port %d --open-browser\n"
        "Terminal=false\n"
        "StartupNotify=false\n"
        "X-GNOME-Autostart-enabled=true\n" % (sys.executable, script, port)
    )
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)
    Log.info("已写入开机自启: %s" % path)
    Log.info("下次登录桌面时自动启动，并会弹出配对页。")
    Log.info("想取消就运行：python3 %s --uninstall-autostart" % script)


def uninstall_autostart():
    path = os.path.join(AUTOSTART_DIR, "typebridge.desktop")
    if os.path.exists(path):
        os.remove(path)
        Log.info("已移除开机自启: %s" % path)
    else:
        Log.info("开机自启未安装")


# ============================================================
# 自检 / 实测
# ============================================================

def selfcheck():
    print("=" * 54)
    print(" TypeBridge 麒麟版 自检（不做任何注入）")
    print("=" * 54)
    session, env_lines, rows = channel_report()
    for l in env_lines:
        print("  " + l)
    print()
    print("  注入通道可用性：")
    for name, ok, why in rows:
        mark = "可用  " if ok else "不可用"
        extra = "" if ok else "（%s）" % why
        print("    [%s] %-10s %s" % (mark, name, extra))
    print()
    injector = build_injector()
    if injector is None:
        print("  结论：没有找到可用的注入通道。")
        print()
        print("  按下面顺序试，每做完一条就重跑一次自检：")
        if session == "wayland":
            print("    1) 装 dotool（Wayland 下首选，内核层注入，对所有应用都有效）")
            print("       装完后把当前用户加进 input 组：")
            print("         sudo usermod -aG input $USER")
            print("       加组必须注销重新登录才生效，只重开终端不算。")
            print("    2) 装剪贴板工具兜底：sudo apt install wl-clipboard")
            print("       剪贴板通道还要有个能发 Ctrl+V 的工具（xdotool 或 dotool）")
            print("    3) 都不行就退回 X11 会话：")
            print("       编辑 /etc/gdm3/custom.conf，设 WaylandEnable=false")
            print("       然后 sudo systemctl restart gdm3，重新登录后再跑自检")
        else:
            print("    1) 装 xdotool（X11 下首选，免权限，不占剪贴板）")
            print("         sudo apt install xdotool")
            print("    2) 装剪贴板工具兜底：sudo apt install xclip")
            print("    3) 做完重跑一次自检确认")
        print()
        print("  如果这台机器是纯服务器版、根本没有图形桌面，那本程序用不上，")
        print("  它要往图形界面的输入框里送字。")
        return 1
    print("  结论：将使用通道 [%s]" % injector.name)
    print("  说明：%s" % channel_note(injector.name))
    if injector.name == "clipboard":
        print("  注意：剪贴板通道会覆盖你当前的剪贴板内容。")
    print()
    print("  下一步可跑  python3 typebridge.py --test-inject  做一次真实注入实测")
    return 0


def channel_note(name):
    return {
        "xdotool": "X11 原生键入，支持中文，免权限，不占用剪贴板",
        "dotool": "uinput 注入，支持中文，X11/Wayland 都可用，需要 input 组权限",
        "clipboard": "剪贴板加模拟粘贴，支持中文，全环境兜底，会覆盖剪贴板",
        "ydotool": "uinput 按键，不能输入中文，仅作键鼠模拟",
    }.get(name, "")


def test_inject(seconds=5, text=None):
    """交互式注入实测：倒数若干秒，让用户把光标停到目标输入框。"""
    injector = build_injector()
    if injector is None:
        print("没有可用的注入通道，先跑 --selfcheck 看看缺什么。")
        return 1
    sample = text or "TypeBridge 跨屏输入测试 123"
    print("将使用通道 [%s]" % injector.name)
    if injector.name == "clipboard":
        print("注意：这会覆盖你当前的剪贴板。")
    print()
    print("请在 %d 秒内把光标点到一个能输入文字的地方（记事本、浏览器输入框都行），" % seconds)
    print("不要停在终端里，终端对中文和快捷键的处理不一样。")
    print()
    for i in range(seconds, 0, -1):
        sys.stdout.write("\r  倒计时 %d ..." % i)
        sys.stdout.flush()
        time.sleep(1)
    sys.stdout.write("\r  开始注入      \n")
    try:
        injector.type_text(sample)
    except Exception as e:
        print("注入失败: %s" % e)
        return 2
    print()
    print("请回看那个输入框，如果出现 [%s]，这条通道就通了。" % sample)
    return 0


# ============================================================
# 入口
# ============================================================

def main():
    ap = argparse.ArgumentParser(description="TypeBridge 麒麟版（Linux）")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--channel", default=None,
                    help="强制指定注入通道: xdotool / dotool / clipboard / ydotool")
    ap.add_argument("--selfcheck", action="store_true", help="环境与通道自检，不做注入")
    ap.add_argument("--test-inject", action="store_true", help="交互式注入实测")
    ap.add_argument("--open-browser", action="store_true", help="启动后打开配对页")
    ap.add_argument("--install-autostart", action="store_true")
    ap.add_argument("--uninstall-autostart", action="store_true")
    args = ap.parse_args()

    if args.install_autostart:
        install_autostart(args.port)
        return 0
    if args.uninstall_autostart:
        uninstall_autostart()
        return 0
    if args.selfcheck:
        return selfcheck()
    if args.test_inject:
        return test_inject()

    injector = build_injector(force=args.channel)
    if injector is None:
        # 不退出。让服务照常起来，配对页上会挂一条红色警示，
        # 扫码的人当场就能看到「电脑那边还没准备好」，不用去翻日志。
        Log.info("!! 没找到可用的注入通道：服务照常启动，但手机发来的文字送不出去。")
        Log.info("!! 请在电脑上运行 python3 typebridge.py --selfcheck，按提示装好工具再重启本程序。")

    url = "http://localhost:%d/pair" % args.port

    if port_in_use(args.port):
        # 已经有一个在跑了。与其甩一句「地址被占用」让人发愣，
        # 不如直接把那个实例的配对页打开，用户要的就是那张二维码。
        Log.info("端口 %d 已经有人在监听，多半是 TypeBridge 本身已经在跑了。" % args.port)
        Log.info("直接给你打开它的配对页：%s" % url)
        if not open_browser(url):
            Log.info("没能自动打开浏览器，请手动访问上面的地址。")
        return 0

    code = Pairing.load_or_new()
    ip = pick_ip()
    if not ip:
        Log.info("未找到可用的局域网 IP，请检查网络连接")
        return 1

    Log.info("========== TypeBridge 麒麟版 启动 ==========")
    Log.info("本机局域网 IP: %s" % ip)
    Log.info("配对码: %s" % code)
    if injector is None:
        Log.info("注入通道: 无（未就绪）")
    else:
        Log.info("注入通道: %s（%s）" % (injector.name, channel_note(injector.name)))
        if injector.name == "clipboard":
            Log.info("提醒：剪贴板通道会覆盖剪贴板内容，且终端里要用 Ctrl+Shift+V 才粘贴")
    Log.info("手机访问地址: http://%s:%d/m?code=%s" % (ip, args.port, code))

    srv = Server(args.port, injector)
    t = threading.Thread(target=srv.serve_forever)
    t.daemon = True
    t.start()

    for _ in range(30):
        if srv.ready or srv.failed:
            break
        time.sleep(0.1)
    if srv.failed:
        return 1

    if args.open_browser and not open_browser(url):
        Log.info("没能自动打开浏览器，请手动访问 %s" % url)

    Log.info("服务运行中。按 Ctrl+C 退出。")
    Log.info("电脑上打开 %s 即可扫码" % url)
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        Log.info("收到中断，退出")


if __name__ == "__main__":
    sys.exit(main())
