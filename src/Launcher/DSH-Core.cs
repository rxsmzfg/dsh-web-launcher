// DSH 网页端启动器(GUI 版) —— 双击弹图形界面,自动处理与桌面客户端的互斥,启动网页服务并给出公网链接。
// 编译: csc.exe /target:winexe /codepage:65001 /win32icon:DSH-Web.ico /win32manifest:DSH-Web.manifest
//        /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /out:DSH-Web.exe DSH-Web.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    internal const int DefaultLocalPort = 8080;
    internal const int DefaultPublicPort = 18080;
    internal const string FallbackPublicHost = "";   // 不再写死任何 IP;探测失败就给局域网地址
    /// <summary>语义版本号:行为有明显变化时手动递增,便于"我这是不是最新版"一眼可辨。</summary>
    internal const string Version = "1.4";

    [STAThread]
    private static void Main(string[] args)
    {
        string cmd = "gui";
        int localPort = DefaultLocalPort;
        int publicPort = DefaultPublicPort;
        bool skipPortproxy = false;
        bool localSet = false, publicSet = false;
        string findDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "stop") { cmd = "stop"; }
            else if (a == "probe") { cmd = "probe"; }
            else if (a == "sync") { cmd = "sync"; }
            else if (a == "prepare") { cmd = "prepare"; }
            else if (a == "finddsh") { cmd = "finddsh"; }
            else if (cmd == "finddsh" && !a.StartsWith("-")) { findDir = a; }
            else if (a == "--local-port") { localPort = int.Parse(args[++i]); localSet = true; }
            else if (a == "--public-port") { publicPort = int.Parse(args[++i]); publicSet = true; }
            else if (a == "--no-portproxy") { skipPortproxy = true; }
            else if (a == "--skip-guard") { Sys.SkipGuard = true; }
            else if (a == "--no-watchdog") { Sys.WatchdogEnabled = false; }
        }

        // exe 旁的 DSH-Web.ini 提供默认值;命令行显式传入的值优先
        Sys.LoadConfig();
        int cfgLocal, cfgPublic;
        if (!localSet && int.TryParse(Sys.ConfigValue("localPort", ""), out cfgLocal)) { localPort = cfgLocal; }
        if (!publicSet && int.TryParse(Sys.ConfigValue("publicPort", ""), out cfgPublic)) { publicPort = cfgPublic; }
        if (Sys.ConfigValue("autoSync", "1") == "0") { Sys.AutoSyncEnabled = false; }

        Sys.Init(localPort, publicPort, skipPortproxy);

        if (cmd == "probe") { Sys.Probe(); return; }
        if (cmd == "stop") { Sys.StopService(true); return; }
        if (cmd == "sync") { RunSyncCli(); return; }
        if (cmd == "prepare") { RunPrepareCli(); return; }
        if (cmd == "finddsh") { RunFindDshCli(findDir); return; }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ChatForm());
    }

    /// <summary>命令行定位模式(排障/验证用):DSH-Web.exe finddsh [目录] —— 结果写 finddsh.log。</summary>
    private static void RunFindDshCli(string dir)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("exeDir=" + Sys.ExeDir);
        sb.AppendLine("iniPath=" + Sys.IniPath());
        sb.AppendLine("autoFound=" + (Sys.FindDshCmd() ?? "(未找到)"));
        if (!string.IsNullOrEmpty(dir))
        {
            string r = Sys.TryResolveDshFrom(dir);
            sb.AppendLine("fromDir[" + dir + "]=" + (r ?? "(未找到)"));
            if (r != null) { sb.AppendLine("saved=" + Sys.SaveDshCli(r)); }
        }
        sb.AppendLine("searchRoots=");
        foreach (string root in Sys.DshSearchRoots()) { sb.AppendLine("  " + root); }
        try { File.WriteAllText(Path.Combine(Sys.WorkDir, "finddsh.log"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
    }

    /// <summary>
    /// 命令行环境准备模式:DSH-Web.exe prepare —— 自举 profile → 部署回环插件 → 同步客户端插件。
    /// 不启动服务、不结束任何进程,适合排障或"先修好环境再启动"。
    /// </summary>
    private static void RunPrepareCli()
    {
        StringBuilder sb = new StringBuilder();
        List<string> log = new List<string>();
        int installed = 0;
        try { installed = PluginSync.Provision(log, null); }
        catch (Exception ex) { sb.AppendLine("准备过程出错: " + ex.Message); }
        foreach (string line in log) { sb.AppendLine(line); }

        string pluginDir = Path.Combine(Sys.ProfilesDir, "web", "plugins", "web-loopback-view");
        string entry = Path.Combine(pluginDir, "index.mjs");
        sb.AppendLine("回环插件目录: " + pluginDir);
        sb.AppendLine("  入口文件存在: " + File.Exists(entry));
        sb.AppendLine("  包清单存在:   " + File.Exists(Path.Combine(pluginDir, "package.json")));
        sb.AppendLine("  旧布局残留:   " + File.Exists(Path.Combine(Sys.ProfilesDir, "web", "web-loopback-view.mjs")));
        if (PluginSync.LastLoopbackError.Length > 0)
        {
            sb.AppendLine("  部署失败原因: " + PluginSync.LastLoopbackError);
        }
        sb.AppendLine("本次补装的包数: " + installed);

        try { File.WriteAllText(Path.Combine(Sys.WorkDir, "prepare.log"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
    }

    /// <summary>命令行同步模式(排障/脚本用):DSH-Web.exe sync —— 结果写到 web-remote\sync.log。</summary>
    private static void RunSyncCli()
    {
        StringBuilder sb = new StringBuilder();
        PluginSync.Report r = PluginSync.Check();
        if (!r.Ok) { sb.AppendLine("对比失败: " + r.Error); }
        else if (r.InSync) { sb.AppendLine("已一致:客户端 " + r.DesktopCount + " 个 bundle,网页端 " + r.WebCount + " 个。"); }
        else
        {
            sb.AppendLine("缺失 " + r.MissingBundles.Count + " 个:" + string.Join("、", r.MissingBundles.ToArray()));
            foreach (string line in PluginSync.Install(r, null)) { sb.AppendLine(line); }
            PluginSync.Report after = PluginSync.Check();
            sb.AppendLine(after.InSync ? "同步完成,已与客户端一致。" : ("仍有缺失:" + string.Join("、", after.MissingBundles.ToArray())));
        }
        try { File.WriteAllText(Path.Combine(Sys.WorkDir, "sync.log"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        Console.WriteLine(sb.ToString());
    }
}

internal static class Sys
{
    internal static int LocalPort = Program.DefaultLocalPort;
    internal static int PublicPort = Program.DefaultPublicPort;
    private static bool skipPortproxy;
    internal static string WorkDir;
    internal static string LogOut;
    internal static string LogErr;
    internal static string PublicHost = "";   // 运行时探测;探测不到就为空(只提供局域网地址)
    /// <summary>构建标识:v1.0 · 2026-10-15 13:05(exe 文件自身的时间戳,自动取,不用手维护)。</summary>
    internal static string BuildStamp
    {
        get
        {
            string when = "?";
            try
            {
                string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
                when = File.GetLastWriteTime(self).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            return "v" + Program.Version + " · " + when;
        }
    }
    internal static int ServicePid;
    /// <summary>排障用:跳过"客户端互斥"检查(--skip-guard),只在明确知道后果时使用。</summary>
    internal static bool SkipGuard;
    /// <summary>守望开关:网页端运行期间检测到客户端启动,就自动退出(--no-watchdog 关闭)。</summary>
    internal static bool WatchdogEnabled = true;
    /// <summary>启动时是否自动把客户端的插件同步过来(DSH-Web.ini 里 autoSync=0 关闭)。</summary>
    internal static bool AutoSyncEnabled = true;
    internal static string WatchLog;
    internal static string GateLog;
    /// <summary>公网闸门:自己监听公网端口,实现"同一时间只允许一个浏览器访问"。</summary>
    internal static Gate Gate;
    /// <summary>profiles 目录(desktop / web 两套配置集都在这下面)。</summary>
    internal static string ProfilesDir;
    /// <summary>DSH 家目录:尊重 DSH_HOME 环境变量,否则用 ~/.dsh。</summary>
    internal static string DshHome;
    /// <summary>exe 所在目录(工作目录与可选配置文件的默认位置)。</summary>
    internal static string ExeDir;
    /// <summary>同级可选配置 DSH-Web.ini(localPort / publicPort / publicHost / dshCli / autoSync)。</summary>
    internal static readonly Dictionary<string, string> Config =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static Process child;
    private static IntPtr jobHandle = IntPtr.Zero;

    /// <summary>
    /// 读取配置(exe 旁的 DSH-Web.ini,其次 %LOCALAPPDATA%\DSH-Web\DSH-Web.ini)。
    /// 这个文件由程序自己维护(例如用户在界面上选过 DSH 目录后就写在这里),
    /// 正常使用不需要手写。每行 key=value,# 或 ; 开头为注释。
    /// </summary>
    internal static void LoadConfig()
    {
        try
        {
            ExeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(ExeDir)) { ExeDir = AppDomain.CurrentDomain.BaseDirectory; }
            ReadIniTo(Path.Combine(ExeDir, "DSH-Web.ini"));
            ReadIniTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-Web", "DSH-Web.ini"));
        }
        catch { }
    }

    private static void ReadIniTo(string ini)
    {
        try
        {
            if (!File.Exists(ini)) { return; }
            foreach (string raw in File.ReadAllLines(ini, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) { continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }
                Config[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        catch { }
    }

    /// <summary>本程序记事的配置文件路径:exe 旁能写就用它,否则用 %LOCALAPPDATA%\DSH-Web。</summary>
    internal static string IniPath()
    {
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-Web", "DSH-Web.ini");
        try
        {
            string beside = Path.Combine(ExeDir == null ? "." : ExeDir, "DSH-Web.ini");
            if (File.Exists(beside)) { return beside; }
            File.AppendAllText(beside, "");      // 试写,失败说明目录只读
            return beside;
        }
        catch { return fallback; }
    }

    /// <summary>把用户选定的 dsh.cmd 路径记下来(自动写配置,用户不用手写)。</summary>
    internal static bool SaveDshCli(string dshCmdPath)
    {
        try
        {
            string ini = IniPath();
            string dir = Path.GetDirectoryName(ini);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            List<string> lines = File.Exists(ini)
                ? new List<string>(File.ReadAllLines(ini, Encoding.UTF8))
                : new List<string>();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("dshCli", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "dshCli=" + dshCmdPath;
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                if (lines.Count == 0) { lines.Add("# DSH 网页端启动器 —— 本文件由程序自动维护,一般不需要手改"); }
                lines.Add("dshCli=" + dshCmdPath);
            }
            File.WriteAllLines(ini, lines.ToArray(), new UTF8Encoding(false));
            Config["dshCli"] = dshCmdPath;
            return true;
        }
        catch { return false; }
    }

    /// <summary>用户在界面上选了某个目录后,尽力在其中定位 dsh.cmd(找不到返回 null)。</summary>
    internal static string TryResolveDshFrom(string dir)
    {
        if (string.IsNullOrEmpty(dir)) { return null; }
        string[] rels = new string[]
        {
            @"resources\runtime\cli\bin\dsh.cmd",
            @"runtime\cli\bin\dsh.cmd",
            @"cli\bin\dsh.cmd",
            @"bin\dsh.cmd",
            "dsh.cmd"
        };
        foreach (string r in rels)
        {
            try { string c = Path.Combine(dir, r); if (File.Exists(c)) { return c; } }
            catch { }
        }
        return FindFileShallow(dir, "dsh.cmd", 5);
    }

    internal static string ConfigValue(string key, string fallback)
    {
        string v;
        return Config.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
    }

    internal static void Init(int localPort, int publicPort, bool skip)
    {
        LocalPort = localPort;
        PublicPort = publicPort;
        skipPortproxy = skip;

        string env = Environment.GetEnvironmentVariable("DSH_HOME");
        DshHome = string.IsNullOrEmpty(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : env;
        ProfilesDir = Path.Combine(DshHome, "profiles");

        // 工作目录(exe 旁优先;不可写则退到 %LOCALAPPDATA%\DSH-Web)
        WorkDir = Path.Combine(ExeDir == null ? Path.GetTempPath() : ExeDir, "DSH-Web-data");
        if (!TryPrepareDir(WorkDir))
        {
            WorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-Web");
            TryPrepareDir(WorkDir);
        }
        LogOut = Path.Combine(WorkDir, "web.out.log");
        LogErr = Path.Combine(WorkDir, "web.err.log");
        WatchLog = Path.Combine(WorkDir, "watchdog.log");
        GateLog = Path.Combine(WorkDir, "gate.log");
    }

    private static bool TryPrepareDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------------- 进程与互斥

    /// <summary>
    /// 是否为"执行 shell 命令"的子进程(dsh-subprocess-local/runner.js)。它的命令行里包含被执行的
    /// 命令原文,凡是拿命令行做特征匹配的判定都必须排除它,否则会被命令文本骗到。
    /// </summary>
    private static bool IsShellRunner(string cl)
    {
        return cl.IndexOf("dsh-subprocess-local", StringComparison.OrdinalIgnoreCase) >= 0
            && cl.IndexOf("runner.js", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>桌面客户端主进程:命令行含 dsh-desktop-host\lib\index.js(且不是 cli.js、不是 shell runner)。</summary>
    internal static List<ProcInfo> FindClientProcesses()
    {
        List<ProcInfo> list = new List<ProcInfo>();
        foreach (ProcInfo p in QueryHarnessProcesses())
        {
            string cl = p.CommandLine;
            if (IsShellRunner(cl)) { continue; }
            bool index = cl.IndexOf("dsh-desktop-host", StringComparison.OrdinalIgnoreCase) >= 0
                      && cl.IndexOf("index.js", StringComparison.OrdinalIgnoreCase) >= 0;
            bool cli = cl.IndexOf("cli.js", StringComparison.OrdinalIgnoreCase) >= 0;
            if (index && !cli) { list.Add(p); }
        }
        return list;
    }

    /// <summary>网页端服务进程:命令行含 dsh-desktop-host\lib\cli.js 且带 web --host 参数。</summary>
    internal static List<ProcInfo> FindWebServiceProcesses()
    {
        List<ProcInfo> list = new List<ProcInfo>();
        foreach (ProcInfo p in QueryHarnessProcesses())
        {
            string cl = p.CommandLine;
            if (IsShellRunner(cl)) { continue; }
            if (cl.IndexOf("cli.js", StringComparison.OrdinalIgnoreCase) >= 0 && Regex.IsMatch(cl, @"\sweb\s+--host")) { list.Add(p); }
        }
        return list;
    }

    /// <summary>沿父进程链向上找到最顶层的 DeepSeek Harness 进程,杀掉它才能整只客户端都退出。</summary>
    internal static int TopmostHarnessAncestor(int pid)
    {
        int top = pid;
        for (int i = 0; i < 5; i++)
        {
            int parent = ParentOf(top);
            if (parent <= 0) { break; }
            if (ProcessNameOf(parent) != "DeepSeek Harness") { break; }
            top = parent;
        }
        return top;
    }

    internal static int ParentOf(int pid)
    {
        try
        {
            ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid);
            foreach (ManagementObject mo in s.Get())
            {
                object v = mo["ParentProcessId"];
                mo.Dispose();
                return v == null ? 0 : Convert.ToInt32(v);
            }
        }
        catch { }
        return 0;
    }

    private static List<ProcInfo> QueryHarnessProcesses()
    {
        List<ProcInfo> list = new List<ProcInfo>();
        try
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='DeepSeek Harness.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                ProcInfo info = new ProcInfo();
                info.Pid = Convert.ToInt32(mo["ProcessId"]);
                object cl = mo["CommandLine"];
                info.CommandLine = cl == null ? "" : cl.ToString();
                list.Add(info);
                mo.Dispose();
            }
        }
        catch { }
        return list;
    }

    internal static int FindPortOwner(int port)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                foreach (string raw in outp.Split('\n'))
                {
                    string[] parts = Regex.Split(raw.Trim(), @"\s+");
                    if (parts.Length < 5) { continue; }
                    if (parts[0] != "TCP" || parts[3] != "LISTENING") { continue; }
                    int idx = parts[1].LastIndexOf(':');
                    if (idx < 0 || parts[1].Substring(idx + 1) != port.ToString()) { continue; }
                    int pid;
                    if (int.TryParse(parts[4], out pid)) { return pid; }
                }
            }
        }
        catch { }
        return 0;
    }

    internal static string ProcessNameOf(int pid)
    {
        try { using (Process p = Process.GetProcessById(pid)) { return p.ProcessName; } }
        catch { return "?"; }
    }

    internal static bool LooksLikeOurService(string name)
    {
        string n = (name == null ? "" : name.ToLowerInvariant());
        return n.Contains("deepseek") || n.Contains("node") || n.Contains("cmd") || n.Contains("conhost");
    }

    internal static void KillTree(int pid)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(20000); }
        }
        catch { }
    }

    internal static bool WaitProcessGone(int pid, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            bool alive = false;
            try { using (Process p = Process.GetProcessById(pid)) { alive = !p.HasExited; } }
            catch { alive = false; }
            if (!alive) { return true; }
            Thread.Sleep(300);
            waited += 300;
        }
        return false;
    }

    internal static bool WaitPortFree(int port, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (FindPortOwner(port) == 0) { return true; }
            Thread.Sleep(300);
            waited += 300;
        }
        return FindPortOwner(port) == 0;
    }

    // -------------------------------------------------------- 服务生命周期

    internal static string StartServiceAndWaitToken()
    {
        try
        {
            File.WriteAllText(LogOut, "[启动] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
            File.WriteAllText(LogErr, string.Empty);
        }
        catch { }

        string dsh = FindDshCmd();
        if (dsh == null) { return null; }
        PublicHost = DetectPublicIp();
        string lanHost = DetectLanIp();

        StringBuilder sb = new StringBuilder();
        sb.Append("web --host 127.0.0.1 --port ").Append(LocalPort).Append(" --no-open");
        if (PublicHost != null) { sb.Append(" --trusted-host ").Append(PublicHost).Append(":").Append(PublicPort); }
        if (lanHost != null) { sb.Append(" --trusted-host ").Append(lanHost).Append(":").Append(PublicPort); }

        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + dsh + "\" " + sb.ToString() + "\"");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.WorkingDirectory = WorkDir;
        psi.EnvironmentVariables.Remove("DSH_PROFILE");
        psi.EnvironmentVariables.Remove("DSH_PROFILE_DIR");
        psi.EnvironmentVariables.Remove("DSH_SESSION_ID");
        psi.EnvironmentVariables.Remove("DSH_WEB_URL");

        child = new Process();
        child.StartInfo = psi;
        child.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
        {
            if (e.Data == null) { return; }
            AppendLog(LogOut, e.Data);
            Match m = Regex.Match(e.Data, @"token=([A-Za-z0-9_\-]+)");
            if (m.Success) { tokenValue = m.Groups[1].Value; }
        };
        child.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
        {
            if (e.Data != null) { AppendLog(LogErr, e.Data); }
        };
        child.Start();
        ServicePid = child.Id;
        CreateJobAndAssign(child);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        int waited = 0;
        while (waited < 45000)
        {
            if (tokenValue != null) { return tokenValue; }
            if (child.HasExited) { return null; }
            Thread.Sleep(250);
            waited += 250;
        }
        return tokenValue;
    }

    private static string tokenValue;

    internal static void WatchChild(Action<string> onLine, Action onExit)
    {
        if (child == null) { return; }
        child.WaitForExit();
        onLine("[服务已退出] 退出码 " + child.ExitCode);
        foreach (string l in Tail(LogErr, 6)) { onLine("  " + l); }
        if (onExit != null) { onExit(); }
    }

    internal static void StopService(bool verbose)
    {
        try
        {
            if (child != null && !child.HasExited) { KillTree(child.Id); }
        }
        catch { }
        try
        {
            int owner = FindPortOwner(LocalPort);
            if (owner != 0 && LooksLikeOurService(ProcessNameOf(owner))) { KillTree(owner); }
        }
        catch { }
        try
        {
            if (jobHandle != IntPtr.Zero) { CloseHandle(jobHandle); jobHandle = IntPtr.Zero; }
        }
        catch { }
        try { if (Gate != null) { Gate.Stop(); Gate = null; } } catch { }
        if (verbose) { Console.WriteLine("已停止网页端服务。"); }
    }

    /// <summary>
    /// 结束其它已在运行的启动器实例:它们的服务会随各自的 Job 对象一起退出,
    /// 从而腾出 8080 与公网端口,保证"同一时间只有一个启动器"。
    /// </summary>
    internal static int KillOtherLaunchers()
    {
        int self = Process.GetCurrentProcess().Id;
        int killed = 0;
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if ((n == "DSH-Web" || n == "DSH网页端") && p.Id != self)
                    {
                        KillTree(p.Id);
                        killed++;
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return killed;
    }

    // -------------------------------------------------------- 公网闸门

    /// <summary>
    /// 起公网闸门:先清掉 netsh 转发规则(否则端口被内核占着),再由本进程监听公网端口,
    /// 这样才有机会读 cookie 做"单浏览器占用"判定。之后 keep-alive / WebSocket 全部按字节盲转发。
    /// </summary>
    internal static void StartGate(string token)
    {
        try { RemovePortproxyFor(PublicPort); } catch { }
        Gate g = new Gate(PublicPort, LocalPort);
        g.LogPath = GateLog;
        g.Token = token;
        g.Start();
        Gate = g;
    }

    /// <summary>删掉指定公网端口的 netsh 转发规则(闸门要自己监听该端口)。</summary>
    internal static bool RemovePortproxyFor(int port)
    {
        if (PortproxyState() == null) { return true; }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe",
                "interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=" + port);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(15000); return p.ExitCode == 0; }
        }
        catch { return false; }
    }

    // -------------------------------------------------------- 转发与网络

    internal static bool EnsurePortproxy()
    {
        if (skipPortproxy) { return true; }
        if (PortproxyState() != null) { return true; }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe",
                "interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=" + PublicPort +
                " connectaddress=127.0.0.1 connectport=" + LocalPort);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(15000); return p.ExitCode == 0; }
        }
        catch { return false; }
    }

    internal static string PortproxyState()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe", "interface portproxy show v4tov4");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                foreach (string raw in outp.Split('\n'))
                {
                    string[] parts = Regex.Split(raw.Trim(), @"\s+");
                    if (parts.Length >= 4 && parts[1] == PublicPort.ToString() && parts[3] == LocalPort.ToString())
                    { return "0.0.0.0:" + PublicPort + " → 127.0.0.1:" + LocalPort; }
                }
            }
        }
        catch { }
        return null;
    }

    internal static string SelfCheck(string publicUrl)
    {
        if (skipPortproxy) { return "已跳过(--no-portproxy)"; }
        try
        {
            // 自检直连本机服务端口(不经过公网闸门),避免自检把"浏览器占用"名额抢掉
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + LocalPort + "/?token=" + tokenValue);
            req.Host = PublicHost + ":" + PublicPort;
            req.AllowAutoRedirect = false;
            req.Timeout = 10000;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                { return "通过 (HTTP " + (int)resp.StatusCode + ")"; }
            }
            catch (WebException we)
            {
                if (we.Response != null)
                { return "通过 (HTTP " + (int)((HttpWebResponse)we.Response).StatusCode + ")"; }
                return "失败:" + we.Message;
            }
        }
        catch (Exception ex) { return "失败:" + ex.Message; }
    }

    /// <summary>
    /// 探测本机出口公网 IP:多服务依次尝试;也可用 DSH-Web.ini 的 publicHost 直接指定。
    /// 全部失败返回 null(调用方应退化为只给局域网地址,而不是编造一个 IP)。
    /// </summary>
    internal static string DetectPublicIp()
    {
        string configured = ConfigValue("publicHost", "");
        if (configured.Length > 0) { return configured.Trim(); }

        string[] services = new string[]
        {
            "https://ifconfig.me/ip",
            "https://api.ipify.org",
            "https://ipinfo.io/ip",
            "https://myip.ipip.net"
        };
        foreach (string url in services)
        {
            string s = HttpGetString(url, 6000);
            if (s == null) { continue; }
            Match m = Regex.Match(s, @"\b(\d{1,3}(?:\.\d{1,3}){3})\b");
            if (m.Success) { return m.Groups[1].Value; }
        }
        return null;
    }

    /// <summary>
    /// 确保公网端口在 Windows 防火墙里被放行(幂等;不加规则的话默认拦截入站)。
    /// 需要管理员权限;失败不影响本地使用,返回值告诉界面是否成功。
    /// </summary>
    internal static bool EnsureFirewallRule()
    {
        try
        {
            string name = "DSH Web " + PublicPort;
            string show = RunTool("netsh.exe", "advfirewall firewall show rule name=\"" + name + "\"");
            if (show != null && show.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            string add = RunTool("netsh.exe",
                "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow protocol=TCP localport=" + PublicPort);
            return add != null;
        }
        catch { return false; }
    }

    private static string RunTool(string exe, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                return o;
            }
        }
        catch { return null; }
    }

    internal static string DetectLanIp()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) { continue; }
                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { continue; }
                    string a = ip.Address.ToString();
                    if (a.StartsWith("127.") || a.StartsWith("169.254.")) { continue; }
                    return a;
                }
            }
        }
        catch { }
        return null;
    }

    private static string HttpGetString(string url, int timeoutMs)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs; req.Proxy = null;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            { return sr.ReadToEnd(); }
        }
        catch { return null; }
    }

    /// <summary>
    /// 定位 dsh CLI:优先 DSH-Web.ini 的 dshCli,其次 DSH_CLI 环境变量,
    /// 再按 %LOCALAPPDATA%\Programs\*\resources\runtime\cli\bin\dsh.cmd 搜索(默认安装位置)。
    /// </summary>
    internal static string FindDshCmd()
    {
        string configured = ConfigValue("dshCli", "");
        if (configured.Length > 0 && File.Exists(configured)) { return configured; }

        string envCli = Environment.GetEnvironmentVariable("DSH_CLI");
        if (!string.IsNullOrEmpty(envCli) && File.Exists(envCli)) { return envCli; }

        const string rel = @"resources\runtime\cli\bin\dsh.cmd";

        // 1) 各安装根下按常见目录名找(覆盖按用户安装与按机器安装、改名目录)
        foreach (string root in DshSearchRoots())
        {
            if (!Directory.Exists(root)) { continue; }
            string direct = Path.Combine(root, "DeepSeek Harness", rel);
            if (File.Exists(direct)) { return direct; }
            foreach (string dir in SafeDirs(root))
            {
                string c = Path.Combine(dir, rel);
                if (File.Exists(c)) { return c; }
                // 目录名里带 deepseek/harness 但层级略深的情况
                string name = Path.GetFileName(dir);
                if (name.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0
                 || name.IndexOf("harness", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string deep = FindFileShallow(dir, "dsh.cmd", 4);
                    if (deep != null) { return deep; }
                }
            }
        }

        // 2) 兜底:在按用户安装目录里浅层递归找 dsh.cmd(最多 4 层,避免全盘扫描)
        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return FindFileShallow(localPrograms, "dsh.cmd", 4);
    }

    /// <summary>dsh.cmd 的搜索根(按用户安装在前,按机器安装在后的顺序)。</summary>
    internal static List<string> DshSearchRoots()
    {
        List<string> roots = new List<string>();
        AddRoot(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return roots;
    }

    private static void AddRoot(List<string> roots, string root)
    {
        if (!string.IsNullOrEmpty(root) && !roots.Contains(root)) { roots.Add(root); }
    }

    private static string[] SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return new string[0]; }
    }

    /// <summary>在指定目录下浅层递归查找文件名(深度受限,找不到返回 null)。</summary>
    private static string FindFileShallow(string dir, string fileName, int depth)
    {
        if (depth < 0 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { return null; }
        try
        {
            foreach (string f in Directory.GetFiles(dir, fileName))
            { return f; }
        }
        catch { }
        foreach (string sub in SafeDirs(dir))
        {
            string hit = FindFileShallow(sub, fileName, depth - 1);
            if (hit != null) { return hit; }
        }
        return null;
    }

    /// <summary>给"找不到 DSH"的提示用:列出找过哪些位置(以及程序自己记事的配置文件在哪)。</summary>
    internal static string DescribeDshSearch()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("  · 程序记下的位置:").Append(IniPath()).Append(Environment.NewLine);
        sb.Append("  · 环境变量 DSH_CLI").Append(Environment.NewLine);
        foreach (string root in DshSearchRoots())
        {
            sb.Append("  · ").Append(Path.Combine(root, @"DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd")).Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    // -------------------------------------------------------- 杂项

    internal static void AppendLog(string path, string line)
    {
        try
        {
            lock (typeof(Sys))
            {
                using (StreamWriter sw = new StreamWriter(path, true, new UTF8Encoding(false))) { sw.WriteLine(line); }
            }
        }
        catch { }
    }

    internal static string[] Tail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) { return new string[0]; }
            string[] all = File.ReadAllLines(path);
            int start = all.Length - lines; if (start < 0) { start = 0; }
            string[] res = new string[all.Length - start];
            Array.Copy(all, start, res, 0, res.Length);
            return res;
        }
        catch { return new string[0]; }
    }

    /// <summary>诊断模式:把检测结果写到 probe.txt,供自动化验证使用(无界面)。</summary>
    internal static void Probe()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("build=" + BuildStamp);
        sb.AppendLine("dshHome=" + DshHome);
        sb.AppendLine("workDir=" + WorkDir);
        List<ProcInfo> clients = FindClientProcesses();
        sb.AppendLine("clientDetected=" + (clients.Count > 0));
        foreach (ProcInfo p in clients) { sb.AppendLine("clientPid=" + p.Pid); }
        List<ProcInfo> webs = FindWebServiceProcesses();
        sb.AppendLine("webServiceDetected=" + (webs.Count > 0));
        foreach (ProcInfo p in webs) { sb.AppendLine("webServicePid=" + p.Pid); }
        int owner = FindPortOwner(LocalPort);
        sb.AppendLine("port" + LocalPort + "Owner=" + owner);
        sb.AppendLine("portproxy=" + (PortproxyState() == null ? "missing" : PortproxyState()));
        sb.AppendLine("publicIp=" + DetectPublicIp());
        sb.AppendLine("lanIp=" + (DetectLanIp() == null ? "" : DetectLanIp()));
        sb.AppendLine("dshCmd=" + (FindDshCmd() == null ? "NOT FOUND" : FindDshCmd()));
        try { File.WriteAllText(Path.Combine(WorkDir, "probe.txt"), sb.ToString()); } catch { }
        Console.WriteLine(sb.ToString());
    }

    // -------------------------------------------------------- Job 对象

    private static void CreateJobAndAssign(Process p)
    {
        try
        {
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) { return; }
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int len = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)len);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            AssignProcessToJobObject(jobHandle, p.Handle);
        }
        catch { }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}


// ================================================================ 进程信息

/// <summary>一条进程记录:进程号 + 完整命令行(判定客户端/网页端身份用)。</summary>
internal sealed class ProcInfo
{
    internal int Pid;
    internal string CommandLine;
}
