using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// ============================================================
//  ASUS TX GAMING MOUSE 控制台 —— 单文件安装程序
//  载荷以自定义容器嵌入（Deflate 压缩），无需任何第三方运行时
//    Setup.exe                       图形安装
//    Setup.exe /S [/DIR=路径] [/NORUN]  静默安装
//    Setup.exe /uninstall            卸载（由安装目录里的 uninstall.exe 调用）
// ============================================================
static class Program
{
    public const string AppName = "ASUS 鼠标控制台";
    public const string AppExe = "ASUS鼠标控制台.exe";
    public const string AppVersion = "1.0.0";
    public const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ASUSMouseConsole";
    public const string PayloadRes = "payload.bin";
    const string Magic = "MCX1";

    [STAThread]
    static int Main(string[] args)
    {
        string dir = null;
        bool silent = false, noRun = false, uninstall = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "/S" || a == "-s" || a == "/silent") silent = true;
            else if (a == "/uninstall" || a == "-u") uninstall = true;
            else if (a == "/NORUN") noRun = true;
            else if (a.StartsWith("/DIR=")) dir = a.Substring(5).Trim('"', ' ', '\t');
            else if (a == "/DIR" && i + 1 < args.Length) dir = args[++i].Trim('"', ' ', '\t');
        }

        if (dir != null) dir = dir.Trim(); if (dir != null && dir.Length == 0) dir = null; if (dir == null) dir = DefaultDir();

        if (uninstall) return Uninstall.Do(dir, silent);

        if (silent)
        {
            string err;
            bool ok = Installer.Run(dir, true, !noRun, null, out err);
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "mcx-setup.log");
                File.WriteAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                    "dir=" + dir + "\r\nok=" + ok + "\r\n" + (err ?? ""), Encoding.UTF8);
            }
            catch { }
            return ok ? 0 : 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(dir));
        return 0;
    }

    public static string DefaultDir()
    {
        // 已装过则沿用原路径
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                if (k != null)
                {
                    string old = k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(old)) return old;
                }
        }
        catch { }

        return Path.Combine(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "ASUSMouseConsole");
    }

    public static string DesktopLnk
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
    }

    public static string StartMenuLnk
    {
        get
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "ASUS 鼠标控制台"), AppName + ".lnk");
        }
    }

    public static string UninstLnk
    {
        get
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "ASUS 鼠标控制台"), "卸载 " + AppName + ".lnk");
        }
    }
}

// ===================== 载荷解包 =====================
// 容器格式: "MCX1" + 重复{ int32 名称字节数, 名称(UTF8), int32 原始长度, int32 压缩长度, Deflate 数据 } + int32 0
static class Payload
{
    static Stream Open()
    {
        Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Program.PayloadRes);
        if (s == null) throw new InvalidOperationException("安装包内缺少载荷数据");
        return s;
    }

    static int ReadInt(BinaryReader r)
    {
        byte[] b = r.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException("载荷已损坏");
        return BitConverter.ToInt32(b, 0);
    }

    public static List<string> Names()
    {
        var list = new List<string>();
        using (Stream s = Open())
        using (var r = new BinaryReader(s))
        {
            byte[] head = r.ReadBytes(4);
            if (head.Length < 4 || Encoding.ASCII.GetString(head) != "MCX1") throw new InvalidOperationException("载荷格式不对");
            while (true)
            {
                int nl = ReadInt(r);
                if (nl <= 0) break;
                list.Add(Encoding.UTF8.GetString(r.ReadBytes(nl)));
                ReadInt(r); ReadInt(r);
                int cl = ReadInt(r);
                Skip(r, cl);
            }
        }
        return list;
    }

    static void Skip(BinaryReader r, int n)
    {
        while (n > 0)
        {
            byte[] buf = r.ReadBytes(Math.Min(n, 81920));
            if (buf.Length == 0) throw new EndOfStreamException("载荷已损坏");
            n -= buf.Length;
        }
    }

    public static int Extract(string targetDir, Action<int, string> progress)
    {
        Directory.CreateDirectory(targetDir);
        int count = 0;
        using (Stream s = Open())
        using (var r = new BinaryReader(s))
        {
            byte[] head = r.ReadBytes(4);
            if (head.Length < 4 || Encoding.ASCII.GetString(head) != "MCX1") throw new InvalidOperationException("载荷格式不对");

            while (true)
            {
                int nl = ReadInt(r);
                if (nl <= 0) break;

                string name = Encoding.UTF8.GetString(r.ReadBytes(nl));
                int rawLen = ReadInt(r);
                int compLen = ReadInt(r);
                byte[] comp = r.ReadBytes(compLen);

                string full = Path.Combine(targetDir, name.Replace('/', '\\'));
                string parent = Path.GetDirectoryName(full);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

                using (var ms = new MemoryStream(comp))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[81920];
                    int total = 0, n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        total += n;
                    }
                    if (total != rawLen) throw new InvalidDataException("解压长度不符: " + name);
                }

                count++;
                if (progress != null) progress(count, name);
            }
        }
        return count;
    }
}

// ===================== 安装 =====================
static class Installer
{
    public static bool Run(string dir, bool desktop, bool launch, Action<int, string> progress, out string error)
    {
        error = null;
        try
        {
            Payload.Extract(dir, progress);

            string exe = FindAppExe(dir);
            if (exe == null) throw new IOException("解包后找不到主程序");

            if (desktop) Shortcut.Create(Program.DesktopLnk, exe, dir, Program.AppName);
            else Shortcut.Delete(Program.DesktopLnk);

            Shortcut.Create(Program.StartMenuLnk, exe, dir, Program.AppName);

            // 卸载器：把安装包自身复制一份
            string uninst = Path.Combine(dir, "uninstall.exe");
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(self, uninst, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(uninst)) File.Delete(uninst);
                    File.Copy(self, uninst, true);
                }
            }
            catch { /* 卸载器复制失败不影响使用 */ }

            if (File.Exists(uninst)) Shortcut.Create(Program.UninstLnk, uninst, dir, "卸载 " + Program.AppName,
                                                     "/uninstall");

            WriteUninstallEntry(dir, exe, uninst);

            if (launch)
            {
                try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true }); }
                catch { }
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有权限写入该目录，请换个位置，或用管理员身份运行。";
            return false;
        }
        catch (Exception e)
        {
            Exception inner = e;
            var sb = new StringBuilder();
            while (inner != null)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
            error = sb.ToString();
            return false;
        }
    }

    // 载荷里的主程序可能叫中文名也可能叫 ASCII 名，两个都认
    static string FindAppExe(string dir)
    {
        string[] names = { "ASUSMouseControl.exe", "ASUS" + "\u9F20" + "\u6807" + "\u63A7" + "\u5236" + "\u53F0.exe" };
        foreach (string n in names)
        {
            string p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        try
        {
            foreach (string p in Directory.GetFiles(dir, "*.exe"))
                if (!string.Equals(Path.GetFileName(p), "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                    return p;
        }
        catch { }
        return null;
    }

    static void WriteUninstallEntry(string dir, string exe, string uninst)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Program.RegPath))
        {
            if (k == null) return;
            k.SetValue("DisplayName", Program.AppName, RegistryValueKind.String);
            k.SetValue("DisplayVersion", Program.AppVersion, RegistryValueKind.String);
            k.SetValue("Publisher", "本地构建", RegistryValueKind.String);
            k.SetValue("DisplayIcon", exe + ",0", RegistryValueKind.String);
            k.SetValue("InstallLocation", dir, RegistryValueKind.String);
            k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall", RegistryValueKind.String);
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /S", RegistryValueKind.String);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            long size = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    size += new FileInfo(f).Length;
            }
            catch { }
            k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        }
    }
}

// ===================== 卸载 =====================
static class Uninstall
{
    public static int Do(string dir, bool silent)
    {
        if (!silent)
        {
            DialogResult r = MessageBox.Show("确定要卸载 " + Program.AppName + " 吗？\n\n安装位置：" + dir,
                "卸载 " + Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return 1;
        }

        try { Shortcut.Delete(Program.DesktopLnk); } catch { }
        try { Shortcut.Delete(Program.StartMenuLnk); } catch { }
        try { Shortcut.Delete(Program.UninstLnk); } catch { }
        try { Shortcut.DeleteFolder(Path.GetDirectoryName(Program.StartMenuLnk)); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(Program.RegPath, false); } catch { }

        // 自身在待删目录里，交给 cmd 延迟删除
        try
        {
            string args = "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + dir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }

        if (!silent)
            MessageBox.Show("已卸载。文件会在几秒内清理完。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

// ===================== 快捷方式（COM 反射，免引用） =====================
static class Shortcut
{
    public static void Create(string lnkPath, string target, string workDir, string desc, string args = null)
    {
        string parent = Path.GetDirectoryName(lnkPath);
        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);

        Type wsType = Type.GetTypeFromProgID("WScript.Shell");
        if (wsType == null) throw new InvalidOperationException("系统缺少 WScript.Shell，无法创建快捷方式");

        object shell = Activator.CreateInstance(wsType);
        object sc = wsType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
        Type scType = sc.GetType();

        scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
        scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
        scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
        if (!string.IsNullOrEmpty(args))
            scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
        scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    public static void Delete(string lnkPath)
    {
        if (File.Exists(lnkPath)) File.Delete(lnkPath);
    }

    public static void DeleteFolder(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) &&
                Directory.GetFileSystemEntries(path).Length == 0)
                Directory.Delete(path);
        }
        catch { }
    }
}

// ===================== 安装界面 =====================
class SetupForm : Form
{
    readonly TextBox txtDir;
    readonly CheckBox chkDesktop;
    readonly CheckBox chkRun;
    readonly Button btnInstall;
    readonly Button btnCancel;
    readonly Button btnBrowse;
    readonly ProgressBar bar;
    readonly Label lblStatus;
    readonly Label lblTitle;

    public SetupForm(string dir)
    {
        Text = "安装 " + Program.AppName + " " + Program.AppVersion;
        ClientSize = new Size(560, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        lblTitle = new Label
        {
            Text = Program.AppName,
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        Controls.Add(lblTitle);

        var sub = new Label
        {
            Text = "华硕 TX GAMING MOUSE（P310）控制台 · 不依赖奥创中心 · 版本 " + Program.AppVersion,
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(27, 62)
        };
        Controls.Add(sub);

        var line = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Width = 512, Location = new Point(24, 92) };
        Controls.Add(line);

        Controls.Add(new Label { Text = "安装位置", AutoSize = true, Location = new Point(24, 112) });

        txtDir = new TextBox { Text = dir, Location = new Point(24, 134), Width = 410 };
        Controls.Add(txtDir);

        btnBrowse = new Button { Text = "浏览…", Location = new Point(444, 132), Width = 92, Height = 26 };
        btnBrowse.Click += delegate
        {
            using (var f = new FolderBrowserDialog())
            {
                f.Description = "选择安装位置";
                if (Directory.Exists(txtDir.Text)) f.SelectedPath = txtDir.Text;
                if (f.ShowDialog(this) == DialogResult.OK)
                    txtDir.Text = Path.Combine(f.SelectedPath, "ASUSMouseConsole");
            }
        };
        Controls.Add(btnBrowse);

        chkDesktop = new CheckBox { Text = "创建桌面快捷方式", Checked = true, AutoSize = true, Location = new Point(24, 172) };
        Controls.Add(chkDesktop);

        chkRun = new CheckBox { Text = "安装完成后立即运行", Checked = true, AutoSize = true, Location = new Point(24, 196) };
        Controls.Add(chkRun);

        lblStatus = new Label { Text = "", ForeColor = Color.DimGray, AutoSize = false, Width = 512, Height = 20, Location = new Point(24, 224) };
        Controls.Add(lblStatus);

        bar = new ProgressBar { Location = new Point(24, 246), Width = 512, Height = 12, Visible = false };
        Controls.Add(bar);

        btnInstall = new Button { Text = "安装", Location = new Point(354, 272), Width = 90, Height = 32 };
        btnInstall.Click += delegate { DoInstall(); };
        Controls.Add(btnInstall);

        btnCancel = new Button { Text = "取消", Location = new Point(450, 272), Width = 90, Height = 32 };
        btnCancel.Click += delegate { Close(); };
        Controls.Add(btnCancel);
    }

    void DoInstall()
    {
        string dir = txtDir.Text.Trim();
        if (dir.Length == 0) { MessageBox.Show(this, "请填写安装位置。", Program.AppName); return; }

        try { dir = Path.GetFullPath(dir); }
        catch { MessageBox.Show(this, "安装位置不是合法路径。", Program.AppName); return; }

        btnInstall.Enabled = false;
        btnCancel.Enabled = false;
        txtDir.Enabled = false;
        btnBrowse.Enabled = false;
        chkDesktop.Enabled = false;
        chkRun.Enabled = false;
        bar.Visible = true;
        lblStatus.Text = "正在释放文件…";

        int total = 0;
        try { total = Payload.Names().Count; } catch { }

        string err;
        bool ok = Installer.Run(dir,
            chkDesktop.Checked,
            chkRun.Checked,
            delegate(int n, string name)
            {
                if (total <= 0) return;
                int pct = (int)(n * 100L / total);
                if (pct > 100) pct = 100;
                if (bar.Value != pct) bar.Value = pct;
                if ((n & 31) == 0) { lblStatus.Text = "正在释放文件… " + n + " / " + total; Application.DoEvents(); }
            },
            out err);

        if (!ok)
        {
            bar.Visible = false;
            lblStatus.ForeColor = Color.Firebrick;
            lblStatus.Text = "安装失败：" + err;
            MessageBox.Show(this, "安装失败：\n\n" + err, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            btnInstall.Enabled = true;
            btnCancel.Enabled = true;
            txtDir.Enabled = true;
            btnBrowse.Enabled = true;
            chkDesktop.Enabled = true;
            chkRun.Enabled = true;
            return;
        }

        bar.Value = 100;
        lblStatus.ForeColor = Color.SeaGreen;
        lblStatus.Text = "安装完成。";
        MessageBox.Show(this, "安装完成，可以开始使用了。\n\n安装位置：" + dir,
            Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}