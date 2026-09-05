// 群星联机状态监视器
// 编译：csc /target:winexe /codepage:65001 StatusMonitor.cs
// 只依赖 .NET Framework 4.8（Win10/11 自带），生成的 exe 可直接分发

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

public class Peer
{
    public string Ip;
    public long Rtt = -1;       // -1 表示 ping 不通
    public bool ArpSeen;        // ARP 表里有记录 = 确实在线（哪怕它拦了 ping）
    public bool IsMe;
}

public class Snapshot
{
    public bool Connected;
    public string MyLanIp = "-";
    public string AdapterName = "-";
    public string ServiceState = "未安装";
    public long PingServer = -1;
    public long PingGateway = -1;
    public bool StellarisRunning;
    public bool SteamRunning;
    public List<Peer> Peers = new List<Peer>();
    public string Error;
}

public class MainForm : Form
{
    // 服务器地址：优先读工具写的配置，读不到就用默认值
    string serverIp = "";   // 已抹去硬编码地址，改为从配置文件读取
    const string Gateway = "192.168.30.1";
    const string Prefix  = "192.168.30.";

    Label lblBanner;
    ListView lvInfo, lvPeers;
    Button btnRefresh, btnCopy;
    Label lblFoot;
    System.Windows.Forms.Timer timer;
    int busy;                       // 0=空闲 1=正在刷新，防止重叠
    Snapshot last = new Snapshot();

    public MainForm()
    {
        LoadServerIp();

        Text = "群星联机状态监视器";
        Size = new Size(780, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        lblBanner = new Label();
        lblBanner.Dock = DockStyle.Top;
        lblBanner.Height = 56;
        lblBanner.TextAlign = ContentAlignment.MiddleLeft;
        lblBanner.Padding = new Padding(16, 0, 0, 0);
        lblBanner.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        lblBanner.Text = "正在检测...";
        lblBanner.ForeColor = Color.Gray;
        Controls.Add(lblBanner);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 84;

        btnRefresh = new Button();
        btnRefresh.Text = "立即刷新";
        btnRefresh.Size = new Size(120, 34);
        btnRefresh.Location = new Point(16, 10);
        btnRefresh.Click += delegate { StartRefresh(); };

        btnCopy = new Button();
        btnCopy.Text = "复制诊断报告";
        btnCopy.Size = new Size(140, 34);
        btnCopy.Location = new Point(148, 10);
        btnCopy.Click += delegate { CopyReport(); };

        lblFoot = new Label();
        lblFoot.Location = new Point(16, 52);
        lblFoot.Size = new Size(740, 24);
        lblFoot.ForeColor = Color.Gray;
        lblFoot.Text = "每 6 秒自动刷新一次";

        bottom.Controls.Add(btnRefresh);
        bottom.Controls.Add(btnCopy);
        bottom.Controls.Add(lblFoot);
        Controls.Add(bottom);

        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Fill;
        grid.ColumnCount = 2;
        grid.RowCount = 2;
        grid.Padding = new Padding(12, 8, 12, 8);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        grid.Controls.Add(MakeHeader("本机状态"), 0, 0);
        grid.Controls.Add(MakeHeader("虚拟局域网里的其他人"), 1, 0);

        lvInfo = MakeList(new string[] { "项目", "值" }, new int[] { 130, 230 });
        lvPeers = MakeList(new string[] { "地址", "延迟", "状态" }, new int[] { 120, 70, 130 });
        grid.Controls.Add(lvInfo, 0, 1);
        grid.Controls.Add(lvPeers, 1, 1);
        Controls.Add(grid);
        grid.BringToFront();

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 6000;
        timer.Tick += delegate { StartRefresh(); };
        timer.Start();

        Shown += delegate { StartRefresh(); };
    }

    Label MakeHeader(string t)
    {
        Label l = new Label();
        l.Text = t;
        l.Dock = DockStyle.Fill;
        l.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        l.ForeColor = Color.FromArgb(60, 60, 60);
        return l;
    }

    ListView MakeList(string[] cols, int[] widths)
    {
        ListView lv = new ListView();
        lv.Dock = DockStyle.Fill;
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = false;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
        return lv;
    }

    void LoadServerIp()
    {
        try
        {
            string p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StellarisLan\\config.json");
            if (File.Exists(p))
            {
                string txt = File.ReadAllText(p);
                Match m = Regex.Match(txt, "\"server\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success && m.Groups[1].Value.Length > 0) serverIp = m.Groups[1].Value;
            }
        }
        catch { }
    }

    // ---------------- 采集 ----------------

    void StartRefresh()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;   // 上一轮还没跑完就跳过
        btnRefresh.Enabled = false;
        lblFoot.Text = "正在扫描...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            Snapshot s = new Snapshot();
            try { Gather(s); }
            catch (Exception ex) { s.Error = ex.Message; }
            try { BeginInvoke(new Action<Snapshot>(Render), s); } catch { }
        });
    }

    void Gather(Snapshot s)
    {
        // 1) 找 SoftEther 虚拟网卡拿到的地址
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && ua.Address.ToString().StartsWith(Prefix))
                {
                    s.MyLanIp = ua.Address.ToString();
                    s.AdapterName = ni.Name;
                    s.Connected = (ni.OperationalStatus == OperationalStatus.Up);
                }
            }
        }

        // 2) SoftEther 客户端服务
        try
        {
            var sc = System.ServiceProcess.ServiceController.GetServices()
                       .FirstOrDefault(x => x.ServiceName == "SEVPNCLIENT");
            s.ServiceState = (sc == null) ? "未安装" : sc.Status.ToString();
        }
        catch { s.ServiceState = "查询失败"; }

        // 3) 延迟
        s.PingServer  = PingOnce(serverIp, 1500);
        if (s.Connected) s.PingGateway = PingOnce(Gateway, 1200);

        // 4) 游戏进程
        s.StellarisRunning = Process.GetProcesses().Any(p => SafeName(p).StartsWith("stellaris"));
        s.SteamRunning     = Process.GetProcesses().Any(p => SafeName(p) == "steam");

        // 5) 扫描同网段的其他人
        if (s.Connected) s.Peers = ScanPeers(s.MyLanIp);
    }

    static string SafeName(Process p)
    {
        try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; }
    }

    static long PingOnce(string host, int timeout)
    {
        try
        {
            using (Ping p = new Ping())
            {
                PingReply r = p.Send(host, timeout);
                if (r != null && r.Status == IPStatus.Success) return r.RoundtripTime;
            }
        }
        catch { }
        return -1;
    }

    List<Peer> ScanPeers(string myIp)
    {
        Dictionary<string, Peer> map = new Dictionary<string, Peer>();
        object gate = new object();
        int pending = 90;
        ManualResetEvent done = new ManualResetEvent(false);

        for (int i = 10; i <= 99; i++)
        {
            string addr = Prefix + i;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long rtt = PingOnce(addr, 600);
                if (rtt >= 0)
                {
                    lock (gate)
                    {
                        Peer pe = new Peer();
                        pe.Ip = addr; pe.Rtt = rtt; pe.IsMe = (addr == myIp);
                        map[addr] = pe;
                    }
                }
                if (Interlocked.Decrement(ref pending) == 0) done.Set();
            });
        }
        done.WaitOne(5000);

        // ping 不通不代表不在线 —— Windows 默认就拦 ICMP。
        // 但 ARP 是二层协议，网卡自己会回应，拦不掉。
        // 所以再查一遍 ARP 表，能查到的就是真在线。
        foreach (string ip in ReadArpTable())
        {
            if (!map.ContainsKey(ip))
            {
                Peer pe = new Peer();
                pe.Ip = ip; pe.Rtt = -1; pe.IsMe = (ip == myIp);
                map[ip] = pe;
            }
            map[ip].ArpSeen = true;
        }

        List<Peer> list = map.Values.ToList();
        list.Sort(delegate (Peer a, Peer b)
        {
            int ia = int.Parse(a.Ip.Substring(Prefix.Length));
            int ib = int.Parse(b.Ip.Substring(Prefix.Length));
            return ia.CompareTo(ib);
        });
        return list;
    }

    static List<string> ReadArpTable()
    {
        List<string> found = new List<string>();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("arp", "-a");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.Default;
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                foreach (Match m in Regex.Matches(outp, @"(192\.168\.30\.\d+)\s+([0-9a-fA-F\-]{17})"))
                {
                    string ip = m.Groups[1].Value;
                    string mac = m.Groups[2].Value;
                    // 广播地址、无效条目跳过
                    if (ip.EndsWith(".255") || mac.StartsWith("ff-ff")) continue;
                    // .1 是 SecureNAT 的虚拟网关，不是玩家，单独在左边显示延迟
                    if (ip == Prefix + "1") continue;
                    if (!found.Contains(ip)) found.Add(ip);
                }
            }
        }
        catch { }
        return found;
    }

    // ---------------- 渲染 ----------------

    void Render(Snapshot s)
    {
        last = s;

        if (s.Connected)
        {
            lblBanner.Text = "● 已连接虚拟局域网    我的地址 " + s.MyLanIp;
            lblBanner.ForeColor = Color.FromArgb(27, 120, 50);
        }
        else
        {
            lblBanner.Text = "● 未连接虚拟局域网";
            lblBanner.ForeColor = Color.FromArgb(190, 60, 50);
        }

        lvInfo.BeginUpdate();
        lvInfo.Items.Clear();
        AddInfo("连接状态", s.Connected ? "已连接" : "未连接");
        AddInfo("我的局域网地址", s.MyLanIp);
        AddInfo("虚拟网卡", s.AdapterName);
        AddInfo("客户端服务", s.ServiceState);
        AddInfo("到服务器延迟", Fmt(s.PingServer));
        AddInfo("到虚拟网关延迟", Fmt(s.PingGateway));
        AddInfo("服务器地址", serverIp);
        AddInfo("群星", s.StellarisRunning ? "运行中" : "未运行");
        AddInfo("Steam", s.SteamRunning ? "运行中" : "未运行");
        if (s.Error != null) AddInfo("采集异常", s.Error);
        lvInfo.EndUpdate();

        lvPeers.BeginUpdate();
        lvPeers.Items.Clear();
        int others = 0;
        foreach (Peer p in s.Peers)
        {
            string state;
            if (p.IsMe) state = "我自己";
            else if (p.Rtt >= 0) { state = "在线"; others++; }
            else if (p.ArpSeen) { state = "在线(拦了ping)"; others++; }
            else state = "-";

            ListViewItem it = new ListViewItem(new string[] {
                p.Ip, p.Rtt >= 0 ? p.Rtt + " ms" : "-", state });
            if (p.IsMe) it.ForeColor = Color.Gray;
            else it.ForeColor = Color.FromArgb(27, 120, 50);
            lvPeers.Items.Add(it);
        }
        if (s.Peers.Count == 0)
            lvPeers.Items.Add(new ListViewItem(new string[] { "-", "-", "没扫到任何人" }));
        lvPeers.EndUpdate();

        lblFoot.Text = "上次刷新 " + DateTime.Now.ToString("HH:mm:ss")
                     + "    局域网里除你之外有 " + others + " 人在线    每 6 秒自动刷新";

        btnRefresh.Enabled = true;
        Interlocked.Exchange(ref busy, 0);
    }

    void AddInfo(string k, string v)
    {
        lvInfo.Items.Add(new ListViewItem(new string[] { k, v }));
    }

    static string Fmt(long ms)
    {
        return ms >= 0 ? ms + " ms" : "不通";
    }

    void CopyReport()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("【群星联机 诊断报告】 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("机器名: " + Environment.MachineName);
        sb.AppendLine("系统: " + Environment.OSVersion.VersionString);
        sb.AppendLine("服务器: " + serverIp);
        sb.AppendLine("连接状态: " + (last.Connected ? "已连接" : "未连接"));
        sb.AppendLine("我的局域网地址: " + last.MyLanIp);
        sb.AppendLine("虚拟网卡: " + last.AdapterName);
        sb.AppendLine("客户端服务: " + last.ServiceState);
        sb.AppendLine("到服务器延迟: " + Fmt(last.PingServer));
        sb.AppendLine("到虚拟网关延迟: " + Fmt(last.PingGateway));
        sb.AppendLine("群星: " + (last.StellarisRunning ? "运行中" : "未运行"));
        sb.AppendLine("Steam: " + (last.SteamRunning ? "运行中" : "未运行"));
        sb.AppendLine("--- 局域网成员 ---");
        if (last.Peers.Count == 0) sb.AppendLine("(空)");
        foreach (Peer p in last.Peers)
            sb.AppendLine("  " + p.Ip + "  " + (p.Rtt >= 0 ? p.Rtt + "ms" : "ping不通")
                          + (p.ArpSeen ? "  ARP可见" : "") + (p.IsMe ? "  <- 我" : ""));
        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show("诊断报告已复制到剪贴板，直接粘贴发出去即可。", "群星联机状态监视器");
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败：" + ex.Message, "群星联机状态监视器");
        }
    }

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
