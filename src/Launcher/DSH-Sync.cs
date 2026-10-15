// DSH 网页端 —— 客户端插件同步。
// profile 之间是隔离的:桌面客户端装了什么插件,网页端(web profile)不会自动有。
// 这里做"以客户端为准"的对比与补装,让浏览器端和客户端保持同一套插件环境。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class PluginSync
{
    /// <summary>对比结果。</summary>
    internal sealed class Report
    {
        internal string Error;
        internal readonly List<string> MissingBundles = new List<string>();
        internal Dictionary<string, string> DesktopDeps = new Dictionary<string, string>();
        internal int DesktopCount;
        internal int WebCount;

        internal bool Ok { get { return Error == null; } }
        internal bool InSync { get { return Ok && MissingBundles.Count == 0; } }
    }

    private static string DesktopPkg { get { return Path.Combine(Sys.ProfilesDir, "desktop", "package.json"); } }
    private static string WebPkg { get { return Path.Combine(Sys.ProfilesDir, "web", "package.json"); } }

    // ---------------------------------------------------------- 自举与部署

    /// <summary>这台机器上有没有桌面客户端的配置集(没有就没有插件可同步)。</summary>
    internal static bool HasClientProfile() { return File.Exists(DesktopPkg); }

    /// <summary>
    /// 确保 web profile 存在。DSH 启动器会在首次运行任何 `dsh --profile web ...` 命令时,
    /// 从随发行版交付的模板把 profile 物化出来;这里跑一次无害的 --dump-config(不起服务)。
    /// </summary>
    internal static bool EnsureWebProfile(Action<string> progress)
    {
        if (File.Exists(WebPkg)) { return true; }
        if (progress != null) { progress("首次运行:正在初始化网页端配置集…"); }
        string dsh = Sys.FindDshCmd();
        if (dsh == null) { return false; }
        RunDsh(dsh, "--profile web --dump-config");
        return File.Exists(WebPkg);
    }

    private const string LoopbackPluginName = "web-loopback-view.mjs";

    /// <summary>
    /// 回环视图插件源码。客户端用页面 hostname 判断 isLoopback,公网 IP 访问时为 false,
    /// 设置界面于是退回内存作用域(模型页报 unavailable)。官方通道是页面全局
    /// __DSH_TRANSPORT__:只声明 ownsHost,不提供 fetch/openStream,Remote 载体行为不变。
    /// </summary>
    private const string LoopbackPluginSource =
        "/**\n" +
        " * web-loopback-view —— 让公网访问的浏览器页面以回环视图呈现(设置/模型页可用)。\n" +
        " * 由 DSH 网页端启动器自动部署,内容随启动器版本更新。\n" +
        " */\n" +
        "export const name = 'web-loopback-view';\n" +
        "export const inject = ['webServer'];\n" +
        "const SCRIPT = '<script>/* DSH web: loopback-owned view */' +\n" +
        "  'try{window.__DSH_TRANSPORT__=Object.assign({ownsHost:true},window.__DSH_TRANSPORT__||{});}catch(e){}' +\n" +
        "  '</script>';\n" +
        "export function apply(ctx) {\n" +
        "  ctx.webServer.tapIndex(function (html) { return html.replace('</head>', SCRIPT + '</head>'); });\n" +
        "}\n";

    /// <summary>
    /// 把回环视图插件写进 web profile 目录,并保证补丁层里有一条指向它的 insert
    /// (用运行时算出的绝对 file:// URL,所以换机器/换用户名都成立)。
    /// </summary>
    internal static bool EnsureLoopbackPlugin(Action<string> progress)
    {
        try
        {
            string webDir = Path.Combine(Sys.ProfilesDir, "web");
            if (!Directory.Exists(webDir)) { return false; }

            string target = Path.Combine(webDir, LoopbackPluginName);
            string url = new Uri(target).AbsoluteUri;          // file:///C:/...

            string current = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : null;
            if (current != LoopbackPluginSource)
            {
                if (progress != null) { progress("正在部署网页端回环视图插件…"); }
                File.WriteAllText(target, LoopbackPluginSource, new UTF8Encoding(false));
            }

            string patch = Path.Combine(webDir, "cordis.patch.yml");
            string text = File.Exists(patch) ? File.ReadAllText(patch, Encoding.UTF8) : "";
            string entry = "- insert:\n    - id: web-loopback-view\n      name: '" + url + "'\n";
            if (text.IndexOf("id: web-loopback-view", StringComparison.Ordinal) < 0)
            {
                if (progress != null) { progress("正在启用回环视图(否则设置/模型页不可用)…"); }

                // 补丁层必须整体是一个顶层数组。新自举出来的 profile 里通常已经有一个空数组
                // "[]",这时必须把它替换掉,而不是在它后面追加(否则 YAML 出现两个顶层值,加载直接失败)。
                string withoutComments = Regex.Replace(text, "(?m)^\\s*#.*$", "").Trim();
                if (withoutComments.Length == 0 || withoutComments == "[]")
                {
                    int idx = text.LastIndexOf("[]", StringComparison.Ordinal);
                    text = (idx >= 0 ? text.Substring(0, idx) : "") + entry;
                }
                else
                {
                    if (text.Length > 0 && !text.EndsWith("\n")) { text += "\n"; }
                    text += entry;
                }
                File.WriteAllText(patch, text, new UTF8Encoding(false));
            }
            else
            {
                // 已有条目:把 file:// 路径改成当前机器算出来的
                string replacement = "${1}" + url.Replace("$", "$$") + "${2}";
                string fixedText = Regex.Replace(text, "(?m)^(\\s*name:\\s*')file:///[^']*('\\s*)$", replacement);
                if (fixedText != text) { File.WriteAllText(patch, fixedText, new UTF8Encoding(false)); }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动前的环境准备:自举 profile → 部署回环插件 → 以客户端为准同步插件。
    /// 返回本次实际补装的包数量(大于 0 表示服务需要重启一次才能带上新插件)。
    /// </summary>
    internal static int Provision(List<string> log, Action<string> progress)
    {
        log.Clear();
        EnsureWebProfile(progress);
        EnsureLoopbackPlugin(progress);

        if (!HasClientProfile())
        {
            log.Add("这台机器上没有桌面客户端的配置集,跳过插件同步(网页端使用自带插件集)。");
            return 0;
        }
        Report r = Check();
        if (!r.Ok) { log.Add("插件对比失败:" + r.Error); return 0; }
        if (r.InSync)
        {
            log.Add("插件环境已与客户端一致(共 " + r.DesktopCount + " 个 bundle)。");
            return 0;
        }
        log.Add("客户端有 " + r.MissingBundles.Count + " 个插件网页端还没有,开始同步…");
        log.AddRange(Install(r, progress));
        Report after = Check();
        log.Add(after.Ok && after.InSync ? "同步完成,已与客户端一致。" : "同步后仍有缺失,可稍后点「同步客户端插件」重试。");
        return r.MissingBundles.Count;
    }

    /// <summary>对比客户端与网页端的插件清单(只读,不安装)。</summary>
    internal static Report Check()
    {
        Report r = new Report();
        try
        {
            if (!File.Exists(DesktopPkg)) { r.Error = "找不到客户端 profile:" + DesktopPkg; return r; }
            if (!File.Exists(WebPkg)) { r.Error = "找不到网页端 profile:" + WebPkg; return r; }

            string dp = File.ReadAllText(DesktopPkg, Encoding.UTF8);
            string wp = File.ReadAllText(WebPkg, Encoding.UTF8);

            List<string> dBundles = ExtractArray(dp, "bundles");
            List<string> wBundles = ExtractArray(wp, "bundles");
            Dictionary<string, string> dDeps = ExtractMap(dp, "dependencies");

            r.DesktopDeps = dDeps;
            r.DesktopCount = dBundles.Count;
            r.WebCount = wBundles.Count;

            foreach (string b in dBundles)
            {
                // 内置 bundle 由发行版提供,两边一定有,不需要同步
                if (b.StartsWith("@deepseek-ai/")) { continue; }
                if (!wBundles.Contains(b)) { r.MissingBundles.Add(b); }
            }
            return r;
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
            return r;
        }
    }

    /// <summary>
    /// pnpm 会给"需要构建脚本的依赖"写占位符要求人工确认,占位符不是合法布尔值,
    /// 会让 pnpm 安装直接失败(ERR_PNPM_IGNORED_BUILDS),进而整个 add 失败。
    /// 这里按客户端 profile 的做法统一置为 true。
    /// </summary>
    internal static bool NormalizePnpmWorkspace()
    {
        try
        {
            string f = Path.Combine(Sys.ProfilesDir, "web", "pnpm-workspace.yaml");
            if (!File.Exists(f)) { return false; }
            string text = File.ReadAllText(f, Encoding.UTF8);
            string fixedText = Regex.Replace(text, @"(?m)^(\s*[^\s:]+:\s*)set this to true or false\s*$", "$1true");
            if (fixedText != text)
            {
                File.WriteAllText(f, fixedText, new UTF8Encoding(false));
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>包是否已经在 web profile 的 node_modules 里。</summary>
    private static bool PackageInstalled(string pkg)
    {
        try { return Directory.Exists(Path.Combine(Sys.ProfilesDir, "web", "node_modules", pkg.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return false; }
    }

    /// <summary>
    /// 确保某个包出现在 web profile 的 bundles 列表里。
    /// 场景:包已经装好(依赖里有、node_modules 里也有)但 bundles 没登记 ——
    /// 这时 pnpm add 是空操作,不会补登记,只能我们直接写进 package.json。
    /// </summary>
    internal static bool EnsureBundleEntry(string pkg)
    {
        try
        {
            string f = WebPkg;
            if (!File.Exists(f)) { return false; }
            string text = File.ReadAllText(f, Encoding.UTF8);
            int k = text.IndexOf("\"bundles\"", StringComparison.Ordinal);
            if (k < 0) { return false; }
            int open = text.IndexOf('[', k);
            if (open < 0) { return false; }
            int close = text.IndexOf(']', open);
            if (close < 0) { return false; }
            string body = text.Substring(open + 1, close - open - 1);
            if (Regex.IsMatch(body, "\"" + Regex.Escape(pkg) + "\"")) { return false; }

            List<string> entries = new List<string>();
            foreach (Match m in Regex.Matches(body, "\"([^\"]+)\"")) { entries.Add(m.Groups[1].Value); }
            entries.Add(pkg);

            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("        \"").Append(entries[i]).Append('"');
            }
            sb.Append("\n      ]");

            File.WriteAllText(f, text.Substring(0, open) + sb.ToString() + text.Substring(close + 1), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>把缺失的客户端插件补装进 web profile;返回过程日志。</summary>
    internal static List<string> Install(Report r, Action<string> progress)
    {
        List<string> log = new List<string>();
        string dsh = Sys.FindDshCmd();
        if (dsh == null) { log.Add("找不到 dsh.cmd,无法同步。"); return log; }

        if (NormalizePnpmWorkspace()) { log.Add("已修正 web profile 的 pnpm 构建脚本占位符(否则安装必定失败)。"); }

        foreach (string bundle in r.MissingBundles)
        {
            string ver;
            string spec = bundle;
            if (r.DesktopDeps.TryGetValue(bundle, out ver) && !string.IsNullOrEmpty(ver)) { spec = bundle + "@" + ver; }

            if (progress != null) { progress("正在安装 " + spec + " …"); }
            string outp = RunDsh(dsh, "plugin --profile web add " + spec);

            // pnpm 可能因为构建脚本策略返回非零,而包其实已经装好;
            // 所以用"bundles 里是否出现它"判断真实结果,必要时自己补登记。
            System.Threading.Thread.Sleep(500);
            Report again = Check();
            bool ok = again.Ok && !again.MissingBundles.Contains(bundle);
            string note = "";
            if (!ok && PackageInstalled(bundle) && EnsureBundleEntry(bundle))
            {
                ok = true;
                note = "(包已在 node_modules,已补登记到 bundles)";
            }
            string tail = TailLines(outp, 3);
            log.Add((ok ? "✓ " : "✗ ") + spec + (ok ? " 已同步" + note : " 失败") + (tail.Length > 0 ? "  |  " + tail : ""));
        }
        return log;
    }

    /// <summary>跑一次 dsh 子命令,返回合并输出。</summary>
    private static string RunDsh(string dsh, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + dsh + "\" " + args + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Sys.WorkDir;
            psi.EnvironmentVariables.Remove("DSH_PROFILE");
            psi.EnvironmentVariables.Remove("DSH_PROFILE_DIR");
            psi.EnvironmentVariables.Remove("DSH_SESSION_ID");
            psi.EnvironmentVariables.Remove("DSH_WEB_URL");
            StringBuilder sb = new StringBuilder();
            object gate = new object();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                // pnpm 会往 stderr 持续刷进度;若先同步读完 stdout 再读 stderr,
                // 未读的那条管道缓冲区填满后子进程会阻塞 → 双向死锁。必须异步收。
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) { lock (gate) { sb.AppendLine(e.Data); } } };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) { lock (gate) { sb.AppendLine(e.Data); } } };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(600000))
                {
                    try { p.Kill(); } catch { }
                    return "安装超时(10 分钟未完成)";
                }
                p.WaitForExit();   // 等异步读取把剩余内容 flush 完
            }
            lock (gate) { return sb.ToString().Trim(); }
        }
        catch (Exception ex) { return "执行失败:" + ex.Message; }
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        string[] all = text.Replace("\r", "").Split('\n');
        List<string> keep = new List<string>();
        for (int i = all.Length - 1; i >= 0 && keep.Count < n; i--)
        {
            string s = all[i].Trim();
            if (s.Length > 0) { keep.Insert(0, s); }
        }
        string joined = string.Join(" / ", keep.ToArray());
        return joined.Length > 220 ? joined.Substring(joined.Length - 220) : joined;
    }

    // ---------------------------------------------------------- JSON 轻量提取

    private static List<string> ExtractArray(string json, string key)
    {
        List<string> res = new List<string>();
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) { return res; }
        int open = json.IndexOf('[', k);
        if (open < 0) { return res; }
        int close = json.IndexOf(']', open);
        if (close < 0) { return res; }
        string body = json.Substring(open + 1, close - open - 1);
        foreach (Match m in Regex.Matches(body, "\"([^\"]+)\"")) { res.Add(m.Groups[1].Value); }
        return res;
    }

    private static Dictionary<string, string> ExtractMap(string json, string key)
    {
        Dictionary<string, string> res = new Dictionary<string, string>();
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) { return res; }
        int open = json.IndexOf('{', k);
        if (open < 0) { return res; }
        int close = json.IndexOf('}', open);
        if (close < 0) { return res; }
        string body = json.Substring(open + 1, close - open - 1);
        foreach (Match m in Regex.Matches(body, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\""))
        { res[m.Groups[1].Value] = m.Groups[2].Value; }
        return res;
    }
}
