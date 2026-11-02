// ============================================================
// TypeBridge 电脑端主程序 v0.4
// 手机浏览器打字/传图 → 局域网 WebSocket → 本程序注入电脑前台输入框
// 语法：C# 5（系统自带 csc.exe 编译，禁用 C#6+ 语法）
// 编译：build\build_stage1.bat （/target:winexe 纯托盘，无控制台窗口）
// 运行：build\TypeBridge.exe   右下角托盘常驻，日志写本地文件
//       已开开机自启 → 静默启动，不弹窗；右键托盘图标可弹扫码窗
// 托盘菜单：状态行 / 版本号 / 获取更新 / 选择网络 / 扫码窗 / 配对码
//           / 暂停注入 / 开机自启 / 日志 / 接收文件夹 / 最近接收
// 新能力（合并自 AirType）：手机传文件/图片到电脑（图片自动粘贴到光标处，
//   纯 Win32 剪贴板防卡死）、扫码页状态轮询与远程控制（断开/重配对/
//   打开接收目录）、批量退格撤回护栏、WS 分片重组（微信 XWeb）、深色手机端
// 设计要点（对应方案 docs/跨屏输入工具-实施方案.md）：
//   - TcpListener 裸 Socket（坑：HttpListener 绑局域网IP要管理员，见方案8.7）
//   - 手写 RFC6455 WebSocket（SHA1 内置，零第三方库）
//   - 配对码首帧校验，未配对连接不接受任何注入指令
//   - 注入前三重校验在手机端目标不可知，此处注入当前前台（产品定义）
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TypeBridge
{
    // ---------------- 日志（v0.2 起写本地文件，winexe 无控制台） ----------------
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeBridge");
        private static readonly string FilePath = Path.Combine(Dir, "log.txt");

        internal static void Info(string msg)
        {
            lock (Gate)
            {
                string line = "[" + DateTime.Now.ToString("MM-dd HH:mm:ss") + "] " + msg;
                Console.WriteLine(line);
                try
                {
                    Directory.CreateDirectory(Dir);
                    FileInfo fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete(); // 超 1MB 重开
                    using (StreamWriter w = File.AppendText(FilePath)) { w.WriteLine(line); }
                }
                catch (Exception) { }
            }
        }

        internal static void OpenFile()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "");
                Process.Start(FilePath);
            }
            catch (Exception) { }
        }
    }

    // ---------------- 注入引擎（阶段0已验收的同源代码） ----------------
    internal static class Inject
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_BACK = 0x08;
        private const ushort VK_TAB = 0x09;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool WaitForInputIdle(IntPtr hProcess, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static readonly int SizeOfInput = Marshal.SizeOf(typeof(INPUT));

        private static void AppendChar(INPUT[] arr, ref int idx, ushort code, bool keyUp)
        {
            arr[idx].type = INPUT_KEYBOARD;
            arr[idx].u.ki.wVk = 0;
            arr[idx].u.ki.wScan = code;
            arr[idx].u.ki.dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0);
            arr[idx].u.ki.time = 0;
            arr[idx].u.ki.dwExtraInfo = IntPtr.Zero;
            idx++;
        }

        private static INPUT[] BuildTextInputs(string text)
        {
            byte[] raw = Encoding.Unicode.GetBytes(text);
            int units = raw.Length / 2;
            INPUT[] inputs = new INPUT[units * 2];
            int idx = 0;
            for (int i = 0; i < units; i++)
            {
                ushort code = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                AppendChar(inputs, ref idx, code, false);
                AppendChar(inputs, ref idx, code, true);
            }
            return inputs;
        }

        private static INPUT[] BuildKeyInputs(ushort vk, int count)
        {
            INPUT[] inputs = new INPUT[count * 2];
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                for (int phase = 0; phase < 2; phase++)
                {
                    inputs[idx].type = INPUT_KEYBOARD;
                    inputs[idx].u.ki.wVk = vk;
                    inputs[idx].u.ki.wScan = 0;
                    inputs[idx].u.ki.dwFlags = (phase == 1 ? KEYEVENTF_KEYUP : 0);
                    inputs[idx].u.ki.time = 0;
                    inputs[idx].u.ki.dwExtraInfo = IntPtr.Zero;
                    idx++;
                }
            }
            return inputs;
        }

        private static uint SendEvents(INPUT[] inputs)
        {
            return SendInput((uint)inputs.Length, inputs, SizeOfInput);
        }

        // 注入文字：\n 拆成回车键、\t 拆成 Tab 键，其余走 Unicode。
        // 返回实际发送的事件数。
        internal static uint Text(string text)
        {
            uint total = 0;
            StringBuilder buf = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r') continue;                     // \r\n 只留 \n
                if (ch == '\n')
                {
                    total += SendEvents(BuildTextInputs(buf.ToString()));
                    buf.Length = 0;
                    total += SendEvents(BuildKeyInputs(VK_RETURN, 1));
                    Thread.Sleep(5);
                }
                else if (ch == '\t')
                {
                    total += SendEvents(BuildTextInputs(buf.ToString()));
                    buf.Length = 0;
                    total += SendEvents(BuildKeyInputs(VK_TAB, 1));
                    Thread.Sleep(5);
                }
                else
                {
                    buf.Append(ch);
                    // 分批：缓冲超过 400 字符先发，防止单次数组过大
                    if (buf.Length >= 400)
                    {
                        total += SendEvents(BuildTextInputs(buf.ToString()));
                        buf.Length = 0;
                        Thread.Sleep(20);
                    }
                }
            }
            if (buf.Length > 0) total += SendEvents(BuildTextInputs(buf.ToString()));
            return total;
        }

        internal static uint Key(ushort vk, int count)
        {
            return SendEvents(BuildKeyInputs(vk, count));
        }

        private static INPUT MakeKey(ushort vk, bool up)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = 0;
            i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0u;
            i.u.ki.time = 0;
            i.u.ki.dwExtraInfo = IntPtr.Zero;
            return i;
        }

        // 组合键 Ctrl+V：按下/抬起分开送并留出间隔。
        // 单批瞬时注入的四个事件，在部分应用（Word/WPS 等）忙于处理剪贴板
        // 变更通知时会被整批丢弃，表现为"自动粘贴没发生，手工再按 Ctrl+V 才出图"。
        internal static void Paste()
        {
            SendEvents(new INPUT[] { MakeKey(VK_CONTROL, false) });
            Thread.Sleep(30);
            SendEvents(new INPUT[] { MakeKey(VK_V, false) });
            Thread.Sleep(30);
            SendEvents(new INPUT[] { MakeKey(VK_V, true) });
            Thread.Sleep(30);
            SendEvents(new INPUT[] { MakeKey(VK_CONTROL, true) });
        }

        // 等前台进程"输入空闲"再注入按键：目标应用正忙时（例如刚收到
        // WM_CLIPBOARDUPDATE 在查询剪贴板格式），合成按键可能被延迟或丢弃。
        // 最多等 ms 毫秒，超时也照常注入（尽力而为）。
        internal static void WaitForForegroundIdle(int ms)
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return;
                // PROCESS_QUERY_LIMITED_INFORMATION(0x1000) | SYNCHRONIZE(0x00100000)
                IntPtr h = OpenProcess(0x00101000u, false, pid);
                if (h == IntPtr.Zero) return;
                try { WaitForInputIdle(h, (uint)ms); }
                finally { CloseHandle(h); }
            }
            catch (Exception) { }
        }

        // 前台窗口是否属于本进程：贴进自己的扫码窗没有意义，调用方据此改走"只保存"
        internal static bool ForegroundIsSelf()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch (Exception) { return false; }
        }

        internal static string ForegroundTitle()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "(未知)";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            string proc = "unknown";
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch (Exception) { }
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            return proc + " - " + sb.ToString();
        }

        // 无害自检
        internal static int SelfCheck()
        {
            Console.WriteLine("TypeBridge 注入内核自检 (无注入)");
            Console.WriteLine(new string('=', 46));
            Console.WriteLine(string.Format("sizeof(INPUT) = {0} (x64 应为 40) {1}",
                SizeOfInput, SizeOfInput == 40 ? "OK" : "FAIL"));
            string sample = "跨屏输入测试 Hello 🎉";
            INPUT[] evs = BuildTextInputs(sample);
            bool codeOk = evs[0].u.ki.wScan == 0x8DE8;
            bool countOk = evs.Length == Encoding.Unicode.GetBytes(sample).Length;
            Console.WriteLine(string.Format("编码验证 '{0}': 事件数 {1} (应={2}) {3}",
                sample, evs.Length, Encoding.Unicode.GetBytes(sample).Length, countOk ? "OK" : "FAIL"));
            Console.WriteLine(string.Format("'跨'=0x{0:X4} {1}", evs[0].u.ki.wScan, codeOk ? "OK" : "FAIL"));
            Console.WriteLine(string.Format("前台窗口: {0}", ForegroundTitle()));
            Console.WriteLine((codeOk && countOk && SizeOfInput == 40) ? "[结论] 注入内核 OK" : "[结论] FAIL");
            return (codeOk && countOk && SizeOfInput == 40) ? 0 : 1;
        }
    }

    // ---------------- 全局状态 ----------------
    internal static class State
    {
        internal static bool Paused = false;
        internal static volatile int ClientCount = 0;
    }

    // ---------------- 剪贴板/粘贴网关 ----------------
    // 剪贴板需要 STA 线程，而剪贴板的 OLE/COM 调用内部会泵消息：
    // 若把粘贴委托排进 UI 线程消息队列，上一张还没贴完就会被重入取出下一张，
    // 两次粘贴互踩剪贴板（多图连发时只有第一张能贴上）。
    // 所以用专属 STA 工作线程+显式队列：线程级串行，重入泵不到自己的队列。
    internal static class PasteGate
    {
        private static Control gate;
        private static NotifyIcon tray;
        private static readonly object Q = new object();
        private static readonly Queue<Action> jobs = new Queue<Action>();
        private static Thread worker;

        internal static void Init(NotifyIcon icon)
        {
            tray = icon;
            gate = new Control();
            IntPtr h = gate.Handle;   // 提前创建句柄，气泡提示需要编组回 UI 线程
            worker = new Thread(delegate()
            {
                while (true)
                {
                    Action job = null;
                    lock (Q) { if (jobs.Count > 0) job = jobs.Dequeue(); }
                    if (job == null) { Thread.Sleep(100); continue; }
                    try { job(); }
                    catch (Exception ex) { Log.Info("粘贴任务异常: " + ex.Message); }
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);   // Clipboard 要求 STA，不必是 UI 线程
            worker.Start();
        }

        internal static void Begin(Action work)
        {
            if (worker != null) { lock (Q) { jobs.Enqueue(work); } return; }
            work();   // 理论上不会发生（托盘未建时还没人发图），兼容起见就地执行
        }

        internal static void Notify(string msg)
        {
            if (tray == null) return;
            Action a = delegate
            {
                try { tray.ShowBalloonTip(1500, "TypeBridge", msg, ToolTipIcon.Info); }
                catch (Exception) { }
            };
            if (gate != null && gate.IsHandleCreated) gate.BeginInvoke(a);
            else a();
        }
    }

    // ---------------- 原生剪贴板（纯 Win32，不经 OLE） ----------------
    // 背景：旧实现用 Clipboard.SetDataObject(dob, true)（内部 OleSetClipboard +
    // OleFlushClipboard）：把跨进程 COM 数据对象挂上剪贴板并当场渲染全部格式，
    // 微信/Word/WPS 等剪贴板监视程序会立即反向请求数据，而本程序的粘贴线程
    // 不泵消息，原图（大位图）时双方互相等待，前台窗口可卡死约 10 秒
    // （日志实测：2.5MB 原图从"接收完成"到"粘贴完成"耗时 11 秒，正常应 ~2 秒）。
    // 现改为一次 OpenClipboard 后直接 SetClipboardData 放入
    // CF_HDROP（文件引用）+ CF_DIB（位图）两个现成 HGLOBAL：全程同步、无 COM 回调。
    internal static class NativeClip
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        internal const uint CF_DIB = 8;
        internal const uint CF_UNICODETEXT = 13;
        internal const uint CF_HDROP = 15;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const int OpenRetries = 20;        // 微信等程序常短暂占用剪贴板，共重试约 1 秒
        private const int OpenRetryDelayMs = 50;
        private const long MaxFormatBytes = 64L * 1024 * 1024;   // 单格式备份上限

        private static bool Open()
        {
            for (int i = 0; i < OpenRetries; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(OpenRetryDelayMs);
            }
            return false;
        }

        private static IntPtr Alloc(byte[] data)
        {
            if (data == null || data.Length == 0) return IntPtr.Zero;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return IntPtr.Zero; }
            try { Marshal.Copy(data, 0, p, data.Length); }
            finally { GlobalUnlock(h); }
            return h;
        }

        // 清空剪贴板（恢复"原本为空"的备份用）
        internal static bool ClearAll()
        {
            if (!Open()) return false;
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
            return true;
        }

        // 放入剪贴板：formats 与 bytes 一一对应，一次开合全部放好。
        // 放入成功的 HGLOBAL 归系统所有；失败的当场释放。返回 false = 剪贴板一直被占用。
        internal static bool SetAll(List<KeyValuePair<uint, byte[]>> items)
        {
            if (!Open()) return false;
            try
            {
                EmptyClipboard();
                for (int i = 0; i < items.Count; i++)
                {
                    byte[] v = items[i].Value;
                    if (v == null || v.Length == 0) continue;
                    IntPtr h = Alloc(v);
                    if (h == IntPtr.Zero) continue;
                    if (SetClipboardData(items[i].Key, h) == IntPtr.Zero)
                        GlobalFree(h);   // 放入失败才需要自己释放
                }
            }
            finally { CloseClipboard(); }
            return true;
        }

        // 读剪贴板某 HGLOBAL 格式的字节副本（CF_DIB 对只有 CF_BITMAP 的剪贴板
        // 会被系统自动合成，同样读得到）；无此格式/超上限返回 null
        internal static byte[] GetBytes(uint format)
        {
            if (!Open()) return null;
            try
            {
                if (!IsClipboardFormatAvailable(format)) return null;
                IntPtr h = GetClipboardData(format);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try
                {
                    long size = (long)GlobalSize(h);
                    if (size <= 0 || size > MaxFormatBytes) return null;
                    byte[] buf = new byte[size];
                    Marshal.Copy(p, buf, 0, (int)size);
                    return buf;
                }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        // CF_HDROP 字节串：DROPFILES 头(20字节) + 双 NUL 结尾的宽字符路径列表
        internal static byte[] BuildHdrop(IList<string> paths)
        {
            string joined = "";
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths[i] == null) continue;
                joined += paths[i] + "\0";
            }
            joined += "\0";   // 列表结尾再补一个空串
            byte[] strBytes = Encoding.Unicode.GetBytes(joined);
            byte[] data = new byte[20 + strBytes.Length];
            data[0] = 20;    // DROPFILES.pFiles = 数据区偏移 20
            data[16] = 1;    // DROPFILES.fWide = 1（UTF-16 路径）
            Array.Copy(strBytes, 0, data, 20, strBytes.Length);
            return data;
        }

        // 解析 CF_HDROP 字节串里的路径列表
        internal static List<string> DropPaths(byte[] hdrop)
        {
            List<string> list = new List<string>();
            if (hdrop == null || hdrop.Length < 20) return list;
            int off = BitConverter.ToInt32(hdrop, 0);
            bool wide = BitConverter.ToInt32(hdrop, 16) != 0;
            if (off <= 0 || off >= hdrop.Length) return list;
            string s = wide
                ? Encoding.Unicode.GetString(hdrop, off, hdrop.Length - off)
                : Encoding.Default.GetString(hdrop, off, hdrop.Length - off);
            string[] parts = s.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0) list.Add(parts[i]);
            }
            return list;
        }

        // CF_DIB 字节串：GDI+ 存成 BMP 再剥掉 14 字节 BITMAPFILEHEADER
        internal static byte[] BuildDib(System.Drawing.Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                byte[] all = ms.ToArray();
                if (all.Length <= 14) return null;
                byte[] dib = new byte[all.Length - 14];
                Array.Copy(all, 14, dib, 0, dib.Length);
                return dib;
            }
        }
    }

    // ---------------- 文件接收与"拍完即贴" ----------------
    internal static class FileSink
    {
        internal const long MaxFile = 200L * 1024 * 1024;   // 单文件上限 200MB
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"Downloads\TypeBridge接收");

        private static string Sanitize(string name)
        {
            name = Path.GetFileName(name ?? "").Trim();
            if (name.Length == 0) name = "未命名";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 100)
            {
                string ext = Path.GetExtension(name);
                name = name.Substring(0, 100 - ext.Length) + ext;
            }
            return name;
        }

        // 在接收目录内生成不重名的落盘路径
        internal static string Prepare(string rawName)
        {
            Directory.CreateDirectory(Dir);
            string name = Sanitize(rawName);
            string path = Path.Combine(Dir, name);
            if (File.Exists(path))
            {
                string baseName = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                for (int n = 1; ; n++)
                {
                    path = Path.Combine(Dir, baseName + "(" + n + ")" + ext);
                    if (!File.Exists(path)) break;
                }
            }
            return path;
        }

        internal static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" ||
                   ext == ".bmp" || ext == ".webp";
        }

        // 剪贴板位图的长边上限：原图(未压缩)整张上剪贴板会让前台应用的
        // 剪贴板监视/粘贴渲染卡死数秒，这里做限制(与手机端智能压缩同思路)；
        // 磁盘上保存的文件仍是原图，不受此影响。想贴更大可调大该值（有卡顿风险）。
        // 经验值：2048 最稳；3000 粘贴略慢半秒；4032(手机原图)约 1~2 秒；5000+ 会卡。
        private const int MaxClipboardBitmapEdge = 3000;

        // 为剪贴板准备位图：解码失败(webp/heic 等)返回 null(只上文件引用)；
        // 长边超过上限时高质量降采样，避免大 DIB 拖垮前台应用
        private static System.Drawing.Bitmap LoadClipboardBitmap(string path)
        {
            System.Drawing.Bitmap src;
            try { src = new System.Drawing.Bitmap(path); }
            catch (Exception) { return null; }
            int w = src.Width, h = src.Height;
            int mx = Math.Max(w, h);
            if (mx <= MaxClipboardBitmapEdge) return src;
            try
            {
                double scale = (double)MaxClipboardBitmapEdge / mx;
                int nw = Math.Max(1, (int)Math.Round(w * scale));
                int nh = Math.Max(1, (int)Math.Round(h * scale));
                System.Drawing.Bitmap dst = new System.Drawing.Bitmap(nw, nh);
                try
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new System.Drawing.Rectangle(0, 0, nw, nh));
                    }
                    src.Dispose();
                    return dst;
                }
                catch (Exception)
                {
                    dst.Dispose();
                    return src;   // 降采样失败就用原图
                }
            }
            catch (Exception) { return src; }
        }

        // 在粘贴线程执行：备份剪贴板 → 图片以「文件引用+位图」双格式上剪贴板(纯 Win32)
        // → 等前台空闲 → Ctrl+V → 有条件恢复原剪贴板。
        // 目标应用兼容性各异：微信/资源管理器认文件引用，Word/老程序只认位图，两个都给让它各取所需；
        // 位图解不了（如 webp）时退化为只给文件引用。返回人话结果描述（给手机 note 和电脑气泡用）
        internal static string PasteToCursor(string path)
        {
            // 前台是我们自己的窗口(如扫码页)时贴了也白贴，直接提示换目标
            if (Inject.ForegroundIsSelf())
                return "当前前台是 TypeBridge 自己的窗口，请先点开要粘贴的目标窗口（文件已保存）";

            // 0) 备份原剪贴板：三个常见格式各留一份字节副本，恢复时原样放回
            byte[] oldDib = NativeClip.GetBytes(NativeClip.CF_DIB);
            byte[] oldDrop = NativeClip.GetBytes(NativeClip.CF_HDROP);
            byte[] oldText = NativeClip.GetBytes(NativeClip.CF_UNICODETEXT);

            // 1) 准备现成数据：文件引用必有；位图尽力（大图先降采样）
            byte[] dib = null;
            System.Drawing.Bitmap cb = LoadClipboardBitmap(path);
            if (cb != null)
            {
                try { dib = NativeClip.BuildDib(cb); }
                catch (Exception) { dib = null; }
                finally { cb.Dispose(); }
            }
            List<KeyValuePair<uint, byte[]>> items = new List<KeyValuePair<uint, byte[]>>();
            byte[] drop = NativeClip.BuildHdrop(new string[] { path });
            if (drop != null) items.Add(new KeyValuePair<uint, byte[]>(NativeClip.CF_HDROP, drop));
            if (dib != null) items.Add(new KeyValuePair<uint, byte[]>(NativeClip.CF_DIB, dib));

            // 2) 上剪贴板：同步完成，无 OLE 跨进程编排，不会与剪贴板监视程序互锁
            if (!NativeClip.SetAll(items))
                return "剪贴板被其他程序占用，未粘贴（文件已保存）";

            // 3) 等前台应用消化完剪贴板变更通知、回到输入空闲，再注入 Ctrl+V
            Thread.Sleep(180);
            Inject.WaitForForegroundIdle(1500);
            Inject.Paste();
            Thread.Sleep(900);   // 等目标应用把剪贴板读走（在专属粘贴线程上，不碰 UI）

            // 4) 剪贴板保护：剪贴板还是我们放的文件才恢复备份；
            //    期间用户复制了新东西（比如微信自动复制了图片）就不覆盖回去
            bool ours = false;
            List<string> nowPaths = NativeClip.DropPaths(NativeClip.GetBytes(NativeClip.CF_HDROP));
            for (int i = 0; i < nowPaths.Count; i++)
            {
                if (string.Equals(nowPaths[i], path, StringComparison.OrdinalIgnoreCase)) { ours = true; break; }
            }
            string tail = "";
            if (!ours)
            {
                tail = "（粘贴期间剪贴板被更新，未恢复原内容）";
            }
            else
            {
                List<KeyValuePair<uint, byte[]>> restore = new List<KeyValuePair<uint, byte[]>>();
                if (oldDib != null) restore.Add(new KeyValuePair<uint, byte[]>(NativeClip.CF_DIB, oldDib));
                if (oldDrop != null) restore.Add(new KeyValuePair<uint, byte[]>(NativeClip.CF_HDROP, oldDrop));
                if (oldText != null) restore.Add(new KeyValuePair<uint, byte[]>(NativeClip.CF_UNICODETEXT, oldText));
                if (restore.Count > 0) NativeClip.SetAll(restore);   // 尽力而为
                else NativeClip.ClearAll();                          // 原剪贴板本就是空的
            }
            Log.Info("已粘贴图片到前台: " + Path.GetFileName(path));
            return "已粘贴到当前光标处" + tail;
        }

        internal static string DirOf() { return Dir; }
    }

    // ---------------- 配对码 ----------------
    internal static class Pairing
    {
        private static readonly object Gate = new object();
        private static string code = "";
        private static Random rnd = new Random();

        internal static string Code
        {
            get { lock (Gate) { return code; } }
        }

        internal static string NewCode()
        {
            lock (Gate)
            {
                code = rnd.Next(1000, 10000).ToString();
                return code;
            }
        }
    }

    // ---------------- 版本与更新地址 ----------------
    internal static class AppInfo
    {
        internal const string Version = "v0.5";
        internal const string UpdateUrl = "https://gitee.com/lanyacp/TypeBridge";
        internal const string UpdateUrlText = "gitee.com/lanyacp/TypeBridge";
    }

    // ---------------- 用户配置（HKCU\Software\TypeBridge，免管理员） ----------------
    // 存两类东西：首次运行的询问是否已经做过、用户记住的网络选择。
    internal static class Config
    {
        private const string Key = @"Software\TypeBridge";

        internal static bool FirstRunDone()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("FirstRunDone");
                    return (v is int) && (int)v == 1;
                }
            }
            catch (Exception) { return false; }
        }

        internal static void SetFirstRunDone()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k != null) k.SetValue("FirstRunDone", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Info("写入配置失败: " + ex.Message); }
        }

        internal static string PreferredIp()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, false))
                {
                    if (k == null) return "";
                    return (k.GetValue("PreferredIp") as string) ?? "";
                }
            }
            catch (Exception) { return ""; }
        }

        internal static void SetPreferredIp(string ip)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k != null) k.SetValue("PreferredIp", ip ?? "", RegistryValueKind.String);
                }
            }
            catch (Exception ex) { Log.Info("保存网络选择失败: " + ex.Message); }
        }
    }

    // ---------------- 防火墙自检/自救 ----------------
    // 「手机扫码后打不开」的头号原因：首次运行时 Windows 弹的防火墙询问被点了取消，
    // 入站被拦——服务在跑，但手机的请求根本到不了电脑。这里做两件事：
    //   1) 启动时只读检查有没有针对本 exe 的生效入站放行规则（读 HKLM 注册表，免管理员）
    //   2) 托盘「一键修复」用提权 netsh 补一条覆盖全部网络类型(公用/专用/域)的放行规则
    internal static class FirewallGuard
    {
        private const string RuleName = "TypeBridgeAllow";
        private const string RulesKey =
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

        // 有没有生效的入站放行规则盖住本程序。读注册表失败时宁可返回 true（不误报打扰）。
        internal static bool CheckAllowed()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RulesKey, false))
                {
                    if (k == null) return true;
                    string exe = Application.ExecutablePath.ToLowerInvariant();
                    foreach (string name in k.GetValueNames())
                    {
                        string v = k.GetValue(name) as string;
                        if (v == null) continue;
                        string lv = v.ToLowerInvariant();
                        // 规则格式：v2.30|Action=Allow|Active=TRUE|Dir=In|App=C:\path\typebridge.exe|...
                        if (lv.IndexOf("|app=" + exe) < 0) continue;
                        if (lv.IndexOf("action=allow") < 0) continue;
                        if (lv.IndexOf("active=true") < 0) continue;
                        if (lv.IndexOf("dir=in") < 0) continue;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("防火墙规则检查失败(按已放行处理): " + ex.Message);
                return true;
            }
            return false;
        }

        // 提权补放行规则（UAC 点「是」后约 1 秒完成）。返回修复后是否检测到规则。
        // 用户在 UAC 框点了取消会抛异常，按未修复返回。
        internal static bool Fix()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                // 先删旧的同名规则再加新的，重复点修复也不会堆规则
                psi.Arguments = "/c netsh advfirewall firewall delete rule name=" + RuleName +
                    " >nul 2>&1 & netsh advfirewall firewall add rule name=" + RuleName +
                    " dir=in action=allow program=\"" + Application.ExecutablePath +
                    "\" profile=any enable=yes";
                psi.Verb = "runas";           // 弹 UAC 管理员确认框
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(30000);
            }
            catch (Exception ex)
            {
                Log.Info("防火墙放行未完成: " + ex.Message);   // 常见：UAC 被取消
                return false;
            }
            return CheckAllowed();
        }
    }

    // ---------------- 开机自启（HKCU Run，免管理员） ----------------
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TypeBridge";

        private static string ExePath
        {
            get { return Application.ExecutablePath; }
        }

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    if (v == null) return false;
                    string s = (string)v;
                    // 写入时包了一层引号（兼容路径带空格），比较前剥掉
                    if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                        s = s.Substring(1, s.Length - 2);
                    return string.Equals(s, ExePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { return false; }
        }

        // 返回 null=成功，否则为错误信息
        internal static string Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return "无法打开注册表启动项";
                    if (on) k.SetValue(ValueName, "\"" + ExePath + "\"", RegistryValueKind.String);
                    else k.DeleteValue(ValueName, false);
                }
                Log.Info("开机自启: " + (on ? "已开启" : "已关闭"));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ---------------- 网卡 IP 探测 ----------------
    // 机器上常有多个网络：WiFi、有线、代理的 TUN 虚拟网卡（Clash 等）。
    // 只自动挑一个不够用——默认路由常被代理网卡抢走，挑出来的 IP 手机
    // 根本连不上。所以列出全部候选：托盘菜单里让用户自己选（选择被记住），
    // 配对页上也列出备选地址。
    internal static class NetDetect
    {
        internal class Candidate
        {
            internal string Ip;
            internal string Kind;    // 无线 / 有线 / 网络
            internal string Nic;     // 网卡名
            internal string Label { get { return Kind + "·" + Nic; } }
        }

        // 当前对外服务的地址（Init / Use 里维护）
        private static Candidate current;

        internal static string CurrentIp()
        {
            return current == null ? null : current.Ip;
        }

        // 列出全部候选：已启用、非环回、IPv4、排除环回/APIPA，按 IP 去重
        internal static List<Candidate> ListCandidates()
        {
            List<Candidate> list = new List<Candidate>();
            List<string> seen = new List<string>();
            try
            {
                NetworkInterface[] nis = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface ni in nis)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string kind;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) kind = "无线";
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) kind = "有线";
                    else kind = "网络";
                    foreach (UnicastIPAddressInformation uai in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (uai.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = uai.Address.ToString();
                        if (s.StartsWith("169.254.") || s.StartsWith("127.")) continue;
                        if (seen.Contains(s)) continue;
                        seen.Add(s);
                        Candidate c = new Candidate();
                        c.Ip = s;
                        c.Kind = kind;
                        c.Nic = ni.Name;
                        list.Add(c);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("网卡探测失败: " + ex.Message);
            }
            return list;
        }

        // 自动挑选打分：无线 > 有线 > 其他；常见局域网网段（192.168. / 10. /
        // 172.16-31.）再加一分，避免在同类里挑中代理 TUN 的 198.18.x 这类地址。
        private static int Score(Candidate c)
        {
            int s;
            if (c.Kind == "无线") s = 3;
            else if (c.Kind == "有线") s = 2;
            else s = 1;
            if (c.Ip.StartsWith("192.168.") || c.Ip.StartsWith("10."))
            {
                s += 1;
            }
            else if (c.Ip.StartsWith("172."))
            {
                string[] seg = c.Ip.Split('.');
                int second;
                if (seg.Length > 1 && int.TryParse(seg[1], out second)
                    && second >= 16 && second <= 31) s += 1;
            }
            return s;
        }

        // 启动时定下对外地址：用户记住的选择还在就用它，否则自动挑
        internal static void Init(string preferred)
        {
            List<Candidate> list = ListCandidates();
            if (list.Count == 0) { current = null; return; }
            if (!string.IsNullOrEmpty(preferred))
            {
                foreach (Candidate c in list)
                {
                    if (c.Ip == preferred) { current = c; return; }
                }
                Log.Info("记住的网络地址 " + preferred + " 已不存在，自动重新选择");
            }
            Candidate best = null;
            int bestScore = -1;
            foreach (Candidate c in list)
            {
                int sc = Score(c);
                if (sc > bestScore) { bestScore = sc; best = c; }
            }
            current = best;
        }

        // 托盘菜单里用户手动切换；地址已失效返回 false
        internal static bool Use(string ip)
        {
            foreach (Candidate c in ListCandidates())
            {
                if (c.Ip == ip) { current = c; return true; }
            }
            return false;
        }

        // 网络守望用：当前地址已经不在任何网卡上（换了 WiFi / 网线拔了）时，
        // 自动切到可用地址。返回新地址；当前地址仍有效返回 null。
        // 笔记本换网是「二维码扫不开」的高发原因——二维码里印的还是旧网段的 IP。
        private static readonly object RepairGate = new object();
        internal static string AutoRepairCurrent()
        {
            lock (RepairGate)
            {
                List<Candidate> list = ListCandidates();
                if (list.Count == 0) return null;
                string cur = current == null ? null : current.Ip;
                foreach (Candidate c in list)
                {
                    if (c.Ip == cur) return null;   // 还在，不用动
                }
                // 先看用户记住的选择是否随网络回来了（比如切回常用的那个 WiFi）
                string preferred = Config.PreferredIp();
                if (!string.IsNullOrEmpty(preferred))
                {
                    foreach (Candidate c in list)
                    {
                        if (c.Ip == preferred) { current = c; return preferred; }
                    }
                }
                Candidate best = null;
                int bestScore = -1;
                foreach (Candidate c in list)
                {
                    int sc = Score(c);
                    if (sc > bestScore) { bestScore = sc; best = c; }
                }
                current = best;
                return best.Ip;
            }
        }

        // 配对页上的备选地址列表（单网卡返回空串，页面上什么都不显示）
        internal static string IpsHtml(int port)
        {
            List<Candidate> list = ListCandidates();
            if (list.Count < 2) return "";
            string cur = CurrentIp();
            StringBuilder sb = new StringBuilder();
            sb.Append("<div class=\"ips\"><div class=\"ipstip\">本机有多个网络地址，上面这个连不上就换一个，手机浏览器直接打开：</div>");
            foreach (Candidate c in list)
            {
                string u = "http://" + c.Ip + ":" + port + "/m?code=" + Pairing.Code;
                sb.Append("<code>").Append(u).Append("</code> ").Append(c.Nic)
                  .Append(c.Ip == cur ? " ←当前" : "").Append("<br>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }
    }

    // ---------------- 资源读取 ----------------
    internal static class Res
    {
        internal static string Get(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("TypeBridgeRes." + name))
            {
                if (s == null) return "<h1>资源缺失: " + name + "</h1>";
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    return r.ReadToEnd();
                }
            }
        }
    }

    // ---------------- 应用图标 ----------------
    // 从内嵌的 typebridge.ico 读取指定尺寸的图标帧，托盘(16)与扫码窗口(32)各取所需；
    // 读取失败回退系统默认图标，保证任何情况下都有图标可用。
    internal static class AppIcon
    {
        private static readonly Dictionary<int, System.Drawing.Icon> cache =
            new Dictionary<int, System.Drawing.Icon>();
        private static MemoryStream kept;   // Icon 依赖底层流，须全程保活不能释放

        internal static System.Drawing.Icon Get(int size)
        {
            System.Drawing.Icon cached;
            if (cache.TryGetValue(size, out cached)) return cached;
            System.Drawing.Icon icon = null;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("TypeBridgeRes.typebridge.ico"))
                {
                    if (s != null)
                    {
                        if (kept == null)
                        {
                            kept = new MemoryStream();
                            byte[] buf = new byte[8192];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) kept.Write(buf, 0, n);
                        }
                        kept.Position = 0;
                        icon = new System.Drawing.Icon(kept, new System.Drawing.Size(size, size));
                    }
                }
            }
            catch (Exception) { icon = null; }
            if (icon == null) icon = System.Drawing.SystemIcons.Application;
            cache[size] = icon;
            return icon;
        }
    }

    // ---------------- TinyHttp：TcpListener 极简 HTTP + WS 路由 ----------------
    internal static class TinyHttp
    {
        private static WsSession activeSession;
        private static readonly object SessionGate = new object();

        internal static Action ready;
        internal static Action<string> failed;
        internal static int BoundPort = 8765;   // 启动后回填，扫码页显示地址用

        internal static void Run(int port)
        {
            try
            {
                BoundPort = port;
                TcpListener listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                Log.Info("HTTP/WS 服务已监听 0.0.0.0:" + port);
                Action r = ready;
                if (r != null) r();
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object o) { Handle((TcpClient)o); }, client);
                }
            }
            catch (Exception ex)
            {
                Log.Info("服务异常退出: " + ex.Message);
                Action<string> f = failed;
                if (f != null) f(ex.Message);
            }
        }

        // 探测端口是否已被占用（用于单实例判断）
        internal static bool PortInUse(int port)
        {
            try
            {
                TcpClient c = new TcpClient();
                IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(300);
                bool connected = false;
                if (ok)
                {
                    try { c.EndConnect(ar); connected = c.Connected; }
                    catch (Exception) { connected = false; }
                }
                c.Close();
                return connected;
            }
            catch (Exception) { return false; }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                string request = ReadHeaders(stream);
                if (request == null) { client.Close(); return; }

                string firstLine = request.Split('\n')[0].Trim();
                string[] parts = firstLine.Split(' ');
                if (parts.Length < 2) { client.Close(); return; }
                string method = parts[0];
                string path = parts[1];

                if (path == "/ws")
                {
                    if (method != "GET") { client.Close(); return; }
                    WsSession ws = new WsSession(client, stream, request);
                    ws.Start();
                    return; // 会话自管理，不关闭 client
                }

                if (method != "GET")
                {
                    WriteResponse(stream, 405, "text/plain", "method not allowed");
                    client.Close();
                    return;
                }

                string qs = "";
                int qi = path.IndexOf('?');
                if (qi >= 0) { qs = path.Substring(qi + 1); path = path.Substring(0, qi); }

                // 扫码页/二维码资源只允许电脑本机访问：局域网里其他设备不应能看到配对信息
                bool local = false;
                try
                {
                    System.Net.IPEndPoint ep = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                    local = ep != null && System.Net.IPAddress.IsLoopback(ep.Address);
                }
                catch (Exception) { }

                switch (path)
                {
                    case "/":
                    case "/pair":
                    case "/qrcode.js":
                        if (!local)
                        {
                            WriteResponse(stream, 403, "text/plain; charset=utf-8", "此页面仅允许在电脑本机打开；手机请使用二维码里的链接");
                        }
                        else if (path == "/")
                        {
                            WriteResponse(stream, 200, "text/html; charset=utf-8", Res.Get("index.html"));
                        }
                        else if (path == "/qrcode.js")
                        {
                            WriteResponse(stream, 200, "application/javascript; charset=utf-8", Res.Get("qrcode.js"));
                        }
                        else
                        {
                            // 二维码指向"当前扫码地址"：托盘「选择网络」切换后刷新页面即跟随
                            string ip = NetDetect.CurrentIp() ?? "127.0.0.1";
                            string url = "http://" + ip + ":" + BoundPort + "/m?code=" + Pairing.Code;
                            string html = Res.Get("pair.html")
                                .Replace("{{URL}}", url)
                                .Replace("{{CODE}}", Pairing.Code)
                                .Replace("{{IP}}", ip)
                                .Replace("{{PORT}}", BoundPort.ToString())
                                .Replace("{{VER}}", AppInfo.Version)
                                .Replace("<!--IPS-->", NetDetect.IpsHtml(BoundPort));
                            WriteResponse(stream, 200, "text/html; charset=utf-8", html);
                        }
                        break;
                    case "/m":
                        NotePeerVisit(client);
                        WriteResponse(stream, 200, "text/html; charset=utf-8", Res.Get("mobile.html"));
                        break;
                    case "/status":
                    {
                        // 供扫码页轮询：只回答「有没有连接」和「页面上的码是否仍是当前码」，
                        // 不回传配对码本身（页面把自己看到的码带在查询串里比对）。
                        // ip/port 一起带回：电脑换了网络后扫码页据此自动刷新成新地址的二维码
                        bool match = false;
                        string[] kvs = qs.Split('&');
                        for (int i = 0; i < kvs.Length; i++)
                        {
                            if (kvs[i].Length > 2 && kvs[i].Substring(0, 2) == "c=")
                            {
                                match = Uri.UnescapeDataString(kvs[i].Substring(2)) == Pairing.Code;
                                break;
                            }
                        }
                        string json = "{\"connected\":" + (State.ClientCount > 0 ? "true" : "false")
                            + ",\"match\":" + (match ? "true" : "false")
                            + ",\"ip\":\"" + (NetDetect.CurrentIp() ?? "") + "\""
                            + ",\"port\":" + BoundPort + "}";
                        WriteResponse(stream, 200, "application/json; charset=utf-8", json);
                        break;
                    }
                    case "/ctrl":
                    {
                        // 扫码页上的控制按钮（断开手机/重新配对/打开接收文件夹）：仅本机可用，
                        // 且请求必须带页面当时看到的配对码授权，防止局域网其他设备或本机网页伪造
                        if (!local)
                        {
                            WriteResponse(stream, 403, "text/plain; charset=utf-8", "此接口仅允许在电脑本机调用");
                            break;
                        }
                        string cc = "", act = "";
                        string[] kvs2 = qs.Split('&');
                        for (int i = 0; i < kvs2.Length; i++)
                        {
                            if (kvs2[i].Length > 2 && kvs2[i].Substring(0, 2) == "c=")
                                cc = Uri.UnescapeDataString(kvs2[i].Substring(2));
                            else if (kvs2[i].Length > 4 && kvs2[i].Substring(0, 4) == "act=")
                                act = Uri.UnescapeDataString(kvs2[i].Substring(4));
                        }
                        if (cc != Pairing.Code)
                        {
                            WriteResponse(stream, 403, "text/plain; charset=utf-8", "配对码校验失败，请刷新页面重试");
                            break;
                        }
                        string cres;
                        if (act == "disconnect")
                        {
                            cres = "{\"ok\":true,\"kicked\":" +
                                (KickActive("电脑已断开此连接，如需重连请在手机上输入配对码") ? "true" : "false") + "}";
                            Log.Info("电脑端主动断开当前手机");
                        }
                        else if (act == "recode")
                        {
                            // 重新配对：换新码 + 撤销旧会话，旧设备不重新拿码就进不来
                            string nc = Pairing.NewCode();
                            KickActive("电脑已重新配对：配对码已更新，请重新扫新码");
                            Log.Info("扫码页重新生成配对码: " + nc);
                            cres = "{\"ok\":true}";
                        }
                        else if (act == "opendir")
                        {
                            // 扫码页/手机页「打开接收文件夹」：两版统一能力（麒麟无托盘，靠这个入口）
                            try { Process.Start("explorer.exe", FileSink.DirOf()); cres = "{\"ok\":true}"; }
                            catch (Exception) { cres = "{\"ok\":false}"; }
                        }
                        else
                        {
                            WriteResponse(stream, 400, "text/plain", "unknown action");
                            break;
                        }
                        WriteResponse(stream, 200, "application/json; charset=utf-8", cres);
                        break;
                    }
                    default:
                        WriteResponse(stream, 404, "text/plain", "not found");
                        break;
                }
                client.Close();
            }
            catch (Exception)
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        // 手机端页面被访问的记录（每个来源 IP 只记一次）：
        // 「扫码后打不开」远程排查时，这一行能直接判断手机到底有没有到达电脑
        private static readonly HashSet<string> seenPeers = new HashSet<string>();
        private static readonly object PeerGate = new object();

        private static void NotePeerVisit(TcpClient client)
        {
            try
            {
                System.Net.IPEndPoint ep = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (ep == null) return;
                string peer = ep.Address.ToString();
                if (peer.Length == 0) return;
                bool fresh = false;
                lock (PeerGate)
                {
                    if (!seenPeers.Contains(peer)) { seenPeers.Add(peer); fresh = true; }
                }
                if (fresh) Log.Info("手机端页面被访问: 来自 " + peer + "（扫码已到达电脑）");
            }
            catch (Exception) { }
        }

        // 字节级读取 HTTP 头（避免 StreamReader 预读吃掉 WS 升级后的数据）
        private static string ReadHeaders(NetworkStream stream)
        {
            List<byte> buf = new List<byte>();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                buf.Add((byte)b);
                int n = buf.Count;
                if (n >= 4 &&
                    buf[n - 4] == 13 && buf[n - 3] == 10 &&
                    buf[n - 2] == 13 && buf[n - 1] == 10)
                {
                    break;
                }
                if (n > 64 * 1024) return null;
            }
            return Encoding.ASCII.GetString(buf.ToArray());
        }

        private static void WriteResponse(NetworkStream stream, int code, string contentType, string body)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + " " + ReasonOf(code) + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + data.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static string ReasonOf(int code)
        {
            if (code == 200) return "OK";
            if (code == 400) return "Bad Request";
            if (code == 403) return "Forbidden";
            if (code == 404) return "Not Found";
            if (code == 405) return "Method Not Allowed";
            if (code == 426) return "Upgrade Required";
            if (code == 500) return "Internal Server Error";
            return "OK";
        }

        internal static void ReplaceActive(WsSession newer)
        {
            lock (SessionGate)
            {
                if (activeSession != null && activeSession != newer)
                {
                    Log.Info("新连接接入，断开旧连接");
                    activeSession.NotifyKick("此页面已被新连接取代");   // 先告知旧页面原因，再断开
                    activeSession.Kick();
                }
                activeSession = newer;
            }
        }

        // 电脑端主动断开当前手机：带原因踢下线，返回是否真有会话被踢
        internal static bool KickActive(string msg)
        {
            WsSession s;
            lock (SessionGate) { s = activeSession; }
            if (s == null) return false;
            s.NotifyKick(msg);
            s.Kick();
            return true;
        }

        internal static void RemoveActive(WsSession session)
        {
            lock (SessionGate)
            {
                if (activeSession == session) activeSession = null;
            }
        }
    }

    // ---------------- WebSocket 会话（手写 RFC6455） ----------------
    internal class WsSession
    {
        private TcpClient client;
        private NetworkStream stream;
        private bool authed;
        private volatile bool closed;
        private readonly object SendGate = new object();   // 多线程写帧互斥（接收线程/UI线程）
        private JavaScriptSerializer json = new JavaScriptSerializer();
        // 文件接收状态（单会话单任务，手机串行发送）
        private bool receiving;
        private FileStream recvStream;
        private string recvPath;
        private long recvSize, recvGot, lastAckGot;
        private bool recvPaste;
        // 撤回护栏：本会话在当前前台窗口里实际注入过的字符数与前台窗口标识。
        // 前台窗口一变，配额归零——批量退格只允许删自己注进去的内容
        private int injectedChars;
        private string lastFg;

        internal WsSession(TcpClient client, NetworkStream stream, string request)
        {
            this.client = client;
            this.stream = stream;
            string key = FindHeader(request, "Sec-WebSocket-Key");
            if (key == null) throw new IOException("not a websocket request");
            string accept = ComputeAccept(key);
            string resp = "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] respBytes = Encoding.ASCII.GetBytes(resp);
            stream.Write(respBytes, 0, respBytes.Length);
            stream.Flush();
        }

        internal void Start()
        {
            // 注意：不在这里踢旧连接！必须等配对验证通过后才替换，
            // 否则任何拿错码的连接都会把正在干活的手机顶下线（升级→顶替→配对失败→断开，两头空）
            Thread t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Start();
        }

        internal void Kick()
        {
            closed = true;
            try { client.Close(); }
            catch (Exception) { }
        }

        // 把原因先发给对端页面，让它停止盲重连并提示用户（取代/手动断开/重新配对共用）
        internal void NotifyKick(string msg)
        {
            try { SendError(msg); }
            catch (Exception) { }
        }

        // 给手机发一条普通提示（不标红不改连接状态）：粘贴真实结果/取消确认
        internal void SendNote(string msg)
        {
            Dictionary<string, object> note = new Dictionary<string, object>();
            note["type"] = "note";
            note["msg"] = msg;
            try { SendJson(note); }
            catch (Exception) { }
        }

        private static string FindHeader(string request, string name)
        {
            string[] lines = request.Split('\n');
            foreach (string line in lines)
            {
                string l = line.Trim();
                int c = l.IndexOf(':');
                if (c <= 0) continue;
                if (string.Equals(l.Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return l.Substring(c + 1).Trim();
                }
            }
            return null;
        }

        private static string ComputeAccept(string key)
        {
            const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            SHA1 sha = SHA1.Create();
            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(key + magic));
            return Convert.ToBase64String(hash);
        }

        private void ReadLoop()
        {
            try
            {
                // 分片重组：部分浏览器内核（如微信 XWeb）会把大帧拆成 FIN=0 + 续帧(0x0)发送
                MemoryStream frag = null;
                int fragOp = 0;
                while (!closed)
                {
                    int opcode;
                    byte[] payload;
                    bool fin;
                    if (!ReadFrame(out opcode, out payload, out fin)) break;

                    int op = opcode;
                    byte[] full = payload;
                    if (opcode == 0x0)                        // 续帧
                    {
                        if (frag == null) continue;           // 无起点的续帧，丢弃
                        frag.Write(payload, 0, payload.Length);
                        if (frag.Length > 1024 * 1024) break; // 分片总量兜底防滥用
                        if (!fin) continue;
                        op = fragOp;
                        full = frag.ToArray();
                        frag = null;
                    }
                    else if (!fin)                            // 首分片
                    {
                        if (opcode != 0x1 && opcode != 0x2) continue;
                        frag = new MemoryStream();
                        frag.Write(payload, 0, payload.Length);
                        fragOp = opcode;
                        continue;
                    }

                    if (op == 0x8) break;                     // close
                    if (op == 0x9)                            // ping → pong
                    {
                        SendFrame(0xA, full);
                        continue;
                    }
                    if (op == 0xA) continue;                  // pong
                    if (op == 0x2)                            // 二进制：文件数据块
                    {
                        if (!HandleBinary(full)) break;
                        continue;
                    }
                    if (op != 0x1) continue;                  // 其余忽略

                    string msg = Encoding.UTF8.GetString(full);
                    if (!HandleMessage(msg)) break;
                }
            }
            catch (Exception) { }
            finally
            {
                closed = true;
                if (receiving) AbortReceive();   // 传一半断线，删掉残文件
                if (authed) State.ClientCount--;
                TinyHttp.RemoveActive(this);
                try { client.Close(); }
                catch (Exception) { }
                Log.Info("连接断开");
            }
        }

        // 返回 false = 连接应关闭
        private bool HandleMessage(string msg)
        {
            Dictionary<string, object> dict;
            try
            {
                dict = json.Deserialize<Dictionary<string, object>>(msg);
            }
            catch (Exception)
            {
                return false;
            }
            if (dict == null || !dict.ContainsKey("type")) return false;
            string type = dict["type"] as string;

            if (!authed)
            {
                if (type != "hello") { SendError("请先配对"); return false; }
                object codeObj;
                string code = "";
                if (dict.TryGetValue("code", out codeObj)) code = (codeObj as string) ?? "";
                if (code != Pairing.Code)
                {
                    SendError("配对码错误");
                    Log.Info("配对失败: 收到[" + code + "] 期望[" + Pairing.Code + "]");
                    return false;   // 配对错误直接断开（方案 4.1）
                }
                authed = true;
                // 先踢旧会话并等其计数减完，再登记自己，避免交接瞬间计数短暂归零
                TinyHttp.ReplaceActive(this);
                for (int i = 0; i < 20 && State.ClientCount > 0; i++) Thread.Sleep(50);
                State.ClientCount++;
                SendJson(new Dictionary<string, object> { { "type", "ready" } });
                Log.Info("手机配对成功");
                return true;
            }

            switch (type)
            {
                case "text":
                {
                    object t;
                    string text = "";
                    if (dict.TryGetValue("text", out t)) text = (t as string) ?? "";
                    if (text.Length == 0) return true;
                    if (text.Length > 10000) { SendError("单次最多1万字符"); return true; }
                    if (State.Paused) { SendError("注入已暂停"); return true; }
                    FgCheck();
                    uint sent = Inject.Text(text);
                    injectedChars += text.Length;
                    Log.Info("注入 " + text.Length + " 字符 (" + sent + " 事件)");
                    SendAck(true, text.Length);
                    return true;
                }
                case "enter":
                {
                    if (State.Paused) { SendError("注入已暂停"); return true; }
                    FgCheck();
                    Inject.Key(0x0D, 1);
                    injectedChars++;   // 手机端把换行也算进镜像（lastSent），这里同步记账
                    SendAck(true, 0);
                    return true;
                }
                case "backspace":
                {
                    if (State.Paused) { SendError("注入已暂停"); return true; }
                    FgCheck();
                    int count = 1;
                    object c;
                    if (dict.TryGetValue("count", out c))
                    {
                        try { count = Convert.ToInt32(c); }
                        catch (Exception) { count = 1; }
                    }
                    if (count < 1) count = 1;
                    if (count > 10000) count = 10000;
                    // 单个退格是普通编辑动作，放行；批量退格（撤回）只能删本会话在当前窗口注入过的内容
                    if (count > 1)
                    {
                        if (injectedChars <= 0)
                        {
                            SendError("当前窗口没有可撤回的内容（若刚切换过窗口，撤回已失效）");
                            return true;
                        }
                        if (count > injectedChars)
                        {
                            SendError("自上次切换窗口以来只注入过 " + injectedChars + " 个字符，撤回已截断");
                            count = injectedChars;
                        }
                    }
                    Inject.Key(0x08, count);
                    injectedChars = injectedChars > count ? injectedChars - count : 0;
                    SendAck(true, count);
                    return true;
                }
                case "ping":
                    SendJson(new Dictionary<string, object> { { "type", "pong" } });
                    return true;
                case "sync":
                {
                    // 实时同步：原子完成「退格 N 次 + 输入增量文本」
                    // 客户端把输入框当作电脑上文本的镜像，算最长公共前缀求增量
                    if (State.Paused) { SendError("注入已暂停"); return true; }
                    FgCheck();
                    object so;
                    string stext = "";
                    if (dict.TryGetValue("text", out so)) stext = (so as string) ?? "";
                    int sback = 0;
                    object sb;
                    if (dict.TryGetValue("back", out sb))
                    {
                        try { sback = Convert.ToInt32(sb); }
                        catch (Exception) { sback = 0; }
                    }
                    if (sback < 0) sback = 0;
                    if (sback > 10000) sback = 10000;
                    if (stext.Length > 10000) { SendError("单次最多1万字符"); return true; }
                    if (sback > 0) Inject.Key(0x08, sback);
                    uint ssent = 0;
                    if (stext.Length > 0) ssent = Inject.Text(stext);
                    if (sback > 0 || ssent > 0)
                    {
                        Log.Info("同步 退格" + sback + " 注入" + stext.Length + "字符");
                    }
                    injectedChars = (injectedChars > sback ? injectedChars - sback : 0) + stext.Length;
                    SendAck(true, stext.Length);
                    return true;
                }
                case "file":
                {
                    if (receiving) { SendError("正在接收另一个文件，请稍候"); return true; }
                    object no, so, po;
                    string fname = dict.TryGetValue("name", out no) ? (no as string) : null;
                    long fsize = -1;
                    if (dict.TryGetValue("size", out so)) { try { fsize = Convert.ToInt64(so); } catch (Exception) { fsize = -1; } }
                    bool pasteOpt = dict.TryGetValue("paste", out po) && po is bool && (bool)po;
                    if (fsize <= 0 || fsize > FileSink.MaxFile) { SendError("文件大小无效（限200MB内）"); return true; }
                    try
                    {
                        recvPath = FileSink.Prepare(fname);
                        recvStream = new FileStream(recvPath, FileMode.Create, FileAccess.Write);
                    }
                    catch (Exception ex) { SendError("创建文件失败: " + ex.Message); return true; }
                    receiving = true; recvSize = fsize; recvGot = 0; lastAckGot = 0; recvPaste = pasteOpt;
                    Log.Info("开始接收文件 " + Path.GetFileName(recvPath) + " (" + fsize + " 字节)");
                    SendJson(new Dictionary<string, object> { { "type", "file_begin" } });
                    return true;
                }
                case "file_end":
                {
                    if (!receiving) return true;
                    FinishReceive();
                    return true;
                }
                case "file_cancel":
                {
                    // 手机取消上传：丢弃半截文件，回一条普通提示确认
                    if (receiving)
                    {
                        string cn = recvPath != null ? Path.GetFileName(recvPath) : "";
                        AbortReceive();
                        Log.Info("手机取消上传: " + cn);
                        SendNote("已取消接收 " + cn);
                    }
                    return true;
                }
                default:
                    return true;    // 未知消息忽略，不断开
            }
        }

        // 窗口切换检查：前台窗口一变，本会话的「可撤回」配额归零，
        // 避免手机端把换窗口后的批量退格打到不相干的应用里
        private void FgCheck()
        {
            string fg = Inject.ForegroundTitle();
            if (fg != lastFg) { injectedChars = 0; lastFg = fg; }
        }

        private void SendAck(bool ok, int chars)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["type"] = "ack";
            ack["ok"] = ok;
            ack["chars"] = chars;
            ack["fg"] = Inject.ForegroundTitle();
            SendJson(ack);
        }

        // 二进制帧：写入当前接收中的文件
        private bool HandleBinary(byte[] data)
        {
            if (!receiving || recvStream == null) return true;   // 未声明文件的二进制帧，忽略
            if (recvGot + data.Length > recvSize)
            {
                AbortReceive();
                try { SendError("文件超出声明大小，已中止"); } catch (Exception) { }
                return false;
            }
            try
            {
                recvStream.Write(data, 0, data.Length);
                recvGot += data.Length;
                if (recvGot - lastAckGot >= 1024 * 1024 || recvGot == recvSize)
                {
                    lastAckGot = recvGot;
                    SendJson(new Dictionary<string, object> { { "type", "file_got" }, { "bytes", recvGot } });
                }
            }
            catch (Exception)
            {
                AbortReceive();
                return false;
            }
            return true;
        }

        private void AbortReceive()
        {
            receiving = false;
            try { if (recvStream != null) recvStream.Close(); } catch (Exception) { }
            recvStream = null;
            try { if (recvPath != null && File.Exists(recvPath)) File.Delete(recvPath); } catch (Exception) { }
        }

        private void FinishReceive()
        {
            receiving = false;
            try { recvStream.Close(); } catch (Exception) { }
            recvStream = null;
            if (recvGot != recvSize || !File.Exists(recvPath))
            {
                try { if (File.Exists(recvPath)) File.Delete(recvPath); } catch (Exception) { }
                SendError("传输不完整（" + recvGot + "/" + recvSize + "），请重试");
                return;
            }
            string name = Path.GetFileName(recvPath);
            bool doPaste = recvPaste && FileSink.IsImage(recvPath) && !State.Paused;
            Log.Info("接收完成 " + name + " (" + recvGot + " 字节)" + (doPaste ? " → 粘贴到前台" : ""));
            Dictionary<string, object> done = new Dictionary<string, object>();
            done["type"] = "file_done";
            done["saved"] = name;
            // 结果提示分级：粘贴是异步的，此刻还不能断言成功，先用 pasting 占位，完成后另发 note 报真实结果
            done["pasted"] = false;
            if (doPaste) done["pasting"] = true;
            else if (recvPaste && FileSink.IsImage(recvPath) && State.Paused) done["why"] = "注入已暂停，未粘贴";
            SendJson(done);
            string p = recvPath;
            WsSession self = this;
            if (doPaste)
            {
                // 排进专属粘贴线程队列：多图连发时一张贴完(含恢复剪贴板)才轮到下一张
                PasteGate.Begin(delegate
                {
                    string r;
                    try { r = FileSink.PasteToCursor(p); }
                    catch (Exception ex) { r = "粘贴异常：" + ex.Message; }
                    Log.Info("粘贴结果 " + name + ": " + r);   // 成败都留痕，方便事后排查
                    PasteGate.Notify(name + "：" + r);
                    self.SendNote(r);   // 真实粘贴结果回传手机
                });
            }
            else
            {
                PasteGate.Notify("已接收文件到「下载\\TypeBridge接收」：" + name);
            }
        }

        private void SendError(string msg)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["type"] = "error";
            e["msg"] = msg;
            SendJson(e);
        }

        private void SendJson(Dictionary<string, object> obj)
        {
            SendFrame(0x1, Encoding.UTF8.GetBytes(json.Serialize(obj)));
        }

        private void SendFrame(int opcode, byte[] payload)
        {
            if (closed) return;
            try
            {
                List<byte> frame = new List<byte>();
                frame.Add((byte)(0x80 | opcode));              // FIN + opcode
                if (payload.Length < 126)
                {
                    frame.Add((byte)payload.Length);
                }
                else if (payload.Length < 65536)
                {
                    frame.Add(126);
                    frame.Add((byte)(payload.Length >> 8));
                    frame.Add((byte)(payload.Length & 0xFF));
                }
                else
                {
                    frame.Add(127);
                    ulong len = (ulong)payload.Length;
                    for (int i = 7; i >= 0; i--) frame.Add((byte)((len >> (i * 8)) & 0xFF));
                }
                frame.AddRange(payload);
                byte[] data = frame.ToArray();
                // 发送加锁：粘贴结果 note 由 UI 线程写入，与接收线程的 ack/file_got 可能并发
                lock (SendGate) { stream.Write(data, 0, data.Length); stream.Flush(); }
            }
            catch (Exception)
            {
                closed = true;
            }
        }

        private bool ReadFrame(out int opcode, out byte[] payload, out bool fin)
        {
            opcode = 0;
            payload = null;
            fin = false;
            int b0 = stream.ReadByte();
            if (b0 < 0) return false;
            int b1 = stream.ReadByte();
            if (b1 < 0) return false;
            fin = (b0 & 0x80) != 0;
            opcode = b0 & 0x0F;
            bool masked = (b1 & 0x80) != 0;
            ulong len = (ulong)(b1 & 0x7F);

            if (len == 126)
            {
                int hi = stream.ReadByte(), lo = stream.ReadByte();
                if (hi < 0 || lo < 0) return false;
                len = (ulong)((hi << 8) | lo);
            }
            else if (len == 127)
            {
                len = 0;
                for (int i = 0; i < 8; i++)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return false;
                    len = (len << 8) | (ulong)(uint)b;
                }
            }
            if (len > 1024 * 1024) return false;               // 1MB 上限，防滥用

            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return false;
                    mask[i] = (byte)b;
                }
            }

            byte[] data = new byte[len];
            int got = 0;
            while (got < (int)len)
            {
                int n = stream.Read(data, got, (int)len - got);
                if (n <= 0) return false;
                got += n;
            }
            if (masked)
            {
                for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
            }
            payload = data;
            return true;
        }
    }

    // ---------------- 扫码弹窗 ----------------
    // 需求：托盘「链接二维码」不要再开浏览器（慢、占内存），改成程序内小弹窗。
    internal class PairForm : Form
    {
        private WebBrowser browser;
        private bool shownOnce;

        internal PairForm()
        {
            Text = "TypeBridge 扫码连接";
            Icon = AppIcon.Get(32);   // 标题栏/任务栏用应用 logo
            // 支持最大化/最小化：内嵌 WebBrowser 是 Dock.Fill，放大后二维码跟着变大
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            // 默认尺寸让「二维码 + 配对码 + 控制按钮」一屏放下（页面是嵌入模式，二维码置顶）
            MinimumSize = new System.Drawing.Size(380, 470);
            ClientSize = new System.Drawing.Size(420, 620);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            browser = new WebBrowser();
            browser.ScriptErrorsSuppressed = true;
            browser.Dock = DockStyle.Fill;
            Controls.Add(browser);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                // 点 X 只是隐藏，程序仍在托盘常驻
                e.Cancel = true;
                Hide();
            };
        }

        internal void ShowPair(string url)
        {
            // 若窗口处于最小化状态，先恢复再激活（托盘双击/菜单复用同一实例）
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (!shownOnce) { shownOnce = true; Show(); }
            else { Show(); Activate(); }
            try { browser.Navigate(url); }
            catch (Exception ex) { Log.Info("加载扫码窗口失败: " + ex.Message); }
        }
    }

    internal static class WinCompat
    {
        // 让内嵌浏览器控件使用 IE11 内核（画二维码的 canvas 需要 IE9+），只影响本程序
        internal static void EnableIe11()
        {
            try
            {
                string exe = System.IO.Path.GetFileName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
                if (k != null)
                {
                    k.SetValue(exe, 11001, Microsoft.Win32.RegistryValueKind.DWord);
                    k.Close();
                }
            }
            catch (Exception ex) { Log.Info("设置浏览器内核模式失败: " + ex.Message); }
        }
    }

    // ---------------- 托盘 ----------------
    internal class TrayApp : ApplicationContext
    {
        private NotifyIcon tray;
        private MenuItem pauseItem;
        internal NotifyIcon Tray { get { return tray; } }

        private MenuItem startupItem;
        private MenuItem statusItem;
        private MenuItem networkMenu;
        private int port;
        private PairForm pairForm;

        internal TrayApp(string ip, int port)
        {
            this.port = port;
            ContextMenu menu = new ContextMenu();

            // 第一行常驻显示版本/当前地址/端口/连接状态（灰色不可点，每次展开菜单刷新）
            statusItem = new MenuItem("");
            statusItem.Enabled = false;
            menu.MenuItems.Add(statusItem);
            menu.Popup += delegate { RefreshStatusItem(); };
            menu.MenuItems.Add("获取更新: " + AppInfo.UpdateUrlText, delegate
            {
                try { Process.Start(AppInfo.UpdateUrl); }
                catch (Exception ex) { Log.Info("打开更新页失败: " + ex.Message); }
            });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("打开手机扫码连接页面", delegate { OpenPair(); });
            menu.MenuItems.Add("重新生成配对码", delegate
            {
                Pairing.NewCode();
                // 重新配对必须同时吊销旧会话，否则旧手机拿着有效连接继续输入，换码就形同虚设
                TinyHttp.KickActive("电脑已重新配对：配对码已更新，请重新扫新码");
                Log.Info("配对码已更新: " + Pairing.Code);
                tray.ShowBalloonTip(1500, "TypeBridge", "新配对码: " + Pairing.Code + "，请重新扫码", ToolTipIcon.Info);
            });
            pauseItem = new MenuItem("暂停注入", delegate
            {
                State.Paused = !State.Paused;
                pauseItem.Checked = State.Paused;
                Log.Info(State.Paused ? "注入已暂停" : "注入已恢复");
            });
            menu.MenuItems.Add(pauseItem);
            // 选择网络：多网卡机器（VPN/热点/代理虚拟网卡）上自动选的可能不是手机能访到的那块，
            // 切换只改二维码指向，服务始终监听 0.0.0.0，不需要重启；选择会记进配置下次沿用
            networkMenu = new MenuItem("选择网络(扫码地址)");
            networkMenu.Popup += delegate { RefreshNetworkMenu(); };
            menu.MenuItems.Add(networkMenu);
            // 手机扫码打不开的一键自救：检查并补防火墙放行（会弹一次管理员确认），
            // 无论如何都把当前扫码地址气泡出来，用户拿手机对照最直观
            menu.MenuItems.Add("手机连不上？一键修复", delegate { RunConnectivityFix(); });
            startupItem = new MenuItem("", delegate
            {
                bool want = !AutoStart.IsEnabled();
                string err = AutoStart.Set(want);
                if (err != null)
                {
                    MessageBox.Show("设置开机启动失败: " + err, "TypeBridge");
                    return;
                }
                RefreshStartupItem();
                tray.ShowBalloonTip(1500, "TypeBridge", want ? "已开启开机自动启动" : "已关闭开机自动启动", ToolTipIcon.Info);
            });
            RefreshStartupItem();
            menu.MenuItems.Add(startupItem);
            menu.MenuItems.Add("查看日志", delegate { Log.OpenFile(); });
            menu.MenuItems.Add("打开接收文件夹", delegate
            {
                try { Process.Start("explorer.exe", FileSink.DirOf()); }
                catch (Exception) { }
            });
            // 最近接收：子菜单弹出时实时列目录，点条目直接打开对应文件
            MenuItem recentItem = new MenuItem("最近接收");
            recentItem.Popup += delegate
            {
                recentItem.MenuItems.Clear();
                try
                {
                    string[] files = Directory.GetFiles(FileSink.DirOf());
                    Array.Sort(files, delegate(string a, string b)
                    {
                        return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                    });
                    for (int fi = 0; fi < files.Length && fi < 8; fi++)
                    {
                        string fp = files[fi];
                        recentItem.MenuItems.Add(new MenuItem(Path.GetFileName(fp), delegate
                        {
                            try { Process.Start(fp); }
                            catch (Exception) { }
                        }));
                    }
                    if (recentItem.MenuItems.Count == 0) recentItem.MenuItems.Add(new MenuItem("（还没有收到文件）"));
                }
                catch (Exception) { recentItem.MenuItems.Add(new MenuItem("（接收目录还不存在）")); }
            };
            menu.MenuItems.Add(recentItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate
            {
                tray.Visible = false;
                Environment.Exit(0);
            });

            tray = new NotifyIcon();
            tray.Icon = AppIcon.Get(16);
            tray.Text = "TypeBridge " + AppInfo.Version + " 配对码 " + Pairing.Code;
            tray.ContextMenu = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenPair(); };
            PasteGate.Init(tray);   // 文件接收后的剪贴板/粘贴操作走专属 STA 线程

            // 网络守望：电脑换了 WiFi / 插拔网线后，二维码里的旧地址失效是
            // 「扫码打不开」的高发原因。每 10 秒核对一次当前地址是否还在网卡上，
            // 失效就自动切到最优网卡并气泡提醒重新扫码（开着的扫码页会自己刷新）。
            Thread watch = new Thread(new ThreadStart(delegate
            {
                while (true)
                {
                    Thread.Sleep(10000);
                    try
                    {
                        string newIp = NetDetect.AutoRepairCurrent();
                        if (newIp != null)
                        {
                            Log.Info("网络已变化，扫码地址自动切换为: " + newIp + ":" + port);
                            PasteGate.Notify("网络已变化，扫码地址已更新为 " + newIp + ":" + port + "，请用手机重新扫码");
                        }
                    }
                    catch (Exception) { }
                }
            }));
            watch.IsBackground = true;
            watch.Start();
        }

        // 一键修复的实际动作（在线程池跑：UAC 等待不能卡死托盘菜单）：
        // 先查防火墙；缺规则就提权补一条全网络类型的放行；最后气泡说明结果
        private void RunConnectivityFix()
        {
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                string msg;
                if (FirewallGuard.CheckAllowed())
                {
                    msg = "防火墙正常。当前扫码地址 http://" + NetDetect.CurrentIp() + ":" + port
                        + "。手机仍连不上时请确认：手机和电脑连的是同一个 WiFi，且手机没开流量/代理";
                }
                else if (FirewallGuard.Fix())
                {
                    msg = "防火墙已放行，请用手机重新扫码（地址 http://" + NetDetect.CurrentIp() + ":" + port + "）";
                }
                else
                {
                    msg = "放行未完成（刚才的管理员确认框点「是」才能生效）。也可检查手机是否和电脑连同一个 WiFi";
                }
                Log.Info("一键修复: " + msg);
                PasteGate.Notify(msg);
            });
        }

        // 菜单顶部状态行：版本 · 当前地址 · 端口 · 连接状态
        private void RefreshStatusItem()
        {
            string ip = NetDetect.CurrentIp();
            if (string.IsNullOrEmpty(ip)) ip = "未获取到地址";
            statusItem.Text = "TypeBridge " + AppInfo.Version + " · " + ip + ":" + port +
                (State.ClientCount > 0 ? " · 手机已连接" : " · 等待手机连接");
        }

        // 菜单项文字动态反映"点一下会做什么"，不靠勾选标记猜
        internal void RefreshStartupItem()
        {
            bool on = AutoStart.IsEnabled();
            startupItem.Text = on ? "关闭开机启动" : "开启开机启动";
            startupItem.Checked = on;
        }

        // 弹出子菜单时重建「选择网络」：网络会变（连新 WiFi、开关代理），固定列表会过期。
        // 当前地址打勾；点击切换后记住选择并刷新扫码窗。
        private void RefreshNetworkMenu()
        {
            networkMenu.MenuItems.Clear();
            List<NetDetect.Candidate> list = NetDetect.ListCandidates();
            string cur = NetDetect.CurrentIp();
            foreach (NetDetect.Candidate c in list)
            {
                string ipx = c.Ip;
                MenuItem mi = new MenuItem(ipx + "（" + c.Label + "）", delegate { SwitchNic(ipx); });
                mi.Checked = ipx == cur;
                networkMenu.MenuItems.Add(mi);
            }
            if (list.Count == 0)
            {
                MenuItem none = new MenuItem("（没找到可用的局域网网卡）");
                none.Enabled = false;
                networkMenu.MenuItems.Add(none);
            }
        }

        private void SwitchNic(string ip)
        {
            if (ip == NetDetect.CurrentIp()) return;
            if (!NetDetect.Use(ip)) return;
            Config.SetPreferredIp(ip);
            Log.Info("扫码地址已切换为: " + ip + ":" + port);
            tray.ShowBalloonTip(1500, "TypeBridge", "扫码地址已改为 http://" + ip + ":" + port + "，手机重新扫码即可", ToolTipIcon.Info);
            // 扫码窗口开着的话立即重画二维码，免得还指着旧地址
            if (pairForm != null && pairForm.Visible) pairForm.ShowPair("http://localhost:" + port + "/pair?embed=1");
        }

        private void OpenPair()
        {
            if (pairForm == null) { pairForm = new PairForm(); }
            // embed=1：嵌入模式，页面隐藏说明栏、二维码置顶，小窗口打开即可扫码
            pairForm.ShowPair("http://localhost:" + port + "/pair?embed=1");
        }

        internal void ShowPair()
        {
            OpenPair();
        }
    }

    // ---------------- 入口 ----------------
    internal static class Program
    {
        private const int Port = 8765;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            WinCompat.EnableIe11();

            // 单实例：端口已被占用 = 已有一个在跑，直接复用，不再重复启动
            if (TinyHttp.PortInUse(Port))
            {
                MessageBox.Show(
                    "TypeBridge 已经在运行了（右下角托盘图标里）。" + Environment.NewLine + Environment.NewLine
                    + "右键托盘图标选「打开手机扫码连接页面」即可弹出扫码窗口。",
                    "TypeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            NetDetect.Init(Config.PreferredIp());
            string ip = NetDetect.CurrentIp();
            if (ip == null)
            {
                MessageBox.Show("未找到可用的局域网 IP，请检查电脑是否连上网络。", "TypeBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            string code = Pairing.NewCode();

            Log.Info("========== TypeBridge " + AppInfo.Version + " 启动 ==========");
            List<NetDetect.Candidate> nics = NetDetect.ListCandidates();
            if (nics.Count > 1)
            {
                string all = "";
                foreach (NetDetect.Candidate c in nics) all += (all.Length > 0 ? "，" : "") + c.Ip + "(" + c.Label + ")";
                Log.Info("检测到多个局域网地址: " + all);
            }
            Log.Info("扫码地址(当前网卡): http://" + ip + ":" + Port + "  手机连不上时可在托盘「选择网络」切换");
            Log.Info("配对码: " + code);
            Log.Info("手机访问地址: http://" + ip + ":" + Port + "/m?code=" + code);

            bool bindOk = false;
            string bindErr = null;
            TinyHttp.ready = delegate { bindOk = true; };
            TinyHttp.failed = delegate(string m) { bindErr = m; };

            Thread server = new Thread(new ThreadStart(delegate { TinyHttp.Run(Port); }));
            server.IsBackground = true;
            server.Start();
            for (int i = 0; i < 25 && !bindOk && bindErr == null; i++) Thread.Sleep(100);
            if (bindErr != null)
            {
                MessageBox.Show("服务启动失败: " + bindErr, "TypeBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            TrayApp app = new TrayApp(ip, Port);
            NotifyIcon trayRef = app.Tray;
            Thread tick = new Thread(new ThreadStart(delegate
            {
                // 悬停提示常驻显示配对码+地址端口，地址被托盘切换后跟着变
                string lastT = "";
                while (true)
                {
                    int n = State.ClientCount;
                    string t = (n > 0 ? "TypeBridge " + AppInfo.Version + " 手机已连接 " : "TypeBridge " + AppInfo.Version + " 等待扫码 ")
                        + "码" + Pairing.Code + " " + NetDetect.CurrentIp() + ":" + Port;
                    if (t != lastT)
                    {
                        lastT = t;
                        try { trayRef.Text = t; } catch (Exception) { }
                    }
                    Thread.Sleep(1000);
                }
            }));
            tick.IsBackground = true;
            tick.Start();

            // 防火墙自检：首次运行弹的防火墙询问被点了取消的话，手机永远连不上，
            // 越早告诉用户越好（免管理员的只读检查）
            if (!FirewallGuard.CheckAllowed())
            {
                Log.Info("!! 未检测到防火墙放行规则（首次运行的弹窗可能被取消了），手机可能连不上。");
                Log.Info("!! 右键托盘图标选「手机连不上？一键修复」即可放行（会弹一次管理员确认）。");
                PasteGate.Notify("未检测到防火墙放行规则，手机可能连不上；右键托盘图标选「手机连不上？一键修复」");
            }

            // 启动行为：已加入开机自启 → 静默待命，不弹窗、不再询问任何东西，
            // 客户需要扫码时自己右键托盘图标（或双击图标）选「链接二维码」。
            bool autostartOn = AutoStart.IsEnabled();
            if (autostartOn)
            {
                Log.Info("已开启开机自启：静默启动，需要扫码时右键托盘图标选「打开手机扫码连接页面」");
            }
            else
            {
                // 未开自启（多半是手动双击启动的）：弹扫码窗；
                // 开机自启只在第一次运行时问一次，问过（无论当时选了什么）
                // 就不再问，想改随时用托盘菜单里的开关。
                app.ShowPair();
                if (!Config.FirstRunDone())
                {
                    DialogResult dr = MessageBox.Show(
                        "TypeBridge 已启动，浏览器里扫码就能连。" + Environment.NewLine + Environment.NewLine
                        + "占用内存很小，下次可以直接扫码连接使用。" + Environment.NewLine + Environment.NewLine
                        + "是否加入开机自动启动？（推荐，开机后自动在后台待命，不再弹窗）",
                        "TypeBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes)
                    {
                        string err = AutoStart.Set(true);
                        if (err != null)
                        {
                            MessageBox.Show("设置开机启动失败: " + err, "TypeBridge",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            app.RefreshStartupItem();
                        }
                    }
                    Config.SetFirstRunDone();
                }
            }

            Application.Run(app);
            return 0;
        }


    }
}
