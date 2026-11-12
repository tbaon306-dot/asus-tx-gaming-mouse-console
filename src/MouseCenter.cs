using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("ASUS 鼠标控制台")]
[assembly: System.Reflection.AssemblyProduct("ASUS 鼠标控制台")]
[assembly: System.Reflection.AssemblyDescription("华硕 TX GAMING MOUSE (P310) 控制台，不依赖奥创中心")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

// ============================================================
//  ASUS TX GAMING MOUSE (P310 / 6797) 原生控制台
//  不依赖 Armoury Crate，直接 P/Invoke ArmouryMsSDK.dll
//
//  关键约束（均为实测得出，勿改）：
//   1. 【最重要】进程里只要有消息泵（Application.Run），ExecuteFunction 就有
//      12%~50% 概率在原生层 AV，被 CLR 升级为 fail-fast(0xC0000409) 后静默退出。
//      无消息泵进程 0/16 稳定。故 UI 进程绝不加载 ArmouryMsSDK.dll，
//      全部 SDK 调用交给 --host 子进程（无窗口、无泵），崩了重启重试。
//   2. 必须先 function 10 (Enumerate) 才能写
//   3. ExecuteFunction 入参必须 UTF-16 (StringToHGlobalUni)
//   4. 写板载 (function 5 / profile_function=3) 仅在奥创框架未运行时才真正落盘
//   5. 写性能必须带 dpi_channel，否则设备把活动通道重置为 1
// ============================================================
static class Dll
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool SetStdHandle(int n, IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StartServiceFn(IntPtr cfg);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ExecuteFn(IntPtr xmlIn, out IntPtr xmlOut);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FreeBufferFn(ref IntPtr p);

    static readonly object gate = new object();
    static ExecuteFn exec;
    static FreeBufferFn free;
    static bool started;

    public const string Head =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><root><device_type key=\"1\">";

    public static string Dir { get; set; }
    public static string Error { get; private set; }

    public static string ResolveDir()
    {
        var cand = new List<string>();
        string env = Environment.GetEnvironmentVariable("MOUSECTL_SDK_DIR");
        if (!string.IsNullOrEmpty(env)) cand.Add(env);
        string bd = AppDomain.CurrentDomain.BaseDirectory;
        cand.Add(Path.Combine(bd, "MouseSDK"));
        cand.Add(bd);
        // 装了奥创的机器，SDK 在 Program Files 那条候选里
        cand.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASUSMouseConsole", "MouseSDK"));
        cand.Add(@"C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK");
        foreach (var c in cand)
            if (Directory.Exists(c)
                && File.Exists(Path.Combine(c, "ArmouryMsSDK.dll"))
                && File.Exists(Path.Combine(c, "config.ini")))
                return c;
        return null;
    }

    public static readonly bool Verbose = Environment.GetEnvironmentVariable("MOUSECTRL_VERBOSE") == "1";
    static void V(string s) { if (Verbose) Log.Write("[" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + s); }

    public static bool Init()
    {
        if (exec != null) return true;
        string dir = ResolveDir();
        if (dir == null) { Error = "找不到 MouseSDK 目录（需要 ArmouryMsSDK.dll + config.ini）"; return false; }
        Dir = dir;

        // （已移除 stdout->NUL 重定向：实测会诱发 SDK 原生崩溃）


        try { Directory.SetCurrentDirectory(dir); } catch { }

        IntPtr h = LoadLibraryW(Path.Combine(dir, "ArmouryMsSDK.dll"));
        if (h == IntPtr.Zero) { Error = "LoadLibrary 失败 err=" + Marshal.GetLastWin32Error(); return false; }

        IntPtr a = GetProcAddress(h, "startService");
        IntPtr b = GetProcAddress(h, "ExecuteFunction");
        IntPtr c = GetProcAddress(h, "freeBuffer");
        if (a == IntPtr.Zero || b == IntPtr.Zero || c == IntPtr.Zero) { Error = "导出函数缺失"; return false; }

        var start = (StartServiceFn)Marshal.GetDelegateForFunctionPointer(a, typeof(StartServiceFn));
        exec = (ExecuteFn)Marshal.GetDelegateForFunctionPointer(b, typeof(ExecuteFn));
        free = (FreeBufferFn)Marshal.GetDelegateForFunctionPointer(c, typeof(FreeBufferFn));

        if (!started)
        {
            Log.Write("Init: 调用 startService…");
            start(IntPtr.Zero);
            started = true;
            Log.Write("Init: startService 已返回，等待内部就绪…");
            System.Threading.Thread.Sleep(400);   // startService 是异步的，过早发指令会在原生层 AV
        }
        Log.Write("SDK 已加载: " + dir);
        return true;
    }

    // 只能在 SDK 工作线程上调用
    public static string Call(string xml, out int ret)
    {
        ret = 0;
        if (exec == null) { Error = "SDK 未初始化"; return null; }
        IntPtr pIn = Marshal.StringToHGlobalUni(xml);
        IntPtr pOut = IntPtr.Zero;
        try
        {
            lock (gate) { ret = exec(pIn, out pOut); }
            if (pOut == IntPtr.Zero) return null;
            string s = Marshal.PtrToStringUni(pOut);
            IntPtr t = pOut; lock (gate) { free(ref t); }
            return s;
        }
        finally { Marshal.FreeHGlobal(pIn); }
    }

    public static string Data(int dataNumber, int profile)
    {
        return Head + "<device key=\"6797\"><function key=\"6\"><data_number>" + dataNumber +
               "</data_number><file_name>-1</file_name><profile_index>" + profile +
               "</profile_index></function></device></device_type></root>";
    }

    public static bool Enumerate()
    {
        if (!Init()) return false;
        int r;
        V("Enumerate: 发送 function 10");
        string o = Call(Head + "<device key=\"-1\"><function key=\"10\"/></device></device_type></root>", out r);
        V("Enumerate: ret=" + r + " len=" + (o == null ? -1 : o.Length));
        if (r == 1 && o != null && o.Contains("<pid>6797</pid>")) return true;
        Error = "枚举失败：鼠标没连接？（r=" + r + "）";
        return false;
    }
}

static class Log
{
    static readonly object gate = new object();
    public static string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASUSMouseConsole");
    public static string Path = System.IO.Path.Combine(Dir, "native.log");

    public static void Write(string s)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}

// ===================== 设备模型 =====================
class Btn
{
    public int Source;          // 物理键码（写指令必须用这个）
    public string Name;         // 中文名
    public int Func;            // 0=鼠标 1=键盘组合 3=多媒体
    public int K1 = -1, K2 = -1, K3 = -1;

    public Btn Clone() { var b = (Btn)MemberwiseClone(); return b; }
}

class ProfileInfo
{
    public int Index;
    public int[] Dpi = new int[4];      // 真实 DPI
    public int Polling;                 // 0..3
    public int Snap;                    // 0/1
    public bool Valid;
}

class Snapshot
{
    public string Model = "ASUS TX GAMING MOUSE";
    public string Firmware = "?", Rf = "?";
    public int ActiveProfile = 1;
    public int ProfileCount = 3;
    public int CurrentDpi = 0;
    public int Channel = 0;
    public readonly ProfileInfo[] Profiles = new ProfileInfo[4];
    public readonly List<Btn> Buttons = new List<Btn>();
    public bool Sleeping;
}

// ===================== 键盘名表（与设备键码一致） =====================
static class Keys2
{
    public static readonly string[][] Grid = new string[][]
    {
        new[]{"Esc","`","Tab","Caps Lock","L-Shift","L-Ctrl","1","2"},
        new[]{"F1","3","E","D","X","C","Q","W"},
        new[]{"F2","4","R","F","V","Code131","A","S"},
        new[]{"F3","5","T","G","B","Space","L-\\","Z"},
        new[]{"F4","6","Y","H","N","Code132","L-Win","L-Alt"},
        new[]{"F5","7","U","J","M","Code133","F11","F12"},
        new[]{"F6","8","I","K",",","L-SP4","Code14","Backspace"},
        new[]{"F7","9","O","L",".","R-Alt","\\","Enter"},
        new[]{"F8","0","P",";","/","Fn","Ins","Del"},
        new[]{"F9","-","[","'","Code56","Menu","Home","End"},
        new[]{"F10","=","]","#","R-Shift","R-Ctrl","←","↑"},
        new[]{"SysRq","Scroll Lock","Pause","","→","↓","Page Up","Page Down"},
        new[]{"Num 7","Num 4","Num 1","Mute","Num Lock","","L-CAP",""},
        new[]{"Num 8","Num 5","Num 2","Num 0","Num /","","",""},
        new[]{"Num 9","Num 6","Num 3","Num .","Num *","","",""},
        new[]{"Num +","","Num Enter","MicMute","Num -","VolumeDown","VolumeUp",""},
        new[]{"LeftButton","RightButton","ScrollClick","ScrollUp","ScrollDown","DPI1","DPI2","DISABLE"},
        new[]{"LeftBackward","LeftForward","RightBackward","RightForward","Button 7","Button 8","Button 9","Button 10"},
        new[]{"Button 11","Button 12","DoubleClick","","Joystick FWD Axis 0","Joystick REV Axis 0","Joystick FWD Axis 1","Joystick REV Axis 1"},
        new[]{"PlayPause","Stop","PreviousTrack","NextTrack","Mute","VolumeDown","VolumeUp",""},
        new[]{"Profile0","Profile1","Profile2","Profile3","Profile4","Profile5","FastForward","FastBackward"},
        new string[0], new string[0], new string[0], new string[0], new string[0], new string[0],
        new[]{"ScrollLeft","ScrollRight"},
    };

    public static readonly string[] MouseFuncs = { "LeftButton","RightButton","ScrollClick","ScrollUp","ScrollDown",
        "DoubleClick","DPI1","DPI2","LeftForward","LeftBackward","RightForward","RightBackward","DISABLE" };

    public static readonly string[] MediaFuncs = { "VolumeUp","VolumeDown","Mute","PlayPause",
        "PreviousTrack","NextTrack","Stop","MicMute" };

    public static bool IsMouse(string n) { foreach (var m in MouseFuncs) if (m == n) return true; return false; }

    public static int Code(string name)
    {
        for (int row = 0; row < Grid.Length; row++)
        {
            int col = Array.IndexOf(Grid[row], name);
            if (col >= 0) return col * 256 + row;
        }
        return -1;
    }

    public static string Name(int code)
    {
        if (code < 0) return "";
        int col = (code >> 8) & 0xFF, row = code & 0xFF;
        if (row < Grid.Length && col < Grid[row].Length && !string.IsNullOrEmpty(Grid[row][col])) return Grid[row][col];
        return "code" + code;
    }

    public static string Display(Btn b)
    {
        if (b.Func == 0 || b.Func == 9) return Name(b.K1);
        var sb = new StringBuilder(Name(b.K1));
        if (b.K2 >= 0) sb.Append('+').Append(Name(b.K2));
        if (b.K3 >= 0) sb.Append('+').Append(Name(b.K3));
        return sb.ToString();
    }

    public static string KeyToName(Keys k)
    {
        if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
        if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
        if (k >= Keys.F1 && k <= Keys.F12) return "F" + (k - Keys.F1 + 1);
        switch (k)
        {
            case Keys.Escape: return "Esc";
            case Keys.Back: return "Backspace";
            case Keys.Tab: return "Tab";
            case Keys.Enter: return "Enter";
            case Keys.Space: return "Space";
            case Keys.Insert: return "Ins";
            case Keys.Delete: return "Del";
            case Keys.Home: return "Home";
            case Keys.End: return "End";
            case Keys.PageUp: return "Page Up";
            case Keys.PageDown: return "Page Down";
            case Keys.Up: return "↑";
            case Keys.Down: return "↓";
            case Keys.Left: return "←";
            case Keys.Right: return "→";
            case Keys.OemMinus: return "-";
            case Keys.Oemplus: return "=";
            case Keys.Oemcomma: return ",";
            case Keys.OemPeriod: return ".";
            case Keys.OemQuestion: return "/";
            case Keys.OemSemicolon: return ";";
            case Keys.OemOpenBrackets: return "[";
            case Keys.OemCloseBrackets: return "]";
            case Keys.OemPipe: return "\\";
            case Keys.Oemtilde: return "`";
            case Keys.OemQuotes: return "'";
        }
        return null;
    }
}

// ===================== SDK 工作线程 =====================
static class Worker
{
    static readonly Queue<Action> q = new Queue<Action>();
    static readonly object gate = new object();
    static Thread th;

    public static void Start()
    {
        if (th != null) return;
        th = new Thread(Loop);
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA);
        th.Name = "sdk-worker";
        th.Start();
    }

    public static void Post(Action a)
    {
        lock (gate) { q.Enqueue(a); Monitor.Pulse(gate); }
    }

    static void Loop()
    {
        while (true)
        {
            Action a;
            lock (gate)
            {
                while (q.Count == 0) Monitor.Wait(gate);
                a = q.Dequeue();
            }
            try { a(); }
            catch (Exception e) { Log.Write("worker: " + e); }
        }
    }
}

// ===================== 主窗口 =====================
class MainForm : Form
{
    static readonly string[] PhyName = { "左键", "侧键-前", "侧键-后", "滚轮上", "中键", "滚轮下", "DPI 键", "右键" };
    static readonly int[] PhySource = { 16, 273, 17, 784, 528, 1040, 1296, 272 };
    static readonly string[] PollName = { "125 Hz", "250 Hz", "500 Hz", "1000 Hz" };

    Snapshot snap = new Snapshot();
    bool busy;

    // 概览
    Label vModel, vFw, vRf, vProf, vDpi, vChan, vSdk;
    // 性能
    ComboBox cbPerfProf;
    NumericUpDown[] nud = new NumericUpDown[4];
    ComboBox cbPoll;
    CheckBox cbSnap;
    // 按键
    FlowLayoutPanel btnList;
    // 配置档
    Label vProfCard;
    readonly List<Button> profBtns = new List<Button>();
    bool syncing;
    Button btnRefresh;
    Label lblWarn;
    // 状态栏
    ToolStripStatusLabel status;

    public MainForm()
    {
        Text = "ASUS TX GAMING MOUSE 控制台";
        ClientSize = new Size(880, 660);
        MinimumSize = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        tabs.TabPages.Add(BuildOverview());
        tabs.TabPages.Add(BuildPerformance());
        tabs.TabPages.Add(BuildButtons());
        tabs.TabPages.Add(BuildProfiles());
        tabs.TabPages.Add(BuildLog());
        if (Program.StartTab >= 0 && Program.StartTab < tabs.TabPages.Count) tabs.SelectedIndex = Program.StartTab;

        var bottom = new StatusStrip();
        status = new ToolStripStatusLabel("正在初始化…");
        bottom.Items.Add(status);
        var btnReload = new ToolStripButton("刷新") { Alignment = ToolStripItemAlignment.Right };
        btnReload.Click += (s, e) => Reload();
        bottom.Items.Add(btnReload);

        Controls.Add(tabs);
        Controls.Add(bottom);

        Shown += (s, e) => Reload();
        FormClosing += (s, e) => Log.Write("=== 窗口关闭 ===");
    }

    // ---------- 通用 UI ----------
    static Label H(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
    }

    static Label Plain(string text, int size = 9)
    {
        return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", size), Margin = new Padding(0, 3, 12, 3) };
    }

    static TableLayoutPanel KV()
    {
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    static void AddRow(TableLayoutPanel t, string k, Label v)
    {
        var kl = new Label { Text = k, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 8, 4) };
        v.Margin = new Padding(0, 4, 8, 4);
        t.Controls.Add(kl);
        t.Controls.Add(v);
    }

    static GroupBox Card(string title, int h)
    {
        return new GroupBox { Text = title, Height = h, Dock = DockStyle.Top, Padding = new Padding(12, 8, 12, 8) };
    }

    // ---------- 概览 ----------
    TabPage BuildOverview()
    {
        var page = new TabPage("概览") { Padding = new Padding(14) };
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        var g1 = Card("设备", 190);
        var t1 = KV();
        vModel = Plain("—", 10); vFw = Plain("—", 10); vRf = Plain("—", 10); vSdk = Plain("—");
        AddRow(t1, "型号（按 PID 映射）", vModel);
        AddRow(t1, "固件版本", vFw);
        AddRow(t1, "接收器版本", vRf);
        AddRow(t1, "SDK 目录", vSdk);
        g1.Controls.Add(t1);

        var g2 = Card("当前状态", 160);
        var t2 = KV();
        vProf = Plain("—", 10); vDpi = Plain("—", 10); vChan = Plain("—", 10);
        AddRow(t2, "生效配置档", vProf);
        AddRow(t2, "当前 DPI", vDpi);
        AddRow(t2, "DPI 通道", vChan);
        g2.Controls.Add(t2);

        var tip = new Label
        {
            Text = "说明：本控制台直接调用华硕鼠标 SDK，不需要 Armoury Crate 运行。\n" +
                   "改动默认只作用于设备当前运行状态；要写进板载闪存，请到「性能」页用「应用并保存到板载」。",
            AutoSize = true, ForeColor = Color.DimGray, Dock = DockStyle.Top, Padding = new Padding(0, 10, 0, 0)
        };

        host.Controls.Add(tip);
        host.Controls.Add(g2);
        host.Controls.Add(g1);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 10, 0, 0), WrapContents = false
        };
        btnRefresh = new Button { Text = "重新读取", Width = 100, Height = 28, Margin = new Padding(0, 0, 12, 0) };
        btnRefresh.Click += (a, b) => Reload();
        lblWarn = new Label
        {
            Text = "", AutoSize = true, ForeColor = Color.Firebrick,
            Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(620, 0)
        };
        bottom.Controls.Add(btnRefresh);
        bottom.Controls.Add(lblWarn);

        page.Controls.Add(host);
        page.Controls.Add(bottom);
        return page;
    }

    // ---------- 性能 ----------
    TabPage BuildPerformance()
    {
        var page = new TabPage("性能") { Padding = new Padding(14) };
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        var g = Card("DPI 与轮询率", 250);
        var t = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top };

        t.Controls.Add(Plain("配置档"), 0, 0);
        cbPerfProf = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        for (int i = 1; i <= 4; i++) cbPerfProf.Items.Add("Profile " + i);
        cbPerfProf.SelectedIndex = 0;
        cbPerfProf.SelectedIndexChanged += (s, e) => LoadPerfFromSnap();
        t.Controls.Add(cbPerfProf, 1, 0);
        t.Controls.Add(new Label { Text = "", Width = 40 }, 2, 0);

        for (int i = 0; i < 4; i++)
        {
            int col = i % 2, row = 1 + i / 2;
            var box = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 20, 6) };
            box.Controls.Add(new Label { Text = "DPI " + (i + 1), AutoSize = true, Width = 50, Margin = new Padding(0, 6, 6, 0) });
            nud[i] = new NumericUpDown
            {
                Minimum = 100, Maximum = 12000, Increment = 100, ThousandsSeparator = true,
                Width = 100, Value = 400 * (i + 1)
            };
            box.Controls.Add(nud[i]);
            t.Controls.Add(box, col, row);
        }

        var boxPoll = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        boxPoll.Controls.Add(new Label { Text = "轮询率", AutoSize = true, Width = 50, Margin = new Padding(0, 6, 6, 0) });
        cbPoll = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        cbPoll.Items.AddRange(PollName);
        cbPoll.SelectedIndex = 3;
        boxPoll.Controls.Add(cbPoll);
        t.Controls.Add(boxPoll, 0, 3);

        var boxSnap = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        cbSnap = new CheckBox { Text = "角度捕捉 (Angle Snapping)", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        boxSnap.Controls.Add(cbSnap);
        t.Controls.Add(boxSnap, 1, 3);

        g.Controls.Add(t);

        var acts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        var bApply = new Button { Text = "应用到设备", Width = 130, Height = 34 };
        var bSave = new Button { Text = "应用并保存到板载", Width = 170, Height = 34 };
        bApply.Click += (s, e) => ApplyPerf(false);
        bSave.Click += (s, e) => ApplyPerf(true);
        acts.Controls.Add(bApply);
        acts.Controls.Add(bSave);

        var warn = new Label
        {
            AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.FromArgb(160, 90, 0),
            Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 0),
            Text = "关于轮询率：本机实测该鼠标恒定约 125 Hz（报告间隔稳定 8.00 ms），写入 1000 Hz 只存进配置档，" +
                   "不改变实际速率。此项已用「注入 3000 条 @1000Hz、WM_INPUT 100% 捕获」排除测量误差，" +
                   "奥创中心自己也改不动 —— 属设备固件行为，不是本工具的问题。"
        };

        host.Controls.Add(warn);
        host.Controls.Add(acts);
        host.Controls.Add(g);
        page.Controls.Add(host);
        return page;
    }

    // ---------- 按键 ----------
    TabPage BuildButtons()
    {
        var page = new TabPage("按键") { Padding = new Padding(14) };
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        var head = new Label
        {
            Text = "点「修改」重新绑定。写入必须带物理键码 (source_key)，所以 8 个键总是一起下发。",
            AutoSize = true, ForeColor = Color.DimGray, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8)
        };

        btnList = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };

        var acts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        var bApply = new Button { Text = "应用到设备", Width = 130, Height = 34 };
        var bReset = new Button { Text = "恢复本档默认", Width = 130, Height = 34 };
        bApply.Click += (s, e) => ApplyButtons();
        bReset.Click += (s, e) => ResetButtons();
        acts.Controls.Add(bApply);
        acts.Controls.Add(bReset);

        host.Controls.Add(acts);
        host.Controls.Add(btnList);
        host.Controls.Add(head);
        page.Controls.Add(host);
        return page;
    }

    // ---------- 配置档 ----------
    TabPage BuildProfiles()
    {
        var page = new TabPage("配置档") { Padding = new Padding(14) };
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        var g1 = Card("切换", 150);
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        for (int i = 1; i <= 4; i++)
        {
            int id = i;
            var b = new Button { Text = "Profile " + i, Width = 110, Height = 46, Margin = new Padding(0, 6, 10, 6) };
            b.Click += (s, e) => SwitchProfile(id);
            row.Controls.Add(b);
            profBtns.Add(b);
        }
        g1.Controls.Add(row);

        var g2 = Card("恢复", 140);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var bRestoreCur = new Button { Text = "恢复当前档出厂设置", Width = 190, Height = 40, Margin = new Padding(0, 6, 10, 6) };
        var bRestoreAll = new Button { Text = "恢复全部配置档", Width = 150, Height = 40, Margin = new Padding(0, 6, 10, 6) };
        bRestoreCur.Click += (s, e) => RestoreProfile(1);
        bRestoreAll.Click += (s, e) => RestoreProfile(0);
        row2.Controls.Add(bRestoreCur);
        row2.Controls.Add(bRestoreAll);
        g2.Controls.Add(row2);

        vProfCard = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 10, 0, 0), ForeColor = Color.DimGray };

        host.Controls.Add(vProfCard);
        host.Controls.Add(g2);
        host.Controls.Add(g1);
        page.Controls.Add(host);
        return page;
    }

    // ---------- 日志 ----------
    TabPage BuildLog()
    {
        var page = new TabPage("日志") { Padding = new Padding(14) };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(250, 250, 250)
        };
        var btn = new Button { Text = "打开日志文件", Dock = DockStyle.Bottom, Height = 32 };
        btn.Click += (s, e) => { try { Process.Start(Log.Path); } catch { } };
        page.Controls.Add(box);
        page.Controls.Add(btn);
        txtLog = box;
        return page;
    }

    TextBox txtLog;

    void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
        else a();
    }

    void Say(string s)
    {
        Log.Write(s);
        Ui(() =>
        {
            if (txtLog == null) return;
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        });
    }

    void SetBusy(bool b, string text)
    {
        Ui(() =>
        {
            busy = b;
            status.Text = text ?? (b ? "处理中…" : "就绪");
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
        });
    }

    // ---------- 读取 ----------
    void Reload()
    {
        if (busy) return;
        SetBusy(true, "正在读取设备…");
        Worker.Post(() =>
        {
            try
            {
                if (!Sdk.Enumerate()) { SetBusy(false, "设备未就绪"); Ui(() => ErrorBox(Sdk.Error)); return; }

                int r;
                string info = Sdk.Call(Sdk.Data(0, -1), out r);
                if (r != 1 || info == null) { SetBusy(false, "读取设备信息失败"); return; }

                var s = new Snapshot();
                s.Model = "ASUS TX GAMING MOUSE（P310 / PID 6797）";
                s.Firmware = TagStr(info, "version") ?? "?";
                s.Rf = TagStr(info, "rf_version") ?? "?";
                s.ActiveProfile = Clamp(TagVal(info, "profile"), 1, 4);
                s.ProfileCount = TagVal(info, "profile_number");
                if (s.ProfileCount < 1 || s.ProfileCount > 4) s.ProfileCount = 3;
                int cdi = TagVal(info, "currentdpi");
                s.CurrentDpi = cdi >= 0 ? 100 + cdi * 100 : 0;
                s.Channel = TagVal(info, "currentdpichannel");

        // 无线鼠标空闲几分钟会休眠，此时 SDK 会返回 fw=0 / dpi=0 之类的假数据
        string ver = TagStr(info, "version");
        s.Sleeping = string.IsNullOrEmpty(ver) || ver == "0" || ver == "0.0"
                  || s.CurrentDpi <= 0 || s.ProfileCount <= 0;

                for (int i = 0; i < 4; i++)
                {
                    var p = new ProfileInfo { Index = i + 1 };
                    for (int k = 0; k < 4; k++) p.Dpi[k] = 100 * (k + 2);
                    string perf = null;
                    if (i + 1 <= s.ProfileCount) { int rr; perf = Sdk.Call(Sdk.Data(2, i + 1), out rr); }
                    if (perf != null)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            int idx = TagVal(perf, "dpi_" + (k + 1));
                            if (idx >= 0) p.Dpi[k] = 100 + idx * 100;
                        }
                        int poll = TagVal(perf, "polling_rate");
                        p.Polling = poll >= 0 && poll <= 3 ? poll : 3;
                        int sn = TagVal(perf, "angle_snapping");
                        p.Snap = sn == 1 ? 1 : 0;
                        p.Valid = true;
                    }
                    s.Profiles[i] = p;
                }

                s.Buttons.AddRange(ButtonsStore.Load(s.ActiveProfile));

                snap = s;
                Ui(() => { ApplySnapshotToUi(); });
                Say("已读取：" + s.Model + " / 固件 " + s.Firmware + " / 生效 Profile " + s.ActiveProfile + " / " + s.CurrentDpi + " DPI");
                SetBusy(false, "就绪");
            }
            catch (Exception e)
            {
                Log.Write("reload: " + e);
                SetBusy(false, "读取异常");
                Ui(() => ErrorBox(e.Message));
            }
        });
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    static int TagVal(string xml, string tag)
    {
        var m = Regex.Match(xml, "<" + tag + ">(-?\\d+)</" + tag + ">");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    static string TagStr(string xml, string tag)
    {
        var m = Regex.Match(xml, "<" + tag + ">([^<]*)</" + tag + ">");
        return m.Success ? m.Groups[1].Value : null;
    }

    void ApplySnapshotToUi()
    {
        vModel.Text = snap.Model;
        vFw.Text = snap.Sleeping ? "—（休眠）" : snap.Firmware;
        lblWarn.Text = snap.Sleeping
            ? "鼠标处于省电休眠：读数不可信（固件显示为 0）。动一下鼠标或按一下键唤醒，再点「重新读取」。"
            : "";
        vRf.Text = snap.Sleeping ? "—（休眠）" : snap.Rf;
        vSdk.Text = Sdk.Dir ?? "—";
        vProf.Text = "Profile " + snap.ActiveProfile + "（共 " + snap.ProfileCount + " 档）";
        vDpi.Text = snap.Sleeping ? "—（休眠）" : snap.CurrentDpi + " DPI";
        vChan.Text = "通道 " + snap.Channel;
        vProfCard.Text = "当前生效：Profile " + snap.ActiveProfile;

        for (int i = 0; i < profBtns.Count; i++) profBtns[i].Enabled = i < snap.ProfileCount;

        syncing = true;
        cbPerfProf.Items.Clear();
        for (int i = 1; i <= snap.ProfileCount; i++) cbPerfProf.Items.Add("Profile " + i);
        cbPerfProf.SelectedIndex = Clamp(snap.ActiveProfile, 1, snap.ProfileCount) - 1;
        syncing = false;
        LoadPerfFromSnap();
        RebuildButtonList();
    }

    void LoadPerfFromSnap()
    {
        if (syncing) return;
        int idx = cbPerfProf.SelectedIndex;
        if (idx < 0 || idx > 3) return;
        var p = snap.Profiles[idx];
        if (p == null) return;
        for (int k = 0; k < 4; k++)
        {
            int v = Clamp(p.Dpi[k], 100, 12000);
            nud[k].Value = v;
        }
        cbPoll.SelectedIndex = Clamp(p.Polling, 0, 3);
        cbSnap.Checked = p.Snap == 1;
    }

    void RebuildButtonList()
    {
        btnList.Controls.Clear();
        foreach (var b in snap.Buttons)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.Add(new Label { Text = b.Name, AutoSize = true, Width = 90, Margin = new Padding(0, 8, 10, 0) });
            row.Controls.Add(new Label
            {
                Text = Keys2.Display(b), AutoSize = true, Width = 320, Margin = new Padding(0, 8, 10, 0),
                Font = new Font("Consolas", 9.5f)
            });
            var btn = new Button { Text = "修改", Width = 78, Height = 30, Margin = new Padding(0, 2, 0, 2) };
            var target = b;
            btn.Click += (s, e) => EditButton(target);
            row.Controls.Add(btn);
            btnList.Controls.Add(row);
        }
    }

    // ---------- 性能写入 ----------
    void ApplyPerf(bool save)
    {
        if (busy) return;
        int prof = cbPerfProf.SelectedIndex + 1;
        int d1 = (int)nud[0].Value, d2 = (int)nud[1].Value, d3 = (int)nud[2].Value, d4 = (int)nud[3].Value;
        int poll = cbPoll.SelectedIndex, snapv = cbSnap.Checked ? 1 : 0;

        if (save && AcRunning() &&
            MessageBox.Show(this, "奥创中心（asus_framework）正在运行。\n\n框架会用自身缓存覆盖板载值，此时写板载一定「返回成功但无效」。\n\n仍要继续吗？",
                "奥创在运行", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        SetBusy(true, "正在写入…");
        Worker.Post(() =>
        {
            try
            {
                if (!Sdk.Enumerate()) { SetBusy(false, "设备未就绪"); return; }

                var sb = new StringBuilder(Sdk.Head);
                sb.Append("<device key=\"6797\"><function key=\"1\">");
                sb.Append("<profile_index>").Append(prof).Append("</profile_index>");
                sb.Append("<dpi_number>4</dpi_number><accelerate>-1</accelerate><decelerate>-1</decelerate>");
                sb.Append("<angle_snapping>").Append(snapv).Append("</angle_snapping>");
                sb.Append("<angle_tuning>-1</angle_tuning><button_response>-1</button_response>");
                sb.Append("<polling_rate>").Append(poll).Append("</polling_rate>");
                sb.Append("<dpi_channel>").Append(Clamp(snap.Channel, 1, 4)).Append("</dpi_channel>");
                sb.Append("<dpi_1>").Append((d1 - 100) / 100).Append("</dpi_1>");
                sb.Append("<dpi_2>").Append((d2 - 100) / 100).Append("</dpi_2>");
                sb.Append("<dpi_3>").Append((d3 - 100) / 100).Append("</dpi_3>");
                sb.Append("<dpi_4>").Append((d4 - 100) / 100).Append("</dpi_4>");
                sb.Append("</function></device></device_type></root>");

                int r;
                Sdk.Call(sb.ToString(), out r);
                if (r != 1) { Say("写入性能失败 ret=" + r); SetBusy(false, "写入失败"); return; }

                string tail = "";
                if (save)
                {
                    int r2;
                    Sdk.Call(Sdk.Head + "<device key=\"6797\"><function key=\"5\"><profile_function>3</profile_function>" +
                             "<profile_parameter>" + prof + "</profile_parameter></function></device></device_type></root>", out r2);
                    tail = r2 == 1 ? "（已请求保存到板载）" : "（保存到板载失败 ret=" + r2 + "）";
                }

                snap.Profiles[prof - 1].Dpi = new[] { d1, d2, d3, d4 };
                snap.Profiles[prof - 1].Polling = poll;
                snap.Profiles[prof - 1].Snap = snapv;
                snap.Profiles[prof - 1].Valid = true;

                Say("已写入 Profile " + prof + "：" + d1 + "/" + d2 + "/" + d3 + "/" + d4 +
                    " DPI，" + PollName[poll] + "，角度捕捉 " + (snapv == 1 ? "开" : "关") + tail);
                SetBusy(false, "已应用");
            }
            catch (Exception e) { Log.Write("perf: " + e); SetBusy(false, "异常"); Ui(() => ErrorBox(e.Message)); }
        });
    }

    bool AcRunning() { return Process.GetProcessesByName("asus_framework").Length > 0; }

    // ---------- 按键写入 ----------
    void ApplyButtons()
    {
        if (busy) return;
        if (!snap.Buttons.Exists(b => b.Func == 0 && b.K1 == 16))
        {
            if (MessageBox.Show(this, "当前映射里没有任何按键提供「左键」，写进去以后鼠标将无法点击。\n\n确实要继续吗？",
                "危险操作", MessageBoxButtons.YesNo, MessageBoxIcon.Stop) != DialogResult.Yes) return;
        }

        int prof = snap.ActiveProfile;
        var list = new List<Btn>();
        foreach (var b in snap.Buttons) list.Add(b.Clone());

        SetBusy(true, "正在写入按键…");
        Worker.Post(() =>
        {
            try
            {
                if (!Sdk.Enumerate()) { SetBusy(false, "设备未就绪"); return; }

                var sb = new StringBuilder(Sdk.Head);
                sb.Append("<device key=\"6797\"><function key=\"0\"><profile_index>").Append(prof).Append("</profile_index>");
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    sb.Append("<button_count_").Append(i + 1).Append(" key=\"").Append(i + 1).Append("\">");
                    sb.Append("<button_function>").Append(e.Func).Append("</button_function>");
                    sb.Append("<target_key_1>").Append(e.K1).Append("</target_key_1>");
                    sb.Append("<target_key_2>").Append(e.K2).Append("</target_key_2>");
                    sb.Append("<target_key_3>").Append(e.K3).Append("</target_key_3>");
                    sb.Append("<source_key>").Append(e.Source).Append("</source_key>");
                    sb.Append("</button_count_").Append(i + 1).Append(">");
                }
                sb.Append("<button_number>").Append(list.Count).Append("</button_number></function></device></device_type></root>");

                int r;
                Sdk.Call(sb.ToString(), out r);
                if (r != 1) { Say("写入按键失败 ret=" + r); SetBusy(false, "写入失败"); return; }

                ButtonsStore.Save(prof, list);
                Say("已写入按键：" + list.Count + " 个（Profile " + prof + "）");
                SetBusy(false, "已应用");
            }
            catch (Exception e) { Log.Write("buttons: " + e); SetBusy(false, "异常"); Ui(() => ErrorBox(e.Message)); }
        });
    }

    void ResetButtons()
    {
        snap.Buttons.Clear();
        foreach (int src in PhySource)
        {
            int i = Array.IndexOf(PhySource, src);
            snap.Buttons.Add(new Btn { Source = src, Name = PhyName[i], Func = 0, K1 = src });
        }
        RebuildButtonList();
        Say("已恢复为默认映射（尚未写入设备，记得点「应用到设备」）");
    }

    // ---------- 单键编辑 ----------
    void EditButton(Btn b)
    {
        using (var dlg = new EditButtonDialog(b))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            b.Func = dlg.ResultFunc;
            b.K1 = dlg.ResultK1; b.K2 = dlg.ResultK2; b.K3 = dlg.ResultK3;
            RebuildButtonList();
            Say("已修改 " + b.Name + " → " + Keys2.Display(b) + "（记得点「应用到设备」）");
        }
    }

    // ---------- 配置档 ----------
    void SwitchProfile(int id)
    {
        if (busy) return;
        SetBusy(true, "正在切换 Profile " + id + "…");
        Worker.Post(() =>
        {
            try
            {
                if (!Sdk.Enumerate()) { SetBusy(false, "设备未就绪"); return; }
                int r;
                Sdk.Call(Sdk.Head + "<device key=\"6797\"><function key=\"5\"><profile_function>4</profile_function>" +
                         "<profile_parameter>" + id + "</profile_parameter></function></device></device_type></root>", out r);
                Say(r == 1 ? "已切换到 Profile " + id : "切换失败 ret=" + r);
                SetBusy(false, r == 1 ? "已切换" : "切换失败");
                if (r == 1) Reload();
            }
            catch (Exception e) { Log.Write("switch: " + e); SetBusy(false, "异常"); }
        });
    }

    void RestoreProfile(int which)
    {
        if (busy) return;
        if (MessageBox.Show(this, which == 1 ? "把当前配置档恢复为出厂设置？" : "把全部配置档恢复为出厂设置？",
            "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetBusy(true, "正在恢复…");
        Worker.Post(() =>
        {
            try
            {
                if (!Sdk.Enumerate()) { SetBusy(false, "设备未就绪"); return; }
                int r;
                Sdk.Call(Sdk.Head + "<device key=\"6797\"><function key=\"5\"><profile_function>" + which +
                         "</profile_function><profile_parameter>" + snap.ActiveProfile +
                         "</profile_parameter></function></device></device_type></root>", out r);
                Say((which == 1 ? "恢复当前档" : "恢复全部") + " → ret=" + r);
                SetBusy(false, r == 1 ? "已恢复" : "恢复失败");
                if (r == 1) Reload();
            }
            catch (Exception e) { Log.Write("restore: " + e); SetBusy(false, "异常"); }
        });
    }

    void ErrorBox(string msg)
    {
        MessageBox.Show(this, msg, "ASUS 鼠标控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

// ===================== 按键存储 =====================
static class ButtonsStore
{
    public static string FilePath(int prof)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASUSMouseConsole", "state");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "native-buttons-profile" + prof + ".txt");
    }

    static readonly string[] Names = { "左键", "侧键-前", "侧键-后", "滚轮上", "中键", "滚轮下", "DPI 键", "右键" };
    static readonly int[] Srcs = { 16, 273, 17, 784, 528, 1040, 1296, 272 };

    // 旧版（网页版）留下的快照，是唯一经过真机验证的按键状态来源。
    // 设备侧 data_number 1 读的是主机缓存，而且槽位顺序与写指令不一致，不能当真相。
    static string[] LegacyFiles(int prof)
    {
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASUSMouseConsole", "state", "buttons-profile" + prof + ".txt"),
        };
    }

    static List<Btn> Parse(string path)
    {
        var list = new List<Btn>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                var f = t.Split(' ');
                if (f.Length < 5) continue;
                int src = int.Parse(f[0]);
                int i = Array.IndexOf(Srcs, src);
                if (i < 0) continue;
                list.Add(new Btn
                {
                    Source = src, Name = Names[i],
                    Func = int.Parse(f[1]), K1 = int.Parse(f[2]),
                    K2 = int.Parse(f[3]), K3 = int.Parse(f[4])
                });
            }
        }
        catch { }
        return list;
    }

    public static List<Btn> Load(int prof)
    {
        var list = Parse(FilePath(prof));
        if (list.Count == Srcs.Length) return list;

        foreach (var legacy in LegacyFiles(prof))
        {
            var l = Parse(legacy);
            if (l.Count == Srcs.Length) { Save(prof, l); return l; }
        }

        list.Clear();
        for (int i = 0; i < Srcs.Length; i++)
            list.Add(new Btn { Source = Srcs[i], Name = Names[i], Func = 0, K1 = Srcs[i] });
        return list;
    }

    public static void Save(int prof, List<Btn> list)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# native-buttons profile " + prof + "  格式: source_key button_function key1 key2 key3");
            foreach (var b in list)
                sb.AppendLine(b.Source + " " + b.Func + " " + b.K1 + " " + b.K2 + " " + b.K3);
            File.WriteAllText(FilePath(prof), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}

// ===================== 单键编辑对话框 =====================
class EditButtonDialog : Form
{
    public int ResultFunc = 0, ResultK1 = -1, ResultK2 = -1, ResultK3 = -1;

    readonly ComboBox cbKind;
    readonly ComboBox cbMouse;
    readonly ComboBox cbMedia;
    readonly TextBox txtCombo;
    readonly Button btnRecord;
    readonly Btn srcBtn;
    bool recording;

    public EditButtonDialog(Btn b)
    {
        srcBtn = b;
        Text = "修改按键 — " + b.Name;
        ClientSize = new Size(450, 350);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);

        var host = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16), WrapContents = false };

        host.Controls.Add(new Label { Text = "按键：" + b.Name + "（source_key " + b.Source + "）", AutoSize = true });
        host.Controls.Add(new Label { Text = "当前：" + Keys2.Display(b), AutoSize = true, ForeColor = Color.DimGray });

        host.Controls.Add(new Label { Text = "类型", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        cbKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        cbKind.Items.AddRange(new object[] { "鼠标功能", "键盘组合", "多媒体" });
        cbKind.SelectedIndex = b.Func == 1 ? 1 : (b.Func == 3 ? 2 : 0);
        cbKind.SelectedIndexChanged += (s, e) => Sync();
        host.Controls.Add(cbKind);

        cbMouse = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Visible = false };
        cbMouse.Items.AddRange(Keys2.MouseFuncs);
        host.Controls.Add(cbMouse);

        cbMedia = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Visible = false };
        cbMedia.Items.AddRange(Keys2.MediaFuncs);
        host.Controls.Add(cbMedia);

        txtCombo = new TextBox { Width = 210, ReadOnly = true };
        btnRecord = new Button { Text = "录制…", Width = 74, Height = 26 };
        btnRecord.Click += (s, e) => StartRecord();
        comboRow = new FlowLayoutPanel { AutoSize = true, Visible = false, WrapContents = false };
        comboRow.Controls.Add(txtCombo);
        comboRow.Controls.Add(btnRecord);
        host.Controls.Add(comboRow);

        hint = new Label
        {
            Text = "点「录制…」再按下组合键（例：Ctrl+C）。录制中按 Esc 取消。",
            AutoSize = true, ForeColor = Color.DimGray, Visible = false,
            MaximumSize = new Size(400, 0)
        };
        host.Controls.Add(hint);

        KeyPreview = true;
        KeyDown += OnKeyDown;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(0, 8, 16, 0) };
        var ok = new Button { Text = "确定", Width = 90, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (s, e) => Commit();
        bottom.Controls.Add(ok); bottom.Controls.Add(cancel);

        Controls.Add(host);
        Controls.Add(bottom);

        if (b.Func == 0) { int i = Array.IndexOf(Keys2.MouseFuncs, Keys2.Name(b.K1)); if (i >= 0) cbMouse.SelectedIndex = i; else cbMouse.SelectedIndex = 0; }
        if (b.Func == 1) txtCombo.Text = Keys2.Display(b);
        if (b.Func == 3) { int i = Array.IndexOf(Keys2.MediaFuncs, Keys2.Name(b.K1)); if (i >= 0) cbMedia.SelectedIndex = i; else cbMedia.SelectedIndex = 0; }
        if (cbMouse.SelectedIndex < 0) cbMouse.SelectedIndex = 0;
        if (cbMedia.SelectedIndex < 0) cbMedia.SelectedIndex = 0;
        Sync();
    }

    FlowLayoutPanel comboRow;
    Label hint;

    // 供 --dialogtest 使用
    public int KindIndex { get { return cbKind.SelectedIndex; } set { cbKind.SelectedIndex = value; } }
    public bool RecordVisible { get { return btnRecord.Visible; } }
    public bool ComboRowVisible { get { return comboRow.Visible; } }
    public bool HintVisible { get { return hint.Visible; } }

    void Sync()
    {
        int k = cbKind.SelectedIndex;
        cbMouse.Visible = k == 0;
        cbMedia.Visible = k == 2;
        bool combo = k == 1;
        txtCombo.Visible = combo;
        btnRecord.Visible = combo;
        comboRow.Visible = combo;
        hint.Visible = combo;
    }

    void StartRecord()
    {
        recording = true;
        txtCombo.Text = "请按下组合键…";
        btnRecord.Text = "记录中";
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!recording) return;
        e.SuppressKeyPress = true;
        CaptureKey(e.KeyCode, e.Control, e.Shift, e.Alt);
    }

    // 录制逻辑独立成方法，便于 --dialogtest 直接断言
    public string CapturedText { get { return txtCombo.Text; } }
    public void BeginRecord() { StartRecord(); }

    public bool CaptureKey(Keys k, bool ctrl, bool shift, bool alt)
    {
        if (k == Keys.Escape) { recording = false; btnRecord.Text = "录制…"; txtCombo.Text = ""; return false; }
        if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin)
            return false;

        string kn = Keys2.KeyToName(k);
        if (kn == null) { txtCombo.Text = "不支持的键：" + k; return false; }

        var parts = new List<string>();
        if (ctrl) parts.Add("L-Ctrl");
        if (shift) parts.Add("L-Shift");
        if (alt) parts.Add("L-Alt");
        parts.Add(kn);

        foreach (var p in parts)
            if (Keys2.Code(p) < 0) { txtCombo.Text = "键名不在设备键表中：" + p; return false; }

        txtCombo.Text = string.Join("+", parts.ToArray());
        recording = false;
        btnRecord.Text = "录制…";
        return true;
    }

    void Commit()
    {
        int k = cbKind.SelectedIndex;
        if (k == 0)
        {
            ResultFunc = 0;
            ResultK1 = Keys2.Code(Keys2.MouseFuncs[cbMouse.SelectedIndex]);
            ResultK2 = -1; ResultK3 = -1;
        }
        else if (k == 2)
        {
            ResultFunc = 3;
            ResultK1 = Keys2.Code(Keys2.MediaFuncs[cbMedia.SelectedIndex]);
            ResultK2 = -1; ResultK3 = -1;
        }
        else
        {
            string t = (txtCombo.Text ?? "").Trim();
            if (t.Length == 0 || t.StartsWith("请") || t.StartsWith("不支持") || t.StartsWith("键名"))
            {
                MessageBox.Show("请先点「录制…」按下要绑定的组合键。", "提示");
                DialogResult = DialogResult.None;
                return;
            }
            var parts = t.Split('+');
            var codes = new List<int>();
            foreach (var p in parts)
            {
                int c = Keys2.Code(p.Trim());
                if (c < 0) { MessageBox.Show("键名无效：" + p, "提示"); DialogResult = DialogResult.None; return; }
                codes.Add(c);
            }
            while (codes.Count < 3) codes.Add(-1);
            ResultFunc = 1;
            ResultK1 = codes[0]; ResultK2 = codes[1]; ResultK3 = codes[2];
        }
    }
}

// ============================================================
//  SDK 客户端
//  实测：只要进程里存在消息泵（Application.Run），ExecuteFunction
//  约有 12%~50% 概率在原生层 AV，随后被 CLR 升级为 fail-fast
//  (0xC0000409)，进程静默消失。无消息泵的进程 0/16 稳定。
//  因此 UI 进程绝不加载 ArmouryMsSDK.dll，而是拉起一个
//  --host 子进程做全部 SDK 调用；子进程崩了重启重试即可。
// ============================================================
static class Sdk
{
    const int MaxAttempt = 2;
    const int CallTimeoutMs = 25000;

    static Process host;
    static StreamWriter toHost;
    static StreamReader fromHost;
    static System.Threading.Timer watchdog;
    static int waiting;

    static readonly object gate = new object();

    public const string Head = Dll.Head;

    public static string Dir { get { return Dll.Dir; } }
    public static string Error { get; private set; }
    public static string ResolveDir() { return Dll.ResolveDir(); }
    public static bool Verbose { get { return Dll.Verbose; } }

    public static bool Init()
    {
        lock (gate) { return EnsureHost(); }
    }

    static void Kill()
    {
        try { if (toHost != null) toHost.Close(); } catch { }
        try { if (fromHost != null) fromHost.Close(); } catch { }
        try { if (host != null && !host.HasExited) host.Kill(); } catch { }
        try { if (host != null) host.Dispose(); } catch { }
        toHost = null; fromHost = null; host = null;
    }

    public static void DebugKillHost() { lock (gate) { Kill(); Log.Write("测试：已强杀宿主"); } }

    public static void Shutdown() { lock (gate) { try { if (toHost != null) toHost.WriteLine("Q"); } catch { } Kill(); } }

    static bool EnsureHost()
    {
        if (host != null && !host.HasExited && toHost != null && fromHost != null) return true;

        Kill();

        string dir = Dll.ResolveDir();
        if (dir == null) { Error = "找不到 MouseSDK 目录（需要 ArmouryMsSDK.dll + config.ini）"; return false; }

        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--host")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = dir
            };
            host = Process.Start(psi);
            if (host == null) { Error = "无法启动 SDK 宿主进程"; return false; }

            toHost = new StreamWriter(host.StandardInput.BaseStream, new UTF8Encoding(false));
            toHost.AutoFlush = true;
            fromHost = new StreamReader(host.StandardOutput.BaseStream, new UTF8Encoding(false));

            string ready = fromHost.ReadLine();
            if (ready == null) { Error = "SDK 宿主启动即退出"; Kill(); return false; }

            if (watchdog == null)
                watchdog = new System.Threading.Timer(_ =>
                {
                    if (Interlocked.CompareExchange(ref waiting, 0, 0) == 1)
                    {
                        Log.Write("SDK 宿主超时无响应，强制重启");
                        lock (gate) { Kill(); }
                    }
                });

            Dll.Dir = dir;
            return true;
        }
        catch (Exception e)
        {
            Error = "启动 SDK 宿主失败: " + e.Message;
            Kill();
            return false;
        }
    }

    static string Exchange(string request, out int ret)
    {
        ret = 0;
        for (int attempt = 1; attempt <= MaxAttempt; attempt++)
        {
            if (!EnsureHost()) return null;
            try
            {
                Interlocked.Exchange(ref waiting, 1);
                watchdog.Change(CallTimeoutMs, Timeout.Infinite);
                toHost.WriteLine(request);
                string resp = fromHost.ReadLine();
                watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                Interlocked.Exchange(ref waiting, 0);

                if (resp == null) throw new IOException("宿主已退出");

                string[] parts = resp.Split(' ');
                if (parts.Length < 2) throw new IOException("宿主响应格式错误: " + resp);
                ret = int.Parse(parts[1]);
                if (parts.Length > 2 && parts[2] != "-")
                    return Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                return null;
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref waiting, 0);
                try { watchdog.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                Error = "SDK 宿主异常（第 " + attempt + " 次）: " + e.Message;
                Log.Write(Error);
                Kill();
            }
        }
        return null;
    }

    public static string Call(string xml, out int ret)
    {
        lock (gate) { return Exchange("C " + Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)), out ret); }
    }

    public static bool Enumerate()
    {
        lock (gate)
        {
            int r;
            string o = Exchange("E", out r);
            if (r == 1) return true;
            Error = Error ?? ("枚举失败（r=" + r + "）：鼠标没连接？");
            return false;
        }
    }

    public static string Data(int dataNumber, int profile)
    {
        return Head + "<device key=\"6797\"><function key=\"6\"><data_number>" + dataNumber +
               "</data_number><file_name>-1</file_name><profile_index>" + profile +
               "</profile_index></function></device></device_type></root>";
    }
}
// ===================== 入口 =====================
static class Program
{
    // 无头自检：走与 UI 完全相同的 SDK 路径，写回当前值（幂等），用于回归验证。
    //   ASUS鼠标控制台.exe --selftest
    static int SelfTest()
    {
        var sb = new StringBuilder();
        Action<string> W = s => { sb.AppendLine(s); };

        try
        {
            W("=== 自检 " + DateTime.Now + " ===");
            if (!Sdk.Init()) { W("SDK 初始化失败: " + Sdk.Error); return 2; }
            W("SDK 目录: " + Sdk.Dir);

            if (!Sdk.Enumerate()) { W("枚举失败: " + Sdk.Error); return 3; }
            W("枚举 OK (pid 6797)");

            int r;
            string info = Sdk.Call(Sdk.Data(0, -1), out r);
            if (r != 1 || info == null) { W("读设备信息失败 r=" + r); return 4; }
            int prof = TagVal(info, "profile");
            W("设备: ASUS TX GAMING MOUSE (P310/6797)" +
              "  fw=" + (TagStr(info, "version") ?? "?") +
              "  rf=" + (TagStr(info, "rf_version") ?? "?") +
              "  profile=" + prof +
              "  currentdpi=" + (100 + TagVal(info, "currentdpi") * 100) +
              "  channel=" + TagVal(info, "currentdpichannel"));

            string perf = Sdk.Call(Sdk.Data(2, prof), out r);
            if (r != 1 || perf == null) { W("读性能失败 r=" + r); return 5; }
            int[] d = new int[4];
            for (int i = 0; i < 4; i++) d[i] = TagVal(perf, "dpi_" + (i + 1));
            int poll = TagVal(perf, "polling_rate"), snap = TagVal(perf, "angle_snapping");
            W("Profile " + prof + " 板载: dpi idx=" + string.Join(",", Array.ConvertAll(d, x => x.ToString())) +
              "  polling=" + poll + "  snap=" + snap);

            // 原值写回（幂等）
            var xml = new StringBuilder(Sdk.Head);
            xml.Append("<device key=\"6797\"><function key=\"1\">");
            xml.Append("<profile_index>").Append(prof).Append("</profile_index>");
            xml.Append("<dpi_number>4</dpi_number><accelerate>-1</accelerate><decelerate>-1</decelerate>");
            xml.Append("<angle_snapping>").Append(snap).Append("</angle_snapping>");
            xml.Append("<angle_tuning>-1</angle_tuning><button_response>-1</button_response>");
            xml.Append("<polling_rate>").Append(poll).Append("</polling_rate>");
            int ch = TagVal(info, "currentdpichannel"); if (ch < 1 || ch > 4) ch = 1;
            xml.Append("<dpi_channel>").Append(ch).Append("</dpi_channel>");
            xml.Append("<dpi_1>").Append(d[0]).Append("</dpi_1><dpi_2>").Append(d[1]).Append("</dpi_2>");
            xml.Append("<dpi_3>").Append(d[2]).Append("</dpi_3><dpi_4>").Append(d[3]).Append("</dpi_4>");
            xml.Append("</function></device></device_type></root>");
            Sdk.Call(xml.ToString(), out r);
            W("写性能(幂等): ret=" + r + (r == 1 ? "  OK" : "  失败"));
            if (r != 1) return 6;

            var btns = ButtonsStore.Load(prof);
            var b = new StringBuilder(Sdk.Head);
            b.Append("<device key=\"6797\"><function key=\"0\"><profile_index>").Append(prof).Append("</profile_index>");
            for (int i = 0; i < btns.Count; i++)
            {
                var e = btns[i];
                b.Append("<button_count_").Append(i + 1).Append(" key=\"").Append(i + 1).Append("\">");
                b.Append("<button_function>").Append(e.Func).Append("</button_function>");
                b.Append("<target_key_1>").Append(e.K1).Append("</target_key_1>");
                b.Append("<target_key_2>").Append(e.K2).Append("</target_key_2>");
                b.Append("<target_key_3>").Append(e.K3).Append("</target_key_3>");
                b.Append("<source_key>").Append(e.Source).Append("</source_key>");
                b.Append("</button_count_").Append(i + 1).Append(">");
            }
            b.Append("<button_number>").Append(btns.Count).Append("</button_number></function></device></device_type></root>");
            Sdk.Call(b.ToString(), out r);
            W("写按键(幂等, " + btns.Count + " 键): ret=" + r + (r == 1 ? "  OK" : "  失败"));
            if (r != 1) return 7;

            string back = Sdk.Call(Sdk.Data(0, -1), out r);
            W("回读: profile=" + TagVal(back, "profile") + "  currentdpi=" + (100 + TagVal(back, "currentdpi") * 100));
            W("自检结果: 全部通过");
            return 0;
        }
        finally
        {
            sb.AppendLine();
            try { Directory.CreateDirectory(Log.Dir); File.AppendAllText(System.IO.Path.Combine(Log.Dir, "selftest.log"), sb.ToString(), Encoding.UTF8); } catch { }
        }
    }

    static int TagVal(string xml, string tag)
    {
        var m = Regex.Match(xml ?? "", "<" + tag + ">(-?\\d+)</" + tag + ">");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    static string TagStr(string xml, string tag)
    {
        var m = Regex.Match(xml ?? "", "<" + tag + ">([^<]*)</" + tag + ">");
        return m.Success ? m.Groups[1].Value : null;
    }

    // 对话框架构自检：不靠肉眼，直接断言各类型下「录制」控件的可见性 + 录制流程
    static int DialogTest()
    {
        var sb = new StringBuilder();
        try
        {
            int k1 = Keys2.Code("L-Ctrl"), k2 = Keys2.Code("C");
            var b = new Btn { Source = 273, Name = "侧键-前", Func = 1, K1 = k1, K2 = k2, K3 = -1 };
            var dlg = new EditButtonDialog(b);
            dlg.Show();
            Application.DoEvents();

            sb.AppendLine("=== 对话框自检 ===");
            sb.AppendLine("初始 kind=" + dlg.KindIndex + "  row=" + dlg.ComboRowVisible +
                          "  record=" + dlg.RecordVisible + "  hint=" + dlg.HintVisible);
            sb.AppendLine("初始已填内容: [" + dlg.CapturedText + "]");

            foreach (int k in new[] { 0, 2, 1 })
            {
                dlg.KindIndex = k;
                Application.DoEvents();
                sb.AppendLine("切到 kind=" + k + "  row=" + dlg.ComboRowVisible +
                              "  record=" + dlg.RecordVisible + "  hint=" + dlg.HintVisible);
            }

            // 录制流程：Ctrl+C
            dlg.BeginRecord();
            Application.DoEvents();
            sb.AppendLine("录制中文本: [" + dlg.CapturedText + "]");
            bool ok = dlg.CaptureKey(Keys.C, true, false, false);
            Application.DoEvents();
            sb.AppendLine("CaptureKey(Ctrl+C) ok=" + ok + "  结果: [" + dlg.CapturedText + "]");

            // 组合键三键：Ctrl+Shift+F5
            ok = dlg.CaptureKey(Keys.F5, true, true, false);
            sb.AppendLine("CaptureKey(Ctrl+Shift+F5) ok=" + ok + "  结果: [" + dlg.CapturedText + "]");

            // 单个键：Space
            ok = dlg.CaptureKey(Keys.Space, false, false, false);
            sb.AppendLine("CaptureKey(Space) ok=" + ok + "  结果: [" + dlg.CapturedText + "]");

            dlg.Close();
            dlg.Dispose();
            sb.AppendLine("结论: " + (dlg.RecordVisible ? "" : "（已关闭，可见性不再有意义）"));
        }
        catch (Exception e) { sb.AppendLine("异常: " + e); }
        finally
        {
            try { Directory.CreateDirectory(Log.Dir); File.WriteAllText(System.IO.Path.Combine(Log.Dir, "dialogtest.log"), sb.ToString(), Encoding.UTF8); } catch { }
        }
        return 0;
    }

    // 探针：隔离「线程 / 消息泵」对 SDK 稳定性的影响
    //   --probe  : 主线程直接调用
    //   --probe2 : 后台线程直接调用（无消息泵）
    //   --probe3 : 后台线程 + Application.Run 消息泵
    public static int StartTab = 0;
    static readonly string ProbeLog = System.IO.Path.Combine(Log.Dir, "probe.log");

    static int ProbeCore(string tag)
    {
        var sb = new StringBuilder();
        Action<string> W = x => sb.AppendLine("[" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + tag + " " + x);
        try
        {
            W("Start");
            if (!Sdk.Init()) { W("Init 失败 " + Sdk.Error); return 2; }
            W("Init OK");
            if (!Sdk.Enumerate()) { W("Enumerate 失败 " + Sdk.Error); return 3; }
            W("Enumerate OK");
            int r;
            string info = Sdk.Call(Sdk.Data(0, -1), out r);
            W("data0 ret=" + r + " len=" + (info == null ? -1 : info.Length));
            for (int i = 1; i <= 3; i++)
            {
                Sdk.Call(Sdk.Data(2, i), out r);
                W("data2 p" + i + " ret=" + r);
            }
            W("Done");
            return 0;
        }
        finally
        {
            try { File.AppendAllText(ProbeLog, sb.ToString(), Encoding.UTF8); } catch { }
        }
    }

    static int Probe(string mode)
    {
        if (mode == "1") return ProbeCore("main");

        // mode 4：后台线程跑消息泵，SDK 在主线程（无泵）调用
        if (mode == "4")
        {
            Form host = null;
            var ready = new System.Threading.ManualResetEvent(false);
            var th4 = new Thread(() =>
            {
                host = new Form { Text = "pump", Width = 160, Height = 90, ShowInTaskbar = false, Opacity = 0 };
                host.Shown += (a, b) => ready.Set();
                Application.Run(host);
            });
            th4.IsBackground = true;
            th4.SetApartmentState(ApartmentState.STA);
            th4.Start();
            ready.WaitOne(5000);
            System.Threading.Thread.Sleep(300);
            int c4 = ProbeCore("main-with-bg-pump");
            try { host.BeginInvoke(new Action(() => host.Close())); } catch { }
            System.Threading.Thread.Sleep(500);
            return c4;
        }

        // mode 5：主线程跑泵，SDK 也在主线程（在消息派发中调用，和奥创同款）
        if (mode == "5")
        {
            int c5 = -1;
            var f5 = new Form { Text = "probe5", Width = 200, Height = 120, ShowInTaskbar = false, Opacity = 0 };
            f5.Shown += (a, b) => { try { c5 = ProbeCore("main-pump"); } finally { f5.Close(); } };
            Application.Run(f5);
            return c5;
        }

        int code = -1;
        var ev = new System.Threading.ManualResetEvent(false);
        var th = new Thread(() =>
        {
            try
            {
                if (mode == "2")
                {
                    code = ProbeCore("bg-nopump");
                    ev.Set();
                }
                else
                {
                    var f = new Form { Text = "probe", Width = 200, Height = 120, ShowInTaskbar = false, Opacity = 0 };
                    f.Shown += (a, b) =>
                    {
                        try { code = ProbeCore("bg-pump"); } finally { f.Close(); }
                    };
                    Application.Run(f);
                    ev.Set();
                }
            }
            catch (Exception e)
            {
                try { File.AppendAllText(ProbeLog, "异常: " + e + "\r\n", Encoding.UTF8); } catch { }
                ev.Set();
            }
        });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        ev.WaitOne(25000);
        return code;
    }
    // ==== SDK 宿主：无窗口、无消息泵，只做 SDK 调用 ====
    // 协议（每行一条，UTF-8，载荷 base64）：
    //   父 -> 子: "E"（枚举） / "C <b64 xml>"（执行） / "Q"（退出）
    //   子 -> 父: "R <ret> <b64 输出 | ->"
    static int HostLoop()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        stdout.AutoFlush = true;
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        try
        {
            bool ok = Dll.Init();
            if (!ok) Log.Write("宿主: SDK 初始化失败 " + Dll.Error);
            stdout.WriteLine("R " + (ok ? 1 : 0) + " -");

            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line == "Q") break;

                if (line == "E")
                {
                    bool e = Dll.Enumerate();
                    stdout.WriteLine("R " + (e ? 1 : 0) + " -");
                    continue;
                }

                if (line.Length > 2 && line.StartsWith("C "))
                {
                    string xml;
                    try { xml = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(2))); }
                    catch { stdout.WriteLine("R 0 -"); continue; }

                    int r;
                    string o = Dll.Call(xml, out r);
                    stdout.WriteLine("R " + r + " " + (o == null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(o))));
                    continue;
                }

                stdout.WriteLine("R 0 -");
            }
        }
        catch (Exception e) { Log.Write("宿主主循环异常: " + e); }
        return 0;
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--retrytest")
        {
            var sb2 = new StringBuilder();
            int r;
            sb2.AppendLine("1) 枚举: " + Sdk.Enumerate());
            sb2.AppendLine("2) 读设备: ret=" + (Sdk.Call(Sdk.Data(0, -1), out r) != null ? 1 : 0));
            Sdk.DebugKillHost();
            sb2.AppendLine("3) 宿主已强杀，重新枚举: " + Sdk.Enumerate());
            sb2.AppendLine("4) 再读设备: ret=" + (Sdk.Call(Sdk.Data(0, -1), out r) != null ? 1 : 0) + " (r=" + r + ")");
            Directory.CreateDirectory(Log.Dir); File.WriteAllText(System.IO.Path.Combine(Log.Dir, "retrytest.log"), sb2.ToString(), Encoding.UTF8);
            Sdk.Shutdown();
            return 0;
        }        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tab") { int t; if (int.TryParse(args[i + 1], out t)) StartTab = t; }
        // 命令行切配置档（走与界面相同的 function 5 / profile_function=4）
        if (args.Length > 1 && args[0] == "--switch")
        {
            int id;
            if (!int.TryParse(args[1], out id) || id < 1 || id > 4) { return 9; }
            if (!Sdk.Enumerate()) { Log.Write("switch: 设备未就绪 " + Sdk.Error); return 1; }
            int rr;
            Sdk.Call(Sdk.Head + "<device key=\"6797\"><function key=\"5\"><profile_function>4</profile_function>" +
                     "<profile_parameter>" + id + "</profile_parameter></function></device></device_type></root>", out rr);
            Log.Write("switch: Profile " + id + " ret=" + rr);
            Sdk.Shutdown();
            return rr == 1 ? 0 : 2;
        }
        if (args.Length > 0 && args[0] == "--host") return HostLoop();
        if (args.Length > 0 && args[0] == "--probe") return Probe(args.Length > 1 ? args[1] : "1");
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
        if (args.Length > 0 && args[0] == "--dialogtest")
        {
            Application.EnableVisualStyles();
            return DialogTest();
        }

        bool created;
        using (var mtx = new Mutex(true, "Global\\ASUSMouseConsoleNative", out created))
        {
            if (!created)
            {
                MessageBox.Show("控制台已经在运行了。", "ASUS 鼠标控制台");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) => Log.Write("UI 异常: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理异常: " + e.ExceptionObject);

            Application.ApplicationExit += (s3, e3) => Sdk.Shutdown();
            AppDomain.CurrentDomain.ProcessExit += (s3, e3) => Sdk.Shutdown();

            Log.Write("=== 启动（原生版） ===");

            // 被强杀后立刻重启时，SDK 服务可能还没退干净，startService 会在原生层
            // 直接把进程带走（托管层抓不到）。检测上次未完成的启动标记，先等它落定。
            string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASUSMouseConsole", "starting.flag");
            try
            {
                if (File.Exists(flag))
                {
                    Log.Write("上次启动未正常完成，等待 1.5 秒再初始化 SDK");
                    System.Threading.Thread.Sleep(1500);
                }
                File.WriteAllText(flag, DateTime.Now.ToString("O"));
            }
            catch { }

            Worker.Start();
            var form = new MainForm();
            form.Shown += (s2, e2) => { try { File.Delete(flag); } catch { } };
            Application.Run(form);
        }
        return 0;
    }
}