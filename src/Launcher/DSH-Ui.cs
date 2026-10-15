// DSH 网页端 —— 对话式界面(气泡 + 流式打字动效)。
// 与 DSH-Core.cs 一起编译:
//   csc.exe /target:winexe /codepage:65001 /win32icon:DSH-Web.ico /win32manifest:DSH-Web.manifest
//           /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll
//           /out:DSH-Web.exe DSH-Core.cs DSH-Ui.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

// ============================================================ 视觉规范

internal static class Theme
{
    internal static readonly Color Bg = Color.FromArgb(242, 243, 245);
    internal static readonly Color Card = Color.White;
    internal static readonly Color Border = Color.FromArgb(229, 231, 235);
    internal static readonly Color TextMain = Color.FromArgb(31, 35, 40);
    internal static readonly Color TextSub = Color.FromArgb(107, 114, 128);
    internal static readonly Color Accent = Color.FromArgb(37, 99, 235);
    internal static readonly Color Ok = Color.FromArgb(22, 163, 74);
    internal static readonly Color Warn = Color.FromArgb(180, 83, 9);
    internal static readonly Color Err = Color.FromArgb(220, 38, 38);
    internal static readonly Color BubbleMe = Color.FromArgb(238, 242, 255);
    internal static readonly Color Primary = Color.FromArgb(17, 24, 39);
    internal static readonly Color PrimaryHover = Color.FromArgb(55, 65, 81);
    internal static readonly Color CardHover = Color.FromArgb(249, 250, 251);

    internal static readonly Font Body = new Font("Microsoft YaHei UI", 10F);
    internal static readonly Font Small = new Font("Microsoft YaHei UI", 9F);
    internal static readonly Font Title = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
    internal static readonly Font Mono = new Font("Consolas", 9.5F);

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        int d = radius * 2;
        if (d > r.Width) { d = r.Width; }
        if (d > r.Height) { d = r.Height; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal enum BubbleKind { Me, Ok, Warn, Err }

// ============================================================ 气泡

internal sealed class Bubble : Panel
{
    private readonly Label label;
    private readonly BubbleKind kind;
    internal bool AlignRight;
    private string full;
    private int shown;
    private int tickCounter;
    internal bool Streaming;

    private const int PadX = 14;
    private const int PadY = 11;
    private const int MaxWidth = 520;
    internal const int SpeedChars = 2;

    internal Bubble(string text, BubbleKind kind, bool alignRight)
    {
        this.kind = kind;
        AlignRight = alignRight;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Font = Theme.Body;

        full = text ?? "";
        shown = 1;

        label = new Label();
        label.AutoSize = false;
        label.Font = Theme.Body;
        label.ForeColor = Theme.TextMain;
        label.BackColor = Color.Transparent;
        Controls.Add(label);

        Recalc();
        Streaming = full.Length > 1;
    }

    private string ShownText { get { return full.Substring(0, Math.Min(shown, full.Length)); } }

    /// <summary>按当前可见文本重算气泡尺寸(流式过程中会逐步长大)。</summary>
    private void Recalc()
    {
        Size sz = TextRenderer.MeasureText(ShownText + "　", Theme.Body, new Size(MaxWidth - PadX * 2, 4000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int w = Math.Min(MaxWidth, sz.Width + PadX * 2 + 6);
        int h = sz.Height + PadY * 2;
        label.SetBounds(PadX, PadY, w - PadX * 2, h - PadY * 2);
        label.Text = ShownText;
        Size = new Size(w, h);
    }

    /// <summary>推进一帧打字动效;返回是否仍在打字。</summary>
    internal void Advance()
    {
        if (!Streaming) { return; }
        tickCounter++;
        if (tickCounter % 2 == 0) { return; }
        shown += SpeedChars;
        if (shown >= full.Length) { shown = full.Length; Streaming = false; }
        Recalc();
    }

    internal void CompleteNow()
    {
        shown = full.Length;
        Streaming = false;
        Recalc();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color bg = kind == BubbleKind.Me ? Theme.BubbleMe : Theme.Card;
        using (GraphicsPath p = Theme.Round(r, 12))
        using (SolidBrush b = new SolidBrush(bg))
        {
            e.Graphics.FillPath(b, p);
            if (kind != BubbleKind.Me)
            {
                Color line = kind == BubbleKind.Ok ? Color.FromArgb(209, 250, 229)
                           : kind == BubbleKind.Warn ? Color.FromArgb(253, 230, 138)
                           : kind == BubbleKind.Err ? Color.FromArgb(254, 226, 226)
                           : Theme.Border;
                using (Pen pen = new Pen(line)) { e.Graphics.DrawPath(pen, p); }
            }
        }
    }
}

// ============================================================ 打字中指示器

internal sealed class TypingBubble : Panel
{
    private readonly System.Windows.Forms.Timer timer;
    private int phase;

    internal TypingBubble()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Size = new Size(64, 40);
        Visible = false;
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 260;
        timer.Tick += delegate { phase = (phase + 1) % 4; Invalidate(); };
    }

    internal void ShowAtEnd() { Visible = true; if (!timer.Enabled) { timer.Start(); } }
    internal void HideMe() { Visible = false; timer.Stop(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath p = Theme.Round(r, 12))
        using (SolidBrush b = new SolidBrush(Theme.Card))
        using (Pen pen = new Pen(Theme.Border))
        {
            e.Graphics.FillPath(b, p);
            e.Graphics.DrawPath(pen, p);
        }
        for (int i = 0; i < 3; i++)
        {
            int a = (phase == i) ? 70 : 190;
            Color c = Color.FromArgb(a, 120, 130, 150);
            using (SolidBrush b = new SolidBrush(c))
            {
                e.Graphics.FillEllipse(b, 16 + i * 12, 17 + (phase == i ? -3 : 0), 8, 8);
            }
        }
    }
}

// ============================================================ 会话区

internal sealed class ChatView : Panel
{
    private readonly List<Bubble> items = new List<Bubble>();
    internal readonly TypingBubble Typing;
    private int alignShown;

    internal ChatView()
    {
        AutoScroll = true;
        BackColor = Theme.Bg;
        Typing = new TypingBubble();
        Controls.Add(Typing);
    }

    internal Bubble AddBubble(string text, BubbleKind kind, bool right)
    {
        Bubble b = new Bubble(text, kind, right);
        items.Add(b);
        Controls.Add(b);
        Relayout();
        return b;
    }

    internal void Relayout()
    {
        int w = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 32;
        if (w < 240) { w = 240; }
        int y = 14;
        foreach (Bubble b in items)
        {
            if (b.Width > w) { b.Width = w; }
            int x = b.AlignRight ? (w - b.Width + 16) : 16;
            if (x < 16) { x = 16; }
            b.Location = new Point(x, y);
            y += b.Height + 10;
        }
        if (Typing.Visible)
        {
            Typing.Location = new Point(16, y);
            y += Typing.Height + 10;
        }
        AutoScrollMinSize = new Size(0, y + 14);
    }

    internal void ScrollToEnd()
    {
        if (Controls.Count == 0) { return; }
        Control last = Typing.Visible ? (Control)Typing : (items.Count > 0 ? items[items.Count - 1] : null);
        if (last != null) { ScrollControlIntoView(last); }
        alignShown++;
    }
}

// ============================================================ 圆角按钮

internal sealed class PillButton : Button
{
    private readonly bool primary;
    private bool hover;

    internal PillButton(string text, bool primaryButton)
    {
        primary = primaryButton;
        Text = text;
        Font = Theme.Body;
        Height = 38;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color bg = primary ? (hover ? Theme.PrimaryHover : Theme.Primary) : (hover ? Theme.CardHover : Theme.Card);
        if (!Enabled) { bg = Color.FromArgb(243, 244, 246); }
        using (GraphicsPath p = Theme.Round(r, 9))
        using (SolidBrush b = new SolidBrush(bg))
        {
            e.Graphics.FillPath(b, p);
            if (!primary)
            {
                using (Pen pen = new Pen(Theme.Border)) { e.Graphics.DrawPath(pen, p); }
            }
        }
        Color fg = !Enabled ? Theme.TextSub : (primary ? Color.White : Theme.TextMain);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ============================================================ 互斥对话框

internal sealed class GuardDialog : Form
{
    internal GuardDialog(string headline, string body, string primaryText, string secondaryText)
    {
        Text = "DSH 网页端";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 262);
        BackColor = Theme.Bg;
        Font = Theme.Body;
        ShowInTaskbar = true;

        Panel card = new Panel();
        card.SetBounds(14, 14, 532, 234);
        card.BackColor = Theme.Card;
        card.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 14))
            using (Pen pen = new Pen(Theme.Border)) { e.Graphics.DrawPath(pen, p); }
        };
        Controls.Add(card);

        Label head = new Label();
        head.Text = headline;
        head.Font = new Font("Microsoft YaHei UI", 12.5F, FontStyle.Bold);
        head.ForeColor = Theme.TextMain;
        head.BackColor = Color.Transparent;
        head.SetBounds(26, 22, 480, 30);
        card.Controls.Add(head);

        Label bodyLabel = new Label();
        bodyLabel.Text = body;
        bodyLabel.ForeColor = Theme.TextSub;
        bodyLabel.BackColor = Color.Transparent;
        bodyLabel.SetBounds(26, 62, 480, 96);
        card.Controls.Add(bodyLabel);

        PillButton yes = new PillButton(primaryText, true);
        yes.SetBounds(26, 172, 232, 40);
        yes.DialogResult = DialogResult.Yes;
        card.Controls.Add(yes);

        PillButton no = new PillButton(secondaryText, false);
        no.SetBounds(272, 172, 234, 40);
        no.DialogResult = DialogResult.No;
        card.Controls.Add(no);

        AcceptButton = yes;
        CancelButton = no;
    }
}

// ============================================================ 主窗口

internal sealed class ChatForm : Form
{
    private ChatView chat;
    private Panel header;
    private Panel linkCard;
    private TextBox urlBox;
    private PillButton copyBtn;
    private PillButton openBtn;
    private PillButton stopBtn;
    private Label statusText;
    private Panel statusDot;
    private Label claimLabel;
    private PillButton releaseBtn;
    private PillButton syncBtn;
    private volatile bool syncRunning;
    private System.Windows.Forms.Timer gateTimer;

    private readonly Queue<string[]> queue = new Queue<string[]>();
    private readonly object qLock = new object();
    private System.Windows.Forms.Timer pump;
    private Bubble current;
    private volatile bool wantTyping;
    private bool serviceUp;

    internal ChatForm()
    {
        Text = "DSH 网页端 · " + Sys.BuildStamp;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ClientSize = new Size(780, 660);
        MinimumSize = new Size(680, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadIcon();

        // ---- 顶部
        header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 62;
        header.BackColor = Theme.Card;
        header.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen pen = new Pen(Theme.Border)) { e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); }
        };
        Controls.Add(header);

        statusDot = new Panel();
        statusDot.SetBounds(22, 27, 10, 10);
        statusDot.BackColor = Theme.Card;
        statusDot.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(statusDot.Tag == null ? Theme.TextSub : (Color)statusDot.Tag))
            { e.Graphics.FillEllipse(b, 0, 0, 9, 9); }
        };
        header.Controls.Add(statusDot);

        Label title = new Label();
        title.Text = "DSH 网页端";
        title.Font = Theme.Title;
        title.ForeColor = Theme.TextMain;
        title.AutoSize = true;
        title.Location = new Point(42, 18);
        header.Controls.Add(title);

        // 构建标识:一眼看出当前跑的是哪个版本(时间戳取自 exe 文件本身)
        Label build = new Label();
        build.Text = "构建 " + Sys.BuildStamp;
        build.Font = Theme.Small;
        build.ForeColor = Theme.TextSub;
        build.AutoSize = true;
        build.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        build.Location = new Point(header.Width - 220, 24);
        header.Controls.Add(build);
        header.Resize += delegate { build.Location = new Point(header.Width - build.Width - 22, 24); };

        statusText = new Label();
        statusText.Text = "准备中";
        statusText.Font = Theme.Small;
        statusText.ForeColor = Theme.TextSub;
        statusText.AutoSize = true;
        statusText.Location = new Point(170, 24);
        header.Controls.Add(statusText);

        // ---- 底部操作区
        Panel footer = new Panel();
        footer.Dock = DockStyle.Bottom;
        footer.Height = 62;
        footer.BackColor = Theme.Card;
        footer.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen pen = new Pen(Theme.Border)) { e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); }
        };
        Label foot = new Label();
        foot.Text = "关闭窗口即停止服务 · 客户端启动时网页端自动退出";
        foot.Font = Theme.Small;
        foot.ForeColor = Theme.TextSub;
        foot.AutoSize = true;
        foot.Location = new Point(22, 10);
        footer.Controls.Add(foot);

        claimLabel = new Label();
        claimLabel.Text = "占用:等待首个浏览器…";
        claimLabel.Font = Theme.Small;
        claimLabel.ForeColor = Theme.Accent;
        claimLabel.AutoSize = true;
        claimLabel.Location = new Point(22, 32);
        footer.Controls.Add(claimLabel);

        releaseBtn = new PillButton("释放浏览器占用", false);
        releaseBtn.Width = 148;
        releaseBtn.Enabled = false;
        releaseBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        releaseBtn.Click += delegate
        {
            if (Sys.Gate != null) { Sys.Gate.Release("操作员手动释放"); }
            RefreshClaim();
        };
        footer.Controls.Add(releaseBtn);

        syncBtn = new PillButton("同步客户端插件", false);
        syncBtn.Width = 148;
        syncBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        syncBtn.Click += delegate { RunSync(); };
        footer.Controls.Add(syncBtn);

        stopBtn = new PillButton("停止并退出", false);
        stopBtn.Width = 132;
        stopBtn.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        stopBtn.Click += delegate { Close(); };
        footer.Controls.Add(stopBtn);

        EventHandler layoutFooter = delegate
        {
            int right = footer.Width - 16;
            stopBtn.Location = new Point(right - 132, 14);
            releaseBtn.Location = new Point(right - 132 - 8 - 148, 14);
            syncBtn.Location = new Point(right - 132 - 8 - 148 - 8 - 148, 14);
        };
        footer.Resize += layoutFooter;
        layoutFooter(null, EventArgs.Empty);

        // 每秒刷新一次占用状态
        gateTimer = new System.Windows.Forms.Timer();
        gateTimer.Interval = 1000;
        gateTimer.Tick += delegate { RefreshClaim(); };
        gateTimer.Start();

        // ---- 链接卡片
        linkCard = new Panel();
        linkCard.Dock = DockStyle.Bottom;
        linkCard.Height = 118;
        linkCard.BackColor = Theme.Bg;
        linkCard.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(16, 10, linkCard.Width - 33, linkCard.Height - 20);
            using (GraphicsPath p = Theme.Round(r, 12))
            using (SolidBrush b = new SolidBrush(Theme.Card))
            using (Pen pen = new Pen(Theme.Border))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
        };
        Controls.Add(linkCard);
        Controls.Add(footer);

        Label linkCaption = new Label();
        linkCaption.Text = "公网链接";
        linkCaption.Font = Theme.Small;
        linkCaption.ForeColor = Theme.TextSub;
        linkCaption.BackColor = Theme.Card;
        linkCaption.AutoSize = true;
        linkCard.Controls.Add(linkCaption);

        urlBox = new TextBox();
        urlBox.ReadOnly = true;
        urlBox.TabStop = false;
        urlBox.BorderStyle = BorderStyle.None;
        urlBox.Font = Theme.Mono;
        urlBox.ForeColor = Theme.Accent;
        urlBox.BackColor = Theme.Card;
        urlBox.Text = "等待服务启动…";
        linkCard.Controls.Add(urlBox);

        copyBtn = new PillButton("复制链接", true);
        copyBtn.Height = 34;
        copyBtn.Enabled = false;
        copyBtn.Click += delegate { CopyUrl(); };
        linkCard.Controls.Add(copyBtn);

        openBtn = new PillButton("浏览器打开", false);
        openBtn.Height = 34;
        openBtn.Enabled = false;
        openBtn.Click += delegate { OpenUrl(); };
        linkCard.Controls.Add(openBtn);

        // 卡片内布局:链接独占一行(给足宽度),按钮排右下
        EventHandler layoutCard = delegate
        {
            int w = linkCard.Width;
            int left = 30;
            int right = w - 30;
            linkCaption.Location = new Point(left, 16);
            urlBox.SetBounds(left, 38, Math.Max(80, right - left), 22);
            openBtn.SetBounds(right - 122, 66, 122, 34);
            copyBtn.SetBounds(right - 122 - 8 - 110, 66, 110, 34);
        };
        linkCard.Resize += layoutCard;
        linkCard.HandleCreated += delegate { layoutCard(null, EventArgs.Empty); };
        layoutCard(null, EventArgs.Empty);

        // ---- 会话区
        chat = new ChatView();
        chat.Dock = DockStyle.Fill;
        Controls.Add(chat);
        chat.BringToFront();
        chat.Resize += delegate { chat.Relayout(); };

        // ---- 打字泵
        pump = new System.Windows.Forms.Timer();
        pump.Interval = 25;
        pump.Tick += Pump;
        pump.Start();

        Shown += delegate { ThreadPool.QueueUserWorkItem(delegate { Worker(); }); };
        FormClosing += delegate { Sys.StopService(false); };
    }

    /// <summary>窗口图标:优先 exe 同级/工作目录里的 .ico,否则直接用 exe 内嵌图标(保证只拷 exe 也能用)。</summary>
    private static Icon LoadIcon()
    {
        try
        {
            string[] candidates = new string[]
            {
                System.IO.Path.Combine(Sys.WorkDir, "DSH-Web.ico"),
                System.IO.Path.Combine(Sys.ExeDir == null ? "." : Sys.ExeDir, "DSH-Web.ico")
            };
            foreach (string ico in candidates)
            {
                if (System.IO.File.Exists(ico)) { return new Icon(ico); }
            }
        }
        catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    // -------------------------------------------------- 流式消息

    private void Say(string text, BubbleKind kind)
    {
        lock (qLock) { queue.Enqueue(new string[] { text, ((int)kind).ToString() }); }
    }

    private void SetTyping(bool on) { wantTyping = on; }

    private void Pump(object sender, EventArgs e)
    {
        if (current != null && current.Streaming)
        {
            current.Advance();
            chat.Relayout();
            chat.ScrollToEnd();
            return;
        }
        if (current != null) { current = null; }

        string[] next = null;
        lock (qLock) { if (queue.Count > 0) { next = queue.Dequeue(); } }
        if (next != null)
        {
            chat.Typing.HideMe();
            current = chat.AddBubble(next[0], (BubbleKind)int.Parse(next[1]), false);
            chat.ScrollToEnd();
            return;
        }

        if (wantTyping)
        {
            if (!chat.Typing.Visible) { chat.Typing.ShowAtEnd(); chat.Relayout(); chat.ScrollToEnd(); }
        }
        else if (chat.Typing.Visible) { chat.Typing.HideMe(); chat.Relayout(); }
    }

    // -------------------------------------------------- 界面小工具

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { Invoke(new Action<string, Color>(SetStatus), text, color); return; }
        statusText.Text = text;
        statusDot.Tag = color;
        statusDot.Invalidate();
    }

    /// <summary>把客户端装的插件补装进网页端(web profile),让两边环境一致。</summary>
    private void RunSync()
    {
        if (syncRunning) { return; }
        syncRunning = true;
        syncBtn.Enabled = false;
        Say("开始对比客户端与网页端的插件环境…", BubbleKind.Me);
        SetTyping(true);
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                PluginSync.Report rep = PluginSync.Check();
                if (!rep.Ok)
                {
                    SetTyping(false);
                    Say("对比失败:" + rep.Error, BubbleKind.Err);
                    return;
                }
                if (rep.InSync)
                {
                    SetTyping(false);
                    Say("两边已经一致(客户端 " + rep.DesktopCount + " 个 bundle,网页端 " + rep.WebCount + " 个),无需同步。", BubbleKind.Ok);
                    return;
                }
                SetTyping(false);
                Say("客户端有 " + rep.MissingBundles.Count + " 个插件网页端还没有:" + string.Join("、", rep.MissingBundles.ToArray()), BubbleKind.Warn);
                List<string> log = PluginSync.Install(rep, delegate(string m) { Say(m, BubbleKind.Me); });
                foreach (string line in log) { Say(line, line.StartsWith("✓") ? BubbleKind.Ok : BubbleKind.Err); }
                PluginSync.Report after = PluginSync.Check();
                Say(after.InSync ? "同步完成,网页端已与客户端一致。重启网页端后生效。" : "仍有缺失:" + string.Join("、", after.MissingBundles.ToArray()), after.InSync ? BubbleKind.Ok : BubbleKind.Warn);
            }
            catch (Exception ex)
            {
                SetTyping(false);
                Say("同步异常:" + ex.Message, BubbleKind.Err);
            }
            finally
            {
                syncRunning = false;
                try { Invoke(new Action(delegate { syncBtn.Enabled = true; })); } catch { }
            }
        });
    }

    /// <summary>刷新底部的"浏览器占用"状态显示。</summary>
    private void RefreshClaim()
    {
        try
        {
            if (Sys.Gate == null)
            {
                claimLabel.Text = "占用:等待服务启动…";
                claimLabel.ForeColor = Theme.TextSub;
                releaseBtn.Enabled = false;
                return;
            }
            if (!Sys.Gate.IsClaimed)
            {
                claimLabel.Text = "占用:空闲(下一个用链接打开的浏览器会被登记)";
                claimLabel.ForeColor = Theme.Accent;
            }
            else
            {
                claimLabel.Text = "占用:" + Sys.Gate.ClaimSummary;
                claimLabel.ForeColor = Theme.Ok;
            }
            releaseBtn.Enabled = true;
        }
        catch { }
    }

    private void CopyUrl()
    {
        try
        {
            if (urlBox.Text.Length > 0) { Clipboard.SetText(urlBox.Text); SetStatus("链接已复制", Theme.Ok); }
        }
        catch { SetStatus("复制失败", Theme.Err); }
    }

    private void OpenUrl()
    {
        try { Process.Start(urlBox.Text); }
        catch { SetStatus("打开浏览器失败", Theme.Err); }
    }

    // -------------------------------------------------- 主流程

    private void Worker()
    {
        try
        {
            Say("你好,我是 DSH 网页端。正在为你准备远程访问环境…", BubbleKind.Me);

            SetTyping(true);
            Thread.Sleep(300);
            SetTyping(false);

            SetTyping(true);
            Thread.Sleep(500);
            List<ProcInfo> clients = Sys.SkipGuard ? new List<ProcInfo>() : Sys.FindClientProcesses();
            SetTyping(false);

            if (clients.Count > 0)
            {
                int pid = clients[0].Pid;
                Say("检测到 DeepSeek Harness 桌面客户端正在运行(PID " + pid + ")。", BubbleKind.Warn);
                bool killClient = false;
                Invoke(new Action(delegate
                {
                    GuardDialog dlg = new GuardDialog(
                        "网页端与客户端只能同时运行一个",
                        "同一个会话只能有一个写者,因此两者需要二选一。\n\n" +
                        "· 继续:先结束桌面客户端(主进程 " + pid + " 及其全部子进程),再启动网页端。\n" +
                        "· 取消:退出本程序,桌面客户端保持运行。",
                        "关闭客户端并继续",
                        "取消,退出本程序");
                    killClient = dlg.ShowDialog(this) == DialogResult.Yes;
                }));

                if (!killClient)
                {
                    Say("好的,已退出。桌面客户端保持运行。", BubbleKind.Me);
                    SetStatus("已退出", Theme.TextSub);
                    Thread.Sleep(700);
                    Invoke(new Action(delegate { Close(); }));
                    return;
                }
                Say("正在结束桌面客户端…", BubbleKind.Me);
                int top = Sys.TopmostHarnessAncestor(pid);
                Sys.KillTree(top);
                bool gone = Sys.WaitProcessGone(pid, 20000);
                Say(gone ? "客户端已关闭。" : "客户端未能在 20 秒内退出,继续尝试启动网页端。",
                    gone ? BubbleKind.Ok : BubbleKind.Warn);
            }
            else
            {
                Say("桌面客户端未在运行,不会冲突。", BubbleKind.Ok);
            }

            int others = Sys.KillOtherLaunchers();
            if (others > 0)
            {
                Say("发现 " + others + " 个更早的启动器实例,已结束它们并腾出端口。", BubbleKind.Me);
                Thread.Sleep(1500);
            }

            // ---- 前置检查:本机必须已安装 DSH(这个启动器只是"启动器",不带运行环境)
            string dshCli = Sys.FindDshCmd();
            if (dshCli == null)
            {
                // 自动找不到就弹目录选择器,让用户点一下即可,不需要手写任何配置文件
                Say("没有自动找到 DeepSeek Harness,需要你指定一下它的安装目录。", BubbleKind.Warn);

                string chosen = null;
                bool wantPick = false;
                Invoke(new Action(delegate
                {
                    DialogResult r = MessageBox.Show(this,
                        "没有自动找到 DeepSeek Harness。\n\n" +
                        "这个程序是启动器,需要调用本机已安装的 DSH 才能工作。\n\n" +
                        "要现在选择 DSH 的安装目录吗?(选中 DSH 的安装文件夹即可,程序会自己找到里面的 dsh.cmd,并记住这次选择)",
                        "DSH 网页端 · 需要指定 DSH 位置",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) { return; }
                    FolderBrowserDialog fbd = new FolderBrowserDialog();
                    fbd.Description = "请选择 DeepSeek Harness 的安装目录(里面有 resources 文件夹的那个)";
                    fbd.ShowNewFolderButton = false;
                    if (fbd.ShowDialog(this) == DialogResult.OK) { chosen = fbd.SelectedPath; wantPick = true; }
                }));

                if (wantPick && chosen != null)
                {
                    string resolved = Sys.TryResolveDshFrom(chosen);
                    if (resolved == null)
                    {
                        Say("在这个目录里没找到 dsh.cmd:" + chosen, BubbleKind.Err);
                        Say("请选 DSH 的安装根目录(里面能看到 resources 文件夹)。", BubbleKind.Warn);
                        SetStatus("目录无效", Theme.Err);
                        return;
                    }
                    Sys.SaveDshCli(resolved);
                    dshCli = resolved;
                    Say("已记住 DSH 位置:" + resolved, BubbleKind.Ok);
                }
                else
                {
                    Say("已取消。以下是自动查找过的位置,供参考:", BubbleKind.Me);
                    foreach (string line in Sys.DescribeDshSearch().Split('\n'))
                    {
                        if (line.Trim().Length > 0) { Say("  " + line.Trim(), BubbleKind.Me); }
                    }
                    SetStatus("缺少 DSH 运行环境", Theme.Err);
                    return;
                }
            }
            Say("已找到 DSH 运行环境:" + dshCli, BubbleKind.Ok);

            // ---- 环境准备:自举 profile → 部署回环视图插件 → 以客户端为准同步插件
            SetTyping(true);
            List<string> provisionLog = new List<string>();
            try
            {
                PluginSync.Provision(provisionLog, delegate(string m) { Say(m, BubbleKind.Me); });
            }
            catch (Exception ex) { provisionLog.Add("环境准备异常:" + ex.Message); }
            SetTyping(false);
            foreach (string line in provisionLog) { Say(line, BubbleKind.Me); }

            // ---- 防火墙放行(通用机器默认会拦入站)
            SetTyping(true);
            bool fw = Sys.EnsureFirewallRule();
            SetTyping(false);
            Say(fw ? ("已在 Windows 防火墙放行公网端口 " + Sys.PublicPort + "。")
                   : ("未能自动放行防火墙端口 " + Sys.PublicPort + "(需管理员权限);若外网打不开,请手动放行。"),
                fw ? BubbleKind.Ok : BubbleKind.Warn);

            SetTyping(true);
            int owner = Sys.FindPortOwner(Sys.LocalPort);
            if (owner != 0)
            {
                if (!Sys.LooksLikeOurService(Sys.ProcessNameOf(owner)))
                {
                    SetTyping(false);
                    Say("端口 " + Sys.LocalPort + " 被其他程序占用(PID " + owner + " · " + Sys.ProcessNameOf(owner) + "),无法启动。", BubbleKind.Err);
                    SetStatus("启动失败", Theme.Err);
                    return;
                }
                Say("发现之前残留的网页端服务,先结束它。", BubbleKind.Me);
                Sys.KillTree(owner);
                Sys.WaitPortFree(Sys.LocalPort, 15000);
            }
            string token = Sys.StartServiceAndWaitToken();
            SetTyping(false);
            if (token == null)
            {
                Say("网页服务启动失败,45 秒内没有拿到访问令牌。", BubbleKind.Err);
                SetStatus("启动失败", Theme.Err);
                return;
            }
            Say("网页服务已启动,新的访问令牌已生成。", BubbleKind.Ok);

            SetTyping(true);
            Sys.StartGate(token);
            Thread.Sleep(400);
            SetTyping(false);
            Say("公网闸门已开启:第一个用链接打开的浏览器会被登记为占用者,之后其它浏览器一律拒绝访问。", BubbleKind.Ok);

            string url;
            if (Sys.PublicHost != null && Sys.PublicHost.Length > 0)
            {
                url = "http://" + Sys.PublicHost + ":" + Sys.PublicPort + "/?token=" + token;
            }
            else
            {
                // 公网地址探测失败:不编造 IP,退化为局域网/本机地址并提示
                Say("未能探测到本机公网地址(可在外网环境下重试,或在 DSH-Web.ini 里用 publicHost 指定)。", BubbleKind.Warn);
                url = "http://" + (Sys.DetectLanIp() ?? "127.0.0.1") + ":" + Sys.PublicPort + "/?token=" + token;
            }
            Invoke(new Action(delegate
            {
                urlBox.Text = url;
                copyBtn.Enabled = true;
                openBtn.Enabled = true;
                try { Clipboard.SetText(url); } catch { }
            }));

            SetTyping(true);
            string chk = Sys.SelfCheck(url);
            SetTyping(false);
            Say("链路自检:" + chk, chk.StartsWith("通过") ? BubbleKind.Ok : BubbleKind.Warn);

            Say("链接已复制到剪贴板,粘贴到浏览器即可远程访问。同一个浏览器 30 天内不用再输令牌。", BubbleKind.Me);

            serviceUp = true;
            SetStatus("运行中", Theme.Ok);

            // 顺带报一下插件环境是否和客户端一致(只对比,不自动安装)
            try
            {
                PluginSync.Report rep = PluginSync.Check();
                if (rep.Ok && !rep.InSync)
                {
                    Say("提示:客户端有 " + rep.MissingBundles.Count + " 个插件(" + string.Join("、", rep.MissingBundles.ToArray()) +
                        ")网页端还没有,点下方「同步客户端插件」即可补上。", BubbleKind.Warn);
                }
            }
            catch { }

            StartWatchdog();

            Sys.WatchChild(delegate(string line) { Say(line, BubbleKind.Me); },
                           delegate { if (serviceUp) { SetStatus("服务已停止", Theme.TextSub); } });
        }
        catch (Exception ex)
        {
            SetTyping(false);
            Say("发生异常:" + ex.Message, BubbleKind.Err);
            SetStatus("发生错误", Theme.Err);
        }
    }

    // -------------------------------------------------- 守望

    private void StartWatchdog()
    {
        Thread t = new Thread(new ThreadStart(WatchdogLoop));
        t.IsBackground = true;
        t.Start();
    }

    private void WatchdogLoop()
    {
        int hits = 0;
        while (true)
        {
            Thread.Sleep(3000);
            if (!Sys.WatchdogEnabled) { continue; }
            List<ProcInfo> clients;
            try { clients = Sys.FindClientProcesses(); }
            catch { continue; }
            if (clients.Count == 0) { hits = 0; continue; }
            hits++;
            int pid = clients[0].Pid;
            Sys.AppendLog(Sys.WatchLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  发现客户端 PID " + pid + "  (连续第 " + hits + " 次)");
            if (hits < 2) { continue; }

            Sys.AppendLog(Sys.WatchLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  触发自动退出:停止网页端服务");
            serviceUp = false;
            Say("检测到桌面客户端已启动(PID " + pid + "),网页端按约定自动退出。再见。", BubbleKind.Warn);
            SetStatus("检测到客户端 · 已自动停止", Theme.Warn);
            Thread.Sleep(2600);
            Sys.StopService(false);
            try { Invoke(new Action(delegate { Close(); })); } catch { }
            return;
        }
    }
}

