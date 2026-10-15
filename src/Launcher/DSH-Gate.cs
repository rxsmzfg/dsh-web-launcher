// DSH 网页端 —— 浏览器占用闸门。
// exe 自己监听公网端口,第一个带正确令牌的浏览器会被"认领"(发一个 dsh_slot cookie),
// 之后其它浏览器一律 403,实现"同一时间只允许一个浏览器访问"。
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal sealed class Gate
{
    private readonly int listenPort;
    private readonly int upstreamPort;
    private TcpListener listener;
    private volatile bool running;
    private readonly object sync = new object();

    /// <summary>DSH 启动令牌(从服务输出里解析得到);只有带它的链接才允许认领占用。</summary>
    internal volatile string Token;
    /// <summary>占用者标识(null = 空闲)。</summary>
    internal string ClaimId;
    internal string ClaimPeer;
    internal DateTime ClaimedAt;
    internal string LogPath;

    internal Gate(int listenPort, int upstreamPort)
    {
        this.listenPort = listenPort;
        this.upstreamPort = upstreamPort;
    }

    internal bool IsClaimed { get { lock (sync) { return ClaimId != null; } } }

    internal string ClaimSummary
    {
        get
        {
            lock (sync)
            {
                if (ClaimId == null) { return "等待首个浏览器…"; }
                return "已被一个浏览器占用(" + ClaimPeer + " · " + ClaimedAt.ToString("HH:mm:ss") + ")";
            }
        }
    }

    internal void Start()
    {
        Exception last = null;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, listenPort);
                listener.Start();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                listener = null;
                Thread.Sleep(500);
            }
        }
        if (last != null) { throw new Exception("公网端口 " + listenPort + " 无法监听:" + last.Message); }
        running = true;
        Thread t = new Thread(new ThreadStart(AcceptLoop));
        t.IsBackground = true;
        t.Start();
        Log("闸门已监听 0.0.0.0:" + listenPort + " → 127.0.0.1:" + upstreamPort);
    }

    internal void Stop()
    {
        running = false;
        try { if (listener != null) { listener.Stop(); } } catch { }
    }

    /// <summary>释放占用,让下一个浏览器可以认领(操作员在 exe 界面上点按钮时调用)。</summary>
    internal void Release(string why)
    {
        bool had;
        lock (sync) { had = ClaimId != null; ClaimId = null; ClaimPeer = null; }
        Log(had ? ("已释放浏览器占用(" + why + ")") : ("释放请求,但当前本就是空闲(" + why + ")"));
    }

    internal void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    // ------------------------------------------------------------ 接受连接

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(delegate(object o) { Handle((TcpClient)o); }, c);
        }
    }

    private void Handle(TcpClient client)
    {
        NetworkStream cs = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            cs = client.GetStream();
            byte[] head = ReadHead(cs);
            if (head == null) { client.Close(); return; }

            string text = Encoding.UTF8.GetString(head);
            string peer = "?";
            try { peer = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); } catch { }

            string slot = CookieValue(HeaderValue(text, "cookie"), "dsh_slot");
            string target = FirstLineTarget(text);
            string tok = QueryValue(target, "token");

            string decision = Decide(slot, tok, peer);

            if (decision == "deny") { WriteDeny(cs); client.Close(); return; }

            if (decision == "claim")
            {
                string id = Guid.NewGuid().ToString("N");
                lock (sync) { ClaimId = id; ClaimPeer = peer; ClaimedAt = DateTime.Now; }
                Log("浏览器占用已建立 peer=" + peer);
                string body = "<!doctype html><meta charset=\"utf-8\"><title>正在进入…</title>" +
                              "<body style=\"font-family:system-ui;padding:40px;color:#374151\">正在进入 DSH 网页端…</body>";
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string resp = "HTTP/1.1 302 Found\r\n" +
                              "Location: " + (target == null ? "/" : target) + "\r\n" +
                              "Set-Cookie: dsh_slot=" + id + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=31536000\r\n" +
                              "Content-Type: text/html; charset=utf-8\r\n" +
                              "Content-Length: " + payload.Length + "\r\n" +
                              "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                byte[] rb = Encoding.UTF8.GetBytes(resp);
                cs.Write(rb, 0, rb.Length);
                cs.Write(payload, 0, payload.Length);
                cs.Flush();
                client.Close();
                return;
            }

            // allow —— 盲转发(首个请求头已读到,后续整条连接按字节双向搬运,
            // 这样 keep-alive、分块传输、WebSocket 升级都天然支持)
            TcpClient up = new TcpClient();
            up.NoDelay = true;
            up.Connect(IPAddress.Loopback, upstreamPort);
            NetworkStream us = up.GetStream();
            us.Write(head, 0, head.Length);
            us.Flush();

            Thread t1 = new Thread(delegate() { Pump(cs, us); });
            Thread t2 = new Thread(delegate() { Pump(us, cs); });
            t1.IsBackground = true; t2.IsBackground = true;
            t1.Start(); t2.Start();
            t1.Join(); t2.Join();
            try { up.Close(); } catch { }
            try { client.Close(); } catch { }
        }
        catch
        {
            try { if (cs != null) { client.Close(); } } catch { }
        }
    }

    /// <summary>判定:claim=允许并认领 / allow=放行 / deny=拒绝。</summary>
    private string Decide(string slot, string tok, string peer)
    {
        lock (sync)
        {
            if (ClaimId == null)
            {
                string expected = Token;
                if (!string.IsNullOrEmpty(expected) && tok == expected) { return "claim"; }
                Log("拒绝未带正确令牌的访问 peer=" + peer);
                return "deny";
            }
            if (slot != null && slot == ClaimId) { return "allow"; }
            Log("拒绝访问:网页端已被其它浏览器占用 peer=" + peer);
            return "deny";
        }
    }

    private static void Pump(NetworkStream from, NetworkStream to)
    {
        byte[] buf = new byte[16384];
        try
        {
            while (true)
            {
                int n = from.Read(buf, 0, buf.Length);
                if (n <= 0) { break; }
                to.Write(buf, 0, n);
                to.Flush();
            }
        }
        catch { }
        try { to.Close(); } catch { }
    }

    private static byte[] ReadHead(NetworkStream s)
    {
        MemoryStream ms = new MemoryStream();
        byte[] one = new byte[1];
        while (ms.Length < 65536)
        {
            int n;
            try { n = s.Read(one, 0, 1); }
            catch { return null; }
            if (n <= 0) { return null; }
            ms.WriteByte(one[0]);
            byte[] cur = ms.GetBuffer();
            int len = (int)ms.Length;
            if (len >= 4 && cur[len - 4] == 13 && cur[len - 3] == 10 && cur[len - 2] == 13 && cur[len - 1] == 10) { break; }
        }
        return ms.ToArray();
    }

    private void WriteDeny(NetworkStream cs)
    {
        string body = "<!doctype html><meta charset=\"utf-8\"><title>已被占用</title>" +
            "<body style=\"margin:0;font-family:system-ui;background:#f2f3f5;display:flex;align-items:center;justify-content:center;height:100vh\">" +
            "<div style=\"background:#fff;border:1px solid #e5e7eb;border-radius:14px;padding:34px 38px;max-width:520px\">" +
            "<div style=\"font-size:19px;font-weight:700;color:#1f2328;margin-bottom:12px\">这个网页端已被其它浏览器占用</div>" +
            "<div style=\"color:#6b7280;line-height:1.75;font-size:14px\">当前没有开着的网页端是空闲的,所以本次访问被拒绝。<br><br>" +
            "如果你要换一个浏览器使用:请到服务器上打开 <b>DSH 网页端</b> 窗口,点「释放浏览器占用」,然后用带令牌的链接重新打开。<br><br>" +
            "如果你只是刷新页面:请回到原来那个浏览器。</div></div></body>";
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string resp = "HTTP/1.1 403 Forbidden\r\n" +
                      "Content-Type: text/html; charset=utf-8\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "Content-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
        byte[] rb = Encoding.UTF8.GetBytes(resp);
        try { cs.Write(rb, 0, rb.Length); cs.Write(payload, 0, payload.Length); cs.Flush(); } catch { }
    }

    // ------------------------------------------------------------ 小工具

    private static string FirstLineTarget(string head)
    {
        int e = head.IndexOf("\r\n");
        string line = e < 0 ? head : head.Substring(0, e);
        string[] parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static string HeaderValue(string head, string name)
    {
        string[] lines = head.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            int c = l.IndexOf(':');
            if (c <= 0) { continue; }
            if (string.Equals(l.Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase))
            { return l.Substring(c + 1).Trim(); }
        }
        return null;
    }

    private static string CookieValue(string cookieHeader, string name)
    {
        if (cookieHeader == null) { return null; }
        foreach (string part in cookieHeader.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) { continue; }
            if (part.Substring(0, eq).Trim() == name) { return part.Substring(eq + 1).Trim(); }
        }
        return null;
    }

    private static string QueryValue(string target, string name)
    {
        if (target == null) { return null; }
        int q = target.IndexOf('?');
        if (q < 0) { return null; }
        string query = target.Substring(q + 1);
        foreach (string part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) { continue; }
            if (part.Substring(0, eq) == name) { return part.Substring(eq + 1); }
        }
        return null;
    }
}
