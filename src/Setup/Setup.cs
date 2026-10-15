// DSH 网页端启动器 —— 安装程序(向导式)。
// 步骤:① 选择安装位置 ② 用户许可协议 ③ 安装选项(桌面快捷方式/开始菜单) ④ 完成(可立即启动)
// 静默模式(供自动化/批量部署):Setup.exe /S /D=<目录> /desktopicon=0|1 /startmenu=0|1 /launch=0|1
// 卸载:卸载器就是安装程序自身(/uninstall),只删除自己的安装目录与快捷方式,绝不动 DSH 自身的数据。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class SetupProgram
{
    internal const string AppName = "DSH 网页端启动器";
    internal const string VersionText = "v1.3";
    internal const string PayloadExe = "DSH-Web.exe";
    internal const string PayloadResource = "payload.DSH-Web.exe";
    internal const string EulaResource = "EULA.md";

    [STAThread]
    private static void Main(string[] args)
    {
        bool silent = false, uninstall = false, desktopIcon = true, startMenu = true, launch = false;
        int startStep = 1;
        string dir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string low = a.ToLowerInvariant();
            if (low == "/s" || low == "--silent") { silent = true; }
            else if (low == "/uninstall" || low == "--uninstall") { uninstall = true; }
            else if (low.StartsWith("/d=")) { dir = a.Substring(3).Trim('"'); }
            else if (low.StartsWith("/desktopicon=")) { desktopIcon = a.Substring(13) != "0"; }
            else if (low.StartsWith("/startmenu=")) { startMenu = a.Substring(11) != "0"; }
            else if (low.StartsWith("/launch=")) { launch = a.Substring(8) == "1"; }
            else if (low.StartsWith("/step=")) { int.TryParse(a.Substring(6), out startStep); }
        }

        // 从安装目录里运行的卸载器(Uninstall.exe)也走卸载分支
        if (!uninstall && Path.GetFileName(Application.ExecutablePath).ToLowerInvariant().StartsWith("uninstall"))
        { uninstall = true; }

        if (uninstall)
        {
            string target = dir ?? Path.GetDirectoryName(Application.ExecutablePath);
            Installer.Uninstall(target, silent);
            return;
        }

        if (silent)
        {
            int code = Installer.Install(dir ?? Installer.DefaultDir(), desktopIcon, startMenu, null);
            if (launch && code == 0) { Installer.LaunchInstalled(dir ?? Installer.DefaultDir()); }
            Environment.Exit(code);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WizardForm(startStep));
    }
}

// ==================================================================== 安装动作

internal static class Installer
{
    internal static string DefaultDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "DSH 网页端");
    }

    internal static byte[] ReadResource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) { throw new Exception("安装包内缺少资源:" + name); }
            byte[] buf = new byte[s.Length];
            int off = 0;
            while (off < buf.Length)
            {
                int n = s.Read(buf, off, buf.Length - off);
                if (n <= 0) { break; }
                off += n;
            }
            return buf;
        }
    }

    internal static string ReadTextResource(string name)
    {
        try { return Encoding.UTF8.GetString(ReadResource(name)); }
        catch { return "(安装包内未找到协议文本)"; }
    }

    /// <summary>执行安装;progress 为空则静默。返回 0 成功。</summary>
    internal static int Install(string dir, bool desktopIcon, bool startMenu, Action<string> progress)
    {
        try
        {
            if (progress != null) { progress("正在创建安装目录…"); }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                LastError = "无法创建安装目录:" + dir + Environment.NewLine + ex.Message;
                if (progress != null) { progress("安装失败。"); }
                return 1;
            }

            if (progress != null) { progress("正在写入程序文件…"); }
            string target = Path.Combine(dir, SetupProgram.PayloadExe);

            // 目标程序若正在运行,会锁住文件导致写入失败 —— 先结束它(仅限该目录里的那一个)
            KillRunningFrom(target, progress);
            try
            {
                File.WriteAllBytes(target, ReadResource(SetupProgram.PayloadResource));
            }
            catch (Exception ex)
            {
                // 再试一次:某些情况下进程退出后句柄释放稍有延迟
                Thread.Sleep(1200);
                try { File.WriteAllBytes(target, ReadResource(SetupProgram.PayloadResource)); }
                catch
                {
                    LastError = "无法写入程序文件:" + target + Environment.NewLine + ex.Message +
                                Environment.NewLine + Environment.NewLine +
                                "常见原因与处理:" + Environment.NewLine +
                                "  · 该目录里的程序正在运行 —— 关掉它再试(程序已尝试自动结束,可能被系统拒绝);" + Environment.NewLine +
                                "  · 目标在 C:\\Program Files 等受保护位置 —— 请右键安装包,选择\"以管理员身份运行\";" + Environment.NewLine +
                                "  · 目录被网盘/杀毒软件锁定 —— 换一个目录试试。";
                    if (progress != null) { progress("安装失败。"); }
                    return 1;
                }
            }

            if (progress != null) { progress("正在写入卸载程序…"); }
            try { File.Copy(Application.ExecutablePath, Path.Combine(dir, "Uninstall.exe"), true); }
            catch (Exception ex) { LastError = "卸载程序写入失败(不影响使用):" + ex.Message; }

            if (progress != null) { progress("正在创建快捷方式…"); }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (desktopIcon) { CreateShortcut(Path.Combine(desktop, SetupProgram.AppName + ".lnk"), target, dir); }
            if (startMenu) { CreateShortcut(Path.Combine(programs, SetupProgram.AppName + ".lnk"), target, dir); }

            if (progress != null) { progress("正在记录安装信息…"); }
            StringBuilder info = new StringBuilder();
            info.AppendLine("dir=" + dir);
            info.AppendLine("desktopIcon=" + (desktopIcon ? "1" : "0"));
            info.AppendLine("startMenu=" + (startMenu ? "1" : "0"));
            info.AppendLine("installedAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try { File.WriteAllText(Path.Combine(dir, "install.txt"), info.ToString(), new UTF8Encoding(false)); } catch { }

            if (progress != null) { progress("安装完成。"); }
            return 0;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            if (progress != null) { progress("安装失败。"); }
            Console.Error.WriteLine("install failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>最近一次失败的原因(供界面原样展示)。</summary>
    internal static string LastError = "";

    /// <summary>结束"正从指定路径运行"的进程(用于覆盖安装前解锁文件)。</summary>
    private static void KillRunningFrom(string exePath, Action<string> progress)
    {
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    if (path != null && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (progress != null) { progress("该目录下的程序正在运行,先结束它…"); }
                        try { p.Kill(); p.WaitForExit(6000); } catch { }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
    }

    /// <summary>卸载:删除快捷方式与安装目录;不触碰 DSH 自身目录。自身正在运行的 exe 交由系统延后删除。</summary>
    internal static int Uninstall(string dir, bool silent)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            foreach (string lnk in new string[]
            {
                Path.Combine(desktop, SetupProgram.AppName + ".lnk"),
                Path.Combine(programs, SetupProgram.AppName + ".lnk")
            })
            {
                try { if (File.Exists(lnk)) { File.Delete(lnk); } } catch { }
            }

            if (!Directory.Exists(dir)) { return 0; }

            foreach (string f in Directory.GetFiles(dir))
            {
                try { File.Delete(f); } catch { }
            }
            string cmd = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"";
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            try { Process.Start(psi); } catch { }

            if (!silent)
            {
                MessageBox.Show("已卸载 " + SetupProgram.AppName + "。\n\n剩余文件会在几秒内自动清理。",
                    SetupProgram.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent) { MessageBox.Show("卸载时出错:" + ex.Message, SetupProgram.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return 1;
        }
    }

    internal static void LaunchInstalled(string dir)
    {
        try
        {
            string exe = Path.Combine(dir, SetupProgram.PayloadExe);
            if (File.Exists(exe))
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
        }
        catch { }
    }

    /// <summary>用 WScript.Shell 创建 .lnk(无需第三方库)。</summary>
    internal static bool CreateShortcut(string lnkPath, string targetPath, string workDir)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) { return false; }
            object shell = Activator.CreateInstance(shellType);
            object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetPath });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { SetupProgram.AppName });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { targetPath + ",0" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            return true;
        }
        catch { return false; }
    }
}

// ==================================================================== 视觉规范

internal static class Skin
{
    internal static readonly Color Page = Color.FromArgb(243, 244, 246);
    internal static readonly Color Card = Color.White;
    internal static readonly Color Border = Color.FromArgb(226, 229, 234);
    internal static readonly Color TextMain = Color.FromArgb(24, 28, 35);
    internal static readonly Color TextSub = Color.FromArgb(110, 117, 129);
    internal static readonly Color Accent = Color.FromArgb(37, 99, 235);
    internal static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    internal static readonly Color GhostHover = Color.FromArgb(243, 244, 246);
    internal static readonly Font Body = new Font("Microsoft YaHei UI", 9.5F);
    internal static readonly Font BodyBold = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
    internal static readonly Font H1 = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold);
    internal static readonly Font H2 = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>圆角按钮:主按钮=实心强调色,次按钮=白底描边。</summary>
internal sealed class RoundButton : Button
{
    private readonly bool primary;
    private bool hover;

    internal RoundButton(string text, bool primaryButton)
    {
        primary = primaryButton;
        Text = text;
        Font = Skin.Body;
        Height = 38;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Skin.Card;
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color bg;
        if (!Enabled) { bg = Color.FromArgb(240, 241, 244); }
        else if (primary) { bg = hover ? Skin.AccentHover : Skin.Accent; }
        else { bg = hover ? Skin.GhostHover : Skin.Card; }

        using (GraphicsPath p = Skin.Rounded(r, 9))
        using (SolidBrush b = new SolidBrush(bg))
        {
            e.Graphics.FillPath(b, p);
            if (!primary)
            {
                using (Pen pen = new Pen(Skin.Border)) { e.Graphics.DrawPath(pen, p); }
            }
        }
        Color fg = !Enabled ? Color.FromArgb(160, 166, 176) : (primary ? Color.White : Skin.TextMain);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>带圆角与描边的卡片面板。</summary>
internal sealed class CardPanel : Panel
{
    internal CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Skin.Page;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath p = Skin.Rounded(r, 12))
        using (SolidBrush b = new SolidBrush(Skin.Card))
        using (Pen pen = new Pen(Skin.Border))
        {
            e.Graphics.FillPath(b, p);
            e.Graphics.DrawPath(pen, p);
        }
    }
}

// ==================================================================== 向导界面

internal sealed class WizardForm : Form
{
    private const int PageWidth = 760;
    private const int PageHeight = 560;
    private const int OuterX = 36;          // 内容左右留白
    private const int CardInner = 30;       // 卡片内留白

    private readonly Panel header;
    private readonly Panel progressTrack;
    private readonly Panel progressFill;
    private readonly CardPanel card;
    private readonly Panel foot;
    private readonly Label headSub;
    private readonly Label status;
    private readonly RoundButton backBtn;
    private readonly RoundButton nextBtn;
    private readonly RoundButton cancelBtn;

    private int step;

    // 各步控件
    private TextBox dirBox;
    private CheckBox agreeBox;
    private CheckBox desktopBox;
    private CheckBox startMenuBox;
    private CheckBox launchBox;
    private ProgressBar bar;
    private Label donePath;

    private bool installed;

    internal WizardForm() : this(1) { }

    /// <summary>startStep 用于界面预览(排障):直接跳到第 N 步渲染。</summary>
    internal WizardForm(int startStep)
    {
        Text = SetupProgram.AppName + " 安装程序 " + SetupProgram.VersionText;
        Font = Skin.Body;
        ClientSize = new Size(PageWidth, PageHeight);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Skin.Page;

        // ---------- 顶部标题带
        header = new Panel();
        header.SetBounds(0, 0, PageWidth, 92);
        header.BackColor = Color.FromArgb(17, 24, 39);
        header.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 左侧一条强调色装饰
            using (SolidBrush b = new SolidBrush(Skin.Accent))
            { e.Graphics.FillRectangle(b, 0, 0, 5, header.Height); }
        };
        Controls.Add(header);

        Label headTitle = new Label();
        headTitle.Text = SetupProgram.AppName;
        headTitle.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        headTitle.ForeColor = Color.White;
        headTitle.AutoSize = true;
        headTitle.Location = new Point(OuterX, 20);
        header.Controls.Add(headTitle);

        headSub = new Label();
        headSub.Text = SetupProgram.VersionText;
        headSub.Font = new Font("Microsoft YaHei UI", 9F);
        headSub.ForeColor = Color.FromArgb(196, 202, 212);
        headSub.AutoSize = true;
        headSub.Location = new Point(OuterX + 2, 56);
        header.Controls.Add(headSub);

        // ---------- 步骤进度条(细线)
        progressTrack = new Panel();
        progressTrack.SetBounds(OuterX, 112, PageWidth - OuterX * 2, 4);
        progressTrack.BackColor = Color.FromArgb(228, 231, 236);
        Controls.Add(progressTrack);

        progressFill = new Panel();
        progressFill.SetBounds(0, 0, 0, 4);
        progressFill.BackColor = Skin.Accent;
        progressTrack.Controls.Add(progressFill);

        // ---------- 内容卡片
        card = new CardPanel();
        card.SetBounds(OuterX, 132, PageWidth - OuterX * 2, PageHeight - 132 - 96);
        Controls.Add(card);

        // ---------- 底部操作条
        foot = new Panel();
        foot.SetBounds(0, PageHeight - 84, PageWidth, 84);
        foot.BackColor = Skin.Card;
        foot.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen pen = new Pen(Skin.Border)) { e.Graphics.DrawLine(pen, 0, 0, foot.Width, 0); }
        };
        Controls.Add(foot);

        status = new Label();
        status.Text = "";
        status.Font = new Font("Microsoft YaHei UI", 9F);
        status.ForeColor = Skin.TextSub;
        status.AutoSize = true;
        status.Location = new Point(OuterX, 32);
        foot.Controls.Add(status);

        cancelBtn = new RoundButton("取消", false);
        cancelBtn.Size = new Size(88, 38);
        cancelBtn.Location = new Point(PageWidth - OuterX - 88, 23);
        cancelBtn.Click += delegate { Close(); };
        foot.Controls.Add(cancelBtn);

        nextBtn = new RoundButton("下一步", true);
        nextBtn.Size = new Size(124, 38);
        nextBtn.Location = new Point(cancelBtn.Left - 10 - 124, 23);
        nextBtn.Click += delegate { OnNext(); };
        foot.Controls.Add(nextBtn);

        backBtn = new RoundButton("上一步", false);
        backBtn.Size = new Size(104, 38);
        backBtn.Location = new Point(nextBtn.Left - 10 - 104, 23);
        backBtn.Click += delegate { GoStep(step - 1); };
        foot.Controls.Add(backBtn);

        AcceptButton = nextBtn;
        CancelButton = cancelBtn;

        GoStep(1);
        // 预览模式:跳到指定步骤(第 2 步自动勾选同意,便于截图)
        if (startStep > 1)
        {
            GoStep(Math.Min(4, startStep));
            if (step == 2 && agreeBox != null) { agreeBox.Checked = true; }
        }
    }

    // ------------------------------------------------------------ 小组件

    private Label Title(string text, int y)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Skin.H1;
        l.ForeColor = Skin.TextMain;
        l.AutoSize = true;
        l.Location = new Point(CardInner, y);
        card.Controls.Add(l);
        return l;
    }

    private Label Desc(string text, int y, int height)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Skin.Body;
        l.ForeColor = Skin.TextSub;
        l.AutoSize = false;
        l.SetBounds(CardInner, y, card.Width - CardInner * 2, height);
        card.Controls.Add(l);
        return l;
    }

    private int ContentWidth { get { return card.Width - CardInner * 2; } }

    // ------------------------------------------------------------ 各步骤内容

    private void BuildStep1()
    {
        Title("选择安装位置", 26);
        Desc("程序文件将安装到下面的文件夹。默认位置只对当前用户可见,通常不需要管理员权限。", 60, 40);

        int rowY = 112;
        dirBox = new TextBox();
        dirBox.Font = Skin.Body;
        dirBox.Text = Installer.DefaultDir();
        dirBox.SetBounds(CardInner, rowY, ContentWidth - 108, 30);
        card.Controls.Add(dirBox);

        RoundButton browse = new RoundButton("浏览…", false);
        browse.Size = new Size(96, 30);
        browse.Location = new Point(CardInner + ContentWidth - 96, rowY);
        browse.Click += delegate
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.Description = "选择安装位置";
            fbd.SelectedPath = dirBox.Text;
            fbd.ShowNewFolderButton = true;
            if (fbd.ShowDialog(this) == DialogResult.OK) { dirBox.Text = fbd.SelectedPath; }
        };
        card.Controls.Add(browse);

        Desc("说明:安装包里已包含启动器本身。运行时它会调用本机已安装的 DSH 来提供网页服务;" +
             "若装在非默认位置,启动时会弹窗让你选择 DSH 的目录。", rowY + 52, 56);
    }

    private void BuildStep2()
    {
        Title("用户许可协议", 26);
        Desc("请完整阅读以下协议。勾选同意后才能继续安装。", 60, 24);

        TextBox eula = new TextBox();
        eula.Multiline = true;
        eula.ReadOnly = true;
        eula.ScrollBars = ScrollBars.Vertical;
        eula.BackColor = Color.FromArgb(250, 251, 252);
        eula.BorderStyle = BorderStyle.FixedSingle;
        eula.Font = new Font("Microsoft YaHei UI", 9F);
        eula.Text = Installer.ReadTextResource(SetupProgram.EulaResource);
        eula.SetBounds(CardInner, 96, ContentWidth, card.Height - 96 - CardInner - 44);
        eula.Select(0, 0);
        card.Controls.Add(eula);

        agreeBox = new CheckBox();
        agreeBox.Text = "我已阅读并同意上述许可协议的全部条款";
        agreeBox.Font = Skin.BodyBold;
        agreeBox.ForeColor = Skin.TextMain;
        agreeBox.AutoSize = true;
        agreeBox.Location = new Point(CardInner, card.Height - CardInner - 26);
        agreeBox.CheckedChanged += delegate { UpdateButtons(); };
        card.Controls.Add(agreeBox);
    }

    private void BuildStep3()
    {
        Title("安装选项", 26);
        Desc("选择是否创建快捷方式,然后点击右下角的\"安装\"。", 60, 24);

        desktopBox = new CheckBox();
        desktopBox.Text = "在桌面创建快捷方式";
        desktopBox.Font = Skin.Body;
        desktopBox.AutoSize = true;
        desktopBox.Checked = true;
        desktopBox.Location = new Point(CardInner + 2, 110);
        card.Controls.Add(desktopBox);

        startMenuBox = new CheckBox();
        startMenuBox.Text = "在开始菜单创建快捷方式";
        startMenuBox.Font = Skin.Body;
        startMenuBox.AutoSize = true;
        startMenuBox.Checked = true;
        startMenuBox.Location = new Point(CardInner + 2, 146);
        card.Controls.Add(startMenuBox);

        bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.MarqueeAnimationSpeed = 0;
        bar.SetBounds(CardInner, 196, ContentWidth, 16);
        card.Controls.Add(bar);
    }

    private void BuildStep4()
    {
        Title("安装完成", 26);
        Desc("程序已安装到:", 60, 22);

        donePath = new Label();
        donePath.Text = dirBox == null ? "" : dirBox.Text;
        donePath.Font = new Font("Microsoft YaHei UI", 9.5F);
        donePath.ForeColor = Skin.Accent;
        donePath.AutoSize = false;
        donePath.SetBounds(CardInner, 88, ContentWidth, 40);
        card.Controls.Add(donePath);

        launchBox = new CheckBox();
        launchBox.Text = "立即启动 " + SetupProgram.AppName;
        launchBox.Font = Skin.BodyBold;
        launchBox.AutoSize = true;
        launchBox.Checked = true;
        launchBox.Location = new Point(CardInner + 2, 142);
        card.Controls.Add(launchBox);

        Label tip = new Label();
        tip.Text = "使用提醒:启动后窗口里会给出带令牌的访问链接。该链接等同于服务器控制权," +
                   "请勿转发给不信任的人,并在云安全组中限制来源 IP。";
        tip.Font = new Font("Microsoft YaHei UI", 9F);
        tip.ForeColor = Skin.TextSub;
        tip.AutoSize = false;
        tip.SetBounds(CardInner, 184, ContentWidth, 56);
        card.Controls.Add(tip);
    }

    // ------------------------------------------------------------ 步骤切换

    private void GoStep(int s)
    {
        step = s;
        card.Controls.Clear();

        if (step == 1) { headSub.Text = SetupProgram.VersionText + "    第 1 步,共 3 步 · 选择安装位置"; BuildStep1(); }
        else if (step == 2) { headSub.Text = SetupProgram.VersionText + "    第 2 步,共 3 步 · 用户许可协议"; BuildStep2(); }
        else if (step == 3) { headSub.Text = SetupProgram.VersionText + "    第 3 步,共 3 步 · 安装选项"; BuildStep3(); }
        else { headSub.Text = SetupProgram.VersionText + "    安装完成"; BuildStep4(); }

        // 进度:3 步安装 + 完成
        int total = progressTrack.Width;
        int filled = step >= 4 ? total : (int)(total * (step - 1) / 3.0);
        progressFill.Width = Math.Max(0, Math.Min(total, filled));

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        backBtn.Enabled = (step > 1 && step <= 3);
        backBtn.Visible = step <= 3;
        if (step <= 2) { nextBtn.Text = "下一步"; }
        else if (step == 3) { nextBtn.Text = installed ? "下一步" : "安装"; }
        else { nextBtn.Text = "完成"; }
        nextBtn.Enabled = !(step == 2 && (agreeBox == null || !agreeBox.Checked));
        cancelBtn.Visible = step <= 3;
    }

    private void OnNext()
    {
        if (step == 1)
        {
            string d = dirBox.Text.Trim();
            if (d.Length == 0)
            {
                MessageBox.Show("请选择安装位置。", SetupProgram.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Directory.CreateDirectory(d); }
            catch (Exception ex)
            {
                MessageBox.Show("无法创建该文件夹:\n" + ex.Message, SetupProgram.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            GoStep(2);
            return;
        }
        if (step == 2) { GoStep(3); return; }
        if (step == 3)
        {
            if (!installed) { RunInstall(); } else { GoStep(4); }
            return;
        }
        if (launchBox != null && launchBox.Checked) { Installer.LaunchInstalled(dirBox.Text.Trim()); }
        Close();
    }

    private void RunInstall()
    {
        string dir = dirBox.Text.Trim();
        bool desktop = desktopBox.Checked;
        bool menu = startMenuBox.Checked;

        nextBtn.Enabled = false;
        backBtn.Enabled = false;
        cancelBtn.Enabled = false;
        bar.MarqueeAnimationSpeed = 30;

        ThreadPool.QueueUserWorkItem(delegate
        {
            int code = Installer.Install(dir, desktop, menu, delegate(string m)
            {
                try { BeginInvoke(new Action(delegate { status.Text = m; })); } catch { }
            });
            try
            {
                BeginInvoke(new Action(delegate
                {
                    bar.MarqueeAnimationSpeed = 0;
                    if (code == 0)
                    {
                        installed = true;
                        status.Text = "安装完成。";
                        GoStep(4);
                    }
                    else
                    {
                        status.Text = "安装失败。";
                        string detail = string.IsNullOrEmpty(Installer.LastError)
                            ? "未知原因。"
                            : Installer.LastError;
                        MessageBox.Show("安装失败,原因如下:" + Environment.NewLine + Environment.NewLine + detail,
                            SetupProgram.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        cancelBtn.Enabled = true;
                        backBtn.Enabled = true;
                        nextBtn.Enabled = true;
                    }
                }));
            }
            catch { }
        });
    }
}
