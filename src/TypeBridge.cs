// ============================================================
// TypeBridge 阶段2 —— 电脑端主程序 v0.2
// 手机浏览器打字 → 局域网 WebSocket → 本程序注入电脑前台输入框
// 语法：C# 5（系统自带 csc.exe 编译，禁用 C#6+ 语法）
// 编译：build\build_stage1.bat （/target:winexe 纯托盘，无控制台窗口）
// 运行：build\TypeBridge.exe   双击即用：自动弹浏览器显示扫码二维码，
//       右下角托盘常驻（右键：链接二维码 / 退出），日志写本地文件
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
                    return v != null && string.Equals((string)v, ExePath, StringComparison.OrdinalIgnoreCase);
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
    internal static class NetDetect
    {
        // 返回最优局域网 IPv4：无线 > 有线 > 其他；排除环回/APIPA
        internal static string PickBestIpv4()
        {
            string best = null;
            int bestScore = -1;
            try
            {
                NetworkInterface[] nis = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface ni in nis)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    int score;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score = 3;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score = 2;
                    else score = 1;
                    foreach (UnicastIPAddressInformation uai in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (uai.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = uai.Address.ToString();
                        if (s.StartsWith("169.254.") || s.StartsWith("127.")) continue;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("网卡探测失败: " + ex.Message);
            }
            return best;
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

    // ---------------- TinyHttp：TcpListener 极简 HTTP + WS 路由 ----------------
    internal static class TinyHttp
    {
        private static WsSession activeSession;
        private static readonly object SessionGate = new object();

        internal static Action ready;
        internal static Action<string> failed;

        internal static void Run(int port)
        {
            try
            {
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

                switch (path)
                {
                    case "/":
                        WriteResponse(stream, 200, "text/html; charset=utf-8", Res.Get("index.html"));
                        break;
                    case "/pair":
                    {
                        string ip = NetDetect.PickBestIpv4();
                        string url = "http://" + ip + ":8765/m?code=" + Pairing.Code;
                        string html = Res.Get("pair.html")
                            .Replace("{{URL}}", url)
                            .Replace("{{CODE}}", Pairing.Code);
                        WriteResponse(stream, 200, "text/html; charset=utf-8", html);
                        break;
                    }
                    case "/m":
                        WriteResponse(stream, 200, "text/html; charset=utf-8", Res.Get("mobile.html"));
                        break;
                    case "/qrcode.js":
                        WriteResponse(stream, 200, "application/javascript; charset=utf-8", Res.Get("qrcode.js"));
                        break;
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
            if (code == 404) return "Not Found";
            if (code == 405) return "Method Not Allowed";
            if (code == 426) return "Upgrade Required";
            return "OK";
        }

        internal static void ReplaceActive(WsSession newer)
        {
            lock (SessionGate)
            {
                if (activeSession != null && activeSession != newer)
                {
                    Log.Info("新连接接入，断开旧连接");
                    activeSession.Kick();
                }
                activeSession = newer;
            }
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
        private JavaScriptSerializer json = new JavaScriptSerializer();

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
            TinyHttp.ReplaceActive(this);
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
                while (!closed)
                {
                    int opcode;
                    byte[] payload;
                    if (!ReadFrame(out opcode, out payload)) break;

                    if (opcode == 0x8) break;                    // close
                    if (opcode == 0x9)                            // ping → pong
                    {
                        SendFrame(0xA, payload);
                        continue;
                    }
                    if (opcode == 0xA) continue;                  // pong
                    if (opcode != 0x1 && opcode != 0x2) continue; // 只处理文本

                    string msg = Encoding.UTF8.GetString(payload);
                    if (!HandleMessage(msg)) break;
                }
            }
            catch (Exception) { }
            finally
            {
                closed = true;
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
                    uint sent = Inject.Text(text);
                    Log.Info("注入 " + text.Length + " 字符 (" + sent + " 事件)");
                    SendAck(true, text.Length);
                    return true;
                }
                case "enter":
                {
                    if (State.Paused) return true;
                    Inject.Key(0x0D, 1);
                    SendAck(true, 0);
                    return true;
                }
                case "backspace":
                {
                    if (State.Paused) return true;
                    int count = 1;
                    object c;
                    if (dict.TryGetValue("count", out c))
                    {
                        try { count = Convert.ToInt32(c); }
                        catch (Exception) { count = 1; }
                    }
                    if (count < 1) count = 1;
                    if (count > 10000) count = 10000;
                    Inject.Key(0x08, count);
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
                    if (State.Paused) return true;
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
                    SendAck(true, stext.Length);
                    return true;
                }
                default:
                    return true;    // 未知消息忽略，不断开
            }
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
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                closed = true;
            }
        }

        private bool ReadFrame(out int opcode, out byte[] payload)
        {
            opcode = 0;
            payload = null;
            int b0 = stream.ReadByte();
            if (b0 < 0) return false;
            int b1 = stream.ReadByte();
            if (b1 < 0) return false;
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

    // ---------------- 托盘 ----------------
    internal class TrayApp : ApplicationContext
    {
        private NotifyIcon tray;
        private MenuItem pauseItem;
        internal NotifyIcon Tray { get { return tray; } }

        private MenuItem startupItem;

        internal TrayApp(string ip, int port)
        {
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("链接二维码", delegate { OpenPair(); });
            menu.MenuItems.Add("重新生成配对码", delegate
            {
                Pairing.NewCode();
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
            startupItem = new MenuItem("开机自动启动", delegate
            {
                bool want = !AutoStart.IsEnabled();
                string err = AutoStart.Set(want);
                if (err != null)
                {
                    MessageBox.Show("设置开机启动失败: " + err, "TypeBridge");
                    return;
                }
                startupItem.Checked = AutoStart.IsEnabled();
                tray.ShowBalloonTip(1500, "TypeBridge", want ? "已开启开机自动启动" : "已关闭开机自动启动", ToolTipIcon.Info);
            });
            startupItem.Checked = AutoStart.IsEnabled();
            menu.MenuItems.Add(startupItem);
            menu.MenuItems.Add("查看日志", delegate { Log.OpenFile(); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate
            {
                tray.Visible = false;
                Environment.Exit(0);
            });

            tray = new NotifyIcon();
            tray.Icon = System.Drawing.SystemIcons.Application;
            tray.Text = "TypeBridge 配对码 " + Pairing.Code;
            tray.ContextMenu = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenPair(); };
        }

        private void OpenPair()
        {
            try
            {
                Process.Start("http://localhost:8765/pair");
            }
            catch (Exception ex)
            {
                Log.Info("打开浏览器失败: " + ex.Message);
            }
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

            // 单实例：端口已被占用 = 已有一个在跑，直接复用，不再重复启动
            if (TinyHttp.PortInUse(Port))
            {
                MessageBox.Show(
                    "TypeBridge 已经在运行了（右下角托盘图标里）。" + Environment.NewLine + Environment.NewLine
                    + "右键托盘图标选「链接二维码」即可重新打开扫码页。",
                    "TypeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            string ip = NetDetect.PickBestIpv4();
            if (ip == null)
            {
                MessageBox.Show("未找到可用的局域网 IP，请检查电脑是否连上网络。", "TypeBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            string code = Pairing.NewCode();

            Log.Info("========== TypeBridge v0.2 启动 ==========");
            Log.Info("本机局域网 IP: " + ip);
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
                int last = -1;
                while (true)
                {
                    int n = State.ClientCount;
                    if (n != last)
                    {
                        last = n;
                        string t = n > 0
                            ? "TypeBridge 手机已连接 (码 " + Pairing.Code + ")"
                            : "TypeBridge 等待扫码 (码 " + Pairing.Code + ")";
                        try { trayRef.Text = t; } catch (Exception) { }
                    }
                    Thread.Sleep(1000);
                }
            }));
            tick.IsBackground = true;
            tick.Start();

            // 服务就绪 → 自动弹出扫码页面
            OpenInBrowser("http://localhost:" + Port + "/pair");

            // 首次运行：询问开机自启 + 使用说明
            if (AutoStart.IsEnabled())
            {
                MessageBox.Show(
                    "TypeBridge 已在运行。" + Environment.NewLine + Environment.NewLine
                    + "占用内存很小，关掉浏览器窗口和电脑上的命令行都没关系，"
                    + "程序会一直在右下角托盘里待命。" + Environment.NewLine + Environment.NewLine
                    + "下次用手机直接扫码就能连上继续使用。",
                    "TypeBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                DialogResult dr = MessageBox.Show(
                    "TypeBridge 已启动，浏览器里扫码就能连。" + Environment.NewLine + Environment.NewLine
                    + "占用内存很小，下次可以直接扫码连接使用。" + Environment.NewLine + Environment.NewLine
                    + "是否加入开机自动启动？（推荐，开机后无需手动打开）",
                    "TypeBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    string err = AutoStart.Set(true);
                    if (err != null)
                        MessageBox.Show("设置开机启动失败: " + err, "TypeBridge",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            Application.Run(app);
            return 0;
        }

        private static void OpenInBrowser(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception ex)
            {
                Log.Info("打开浏览器失败: " + ex.Message);
            }
        }
    }
}
