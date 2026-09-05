// ============================================================
//  群星联机助手（整合版）
//  一个 exe 搞定：一键连接 / 状态监视 / 部署服务器
//  只依赖 .NET Framework 4.8（Win10/11 自带），无需安装任何东西
//  编译：csc /target:winexe /codepage:65001 StellarisTool.cs
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ---------------- 数据结构 ----------------

public class Peer
{
    public string Ip;
    public long Rtt = -1;      // -1 = ping 不通
    public bool ArpSeen;       // ARP 表里有 = 确实在线（哪怕它拦了 ping）
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

public class Config
{
    public string Server = "";
    public string Port = "443";
    public string Hub = "STELLARIS";
    public string User = "";
    public string Pass = "";
}

public class RunResult
{
    public int Code;
    public string Output = "";
    public bool Ok { get { return Code == 0; } }
}

// ---------------- 工具函数 ----------------

public static class Util
{
    public const string Prefix = "192.168.30.";
    public const string Gateway = "192.168.30.1";
    public const string Subnet = "192.168.30.0/24";
    public const string AccountName = "Stellaris";
    public const string NicName = "VPN";
    public const string FirewallRule = "Stellaris-LAN-Allow";
    public const string ClientUrl =
        "https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/" +
        "softether-vpnclient-v4.44-9807-rtm-2025.04.16-windows-x86_x64-intel.exe";
    public const string ReleasePage =
        "https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/tag/v4.44-9807-rtm";

    public static string ExeDir
    {
        get { return Path.GetDirectoryName(Application.ExecutablePath); }
    }

    public static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // 找 SoftEther 客户端的命令行工具。找不到返回 null。
    public static string FindVpnCmd()
    {
        foreach (string p in VpnCmdCandidates())
            if (File.Exists(p)) return p;

        // 兜底：装到了非默认目录时，在 Program Files 下浅扫两层
        foreach (string b in new string[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(b) || !Directory.Exists(b)) continue;
            try
            {
                foreach (string d1 in Directory.GetDirectories(b))
                {
                    string c = Path.Combine(d1, "vpncmd.exe");
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
        }
        return null;
    }

    public static List<string> VpnCmdCandidates()
    {
        List<string> c = new List<string>();
        List<string> bases = new List<string>();
        bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        bases.Add(@"C:\Program Files");
        bases.Add(@"C:\Program Files (x86)");
        string[] names = { "SoftEther VPN Client", "SoftEther VPN Client Manager", "SoftEther VPN" };
        foreach (string b in bases)
        {
            if (string.IsNullOrEmpty(b)) continue;
            foreach (string n in names)
            {
                string p = Path.Combine(Path.Combine(b, n), "vpncmd.exe");
                if (!c.Contains(p)) c.Add(p);
            }
        }
        return c;
    }

    public static ServiceController ClientService()
    {
        try
        {
            foreach (ServiceController sc in ServiceController.GetServices())
                if (sc.ServiceName == "SEVPNCLIENT") return sc;
        }
        catch { }
        return null;
    }

    public static RunResult Run(string exe, string args, int timeoutMs)
    {
        RunResult r = new RunResult();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.Default;
            psi.StandardErrorEncoding = Encoding.Default;
            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.Code = -99; }
                else r.Code = p.ExitCode;
                r.Output = (so + se).Trim();
            }
        }
        catch (Exception ex) { r.Code = -1; r.Output = ex.Message; }
        return r;
    }

    public static RunResult VpnCmd(string args, int timeoutMs)
    {
        string exe = FindVpnCmd();
        if (exe == null) { RunResult r = new RunResult(); r.Code = -1; r.Output = "找不到 vpncmd.exe"; return r; }
        return Run(exe, "/CLIENT localhost /CMD " + args, timeoutMs);
    }

    public static string MyLanIp()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && ua.Address.ToString().StartsWith(Prefix))
                        return ua.Address.ToString();
        }
        catch { }
        return null;
    }

    public static long PingOnce(string host, int timeout)
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

    // ---- 配置读写（手写 JSON，避免引入第三方库）----

    public static string CfgPath
    {
        get
        {
            return Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "StellarisLan"), "config.json");
        }
    }

    public static Config LoadConfig()
    {
        Config c = new Config();
        try
        {
            if (!File.Exists(CfgPath)) return c;
            string t = File.ReadAllText(CfgPath, Encoding.UTF8);
            c.Server = JsonGet(t, "server", c.Server);
            c.Port = JsonGet(t, "port", c.Port);
            c.Hub = JsonGet(t, "hub", c.Hub);
            c.User = JsonGet(t, "user", c.User);
            c.Pass = JsonGet(t, "pass", c.Pass);
        }
        catch { }
        return c;
    }

    static string JsonGet(string json, string key, string def)
    {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : def;
    }

    public static void SaveConfig(Config c)
    {
        try
        {
            string dir = Path.GetDirectoryName(CfgPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"server\": \"" + Esc(c.Server) + "\",");
            sb.AppendLine("  \"port\":   \"" + Esc(c.Port) + "\",");
            sb.AppendLine("  \"hub\":    \"" + Esc(c.Hub) + "\",");
            sb.AppendLine("  \"user\":   \"" + Esc(c.User) + "\",");
            sb.AppendLine("  \"pass\":   \"" + Esc(c.Pass) + "\"");
            sb.AppendLine("}");
            File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

// ============================================================
//  主窗口
// ============================================================

public class MainForm : Form
{
    TabControl tabs;
    // --- 连接页 ---
    TextBox txServer, txPort, txHub, txUser, txPass;
    CheckBox ckShow;
    Button btnConnect, btnDisconnect, btnManual;
    Label lbConnStatus;
    RichTextBox logConn;
    // --- 监视页 ---
    Label lbMonBanner, lbMonFoot;
    ListView lvInfo, lvPeers;
    Button btnMonRefresh, btnMonCopy;
    System.Windows.Forms.Timer monTimer;
    int monBusy;
    Snapshot lastSnap = new Snapshot();
    // --- 部署页 ---
    TextBox txDServer, txDHub, txDAdmin, txDGame;
    NumericUpDown numCount;
    Button btnGen, btnSsh, btnInfo;
    RichTextBox logDeploy;

    public MainForm()
    {
        Text = "群星联机助手";
        Size = new Size(820, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        tabs = new TabControl();
        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(16, 6);
        Controls.Add(tabs);

        tabs.TabPages.Add(BuildConnectTab());
        tabs.TabPages.Add(BuildMonitorTab());
        tabs.TabPages.Add(BuildDeployTab());

        monTimer = new System.Windows.Forms.Timer();
        monTimer.Interval = 6000;
        monTimer.Tick += delegate { StartMonRefresh(); };
        monTimer.Start();

        Shown += delegate
        {
            if (!Util.IsAdmin())
            {
                Log(logConn, "提示：当前不是管理员身份，自动配置防火墙那一步会被跳过。", Color.DarkOrange);
                Log(logConn, "建议关掉本程序，右键 →「以管理员身份运行」再打开。", Color.DarkOrange);
                Log(logConn, "", Color.Black);
            }
            Log(logConn, "准备就绪。填好房主给的信息，点【一键连接】即可。", Color.Black);
            StartMonRefresh();
        };
    }

    // ---------------- 通用控件工厂 ----------------

    static Label L(string t, int x, int y, int w)
    {
        Label l = new Label();
        l.Text = t; l.Location = new Point(x, y + 4); l.Size = new Size(w, 22);
        return l;
    }
    static TextBox T(int x, int y, int w, string v)
    {
        TextBox t = new TextBox();
        t.Location = new Point(x, y); t.Size = new Size(w, 24); t.Text = v;
        return t;
    }
    static Button B(string t, int x, int y, int w, int h)
    {
        Button b = new Button();
        b.Text = t; b.Location = new Point(x, y); b.Size = new Size(w, h);
        return b;
    }
    static RichTextBox LogBox(int x, int y, int w, int h)
    {
        RichTextBox r = new RichTextBox();
        r.Location = new Point(x, y); r.Size = new Size(w, h);
        r.ReadOnly = true; r.BackColor = Color.White;
        r.Font = new Font("Consolas", 9F);
        r.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        return r;
    }

    void Log(RichTextBox box, string text, Color c)
    {
        if (box.InvokeRequired) { box.BeginInvoke(new Action<RichTextBox, string, Color>(Log), box, text, c); return; }
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = c;
        box.AppendText(text + Environment.NewLine);
        box.ScrollToCaret();
    }

    // ============================================================
    //  第一页：一键连接
    // ============================================================

    TabPage BuildConnectTab()
    {
        TabPage tp = new TabPage("  一键连接（所有人用）  ");
        tp.BackColor = Color.White;

        Label tip = new Label();
        tip.Text = "把房主给你的信息填进去，点【一键连接】。第一次会自动帮你装客户端、开防火墙。";
        tip.Location = new Point(20, 14); tip.Size = new Size(740, 22); tip.ForeColor = Color.DimGray;
        tp.Controls.Add(tip);

        Config c = Util.LoadConfig();
        txServer = T(130, 44, 250, c.Server);
        txPort = T(490, 44, 70, c.Port);
        txHub = T(130, 79, 250, c.Hub);
        txUser = T(130, 114, 250, c.User);
        txPass = T(130, 149, 250, c.Pass);
        txPass.UseSystemPasswordChar = true;

        ckShow = new CheckBox();
        ckShow.Text = "显示密码"; ckShow.Location = new Point(392, 151); ckShow.Size = new Size(100, 22);
        ckShow.CheckedChanged += delegate { txPass.UseSystemPasswordChar = !ckShow.Checked; };

        tp.Controls.AddRange(new Control[] {
            L("服务器地址", 20, 44, 105), txServer,
            L("端口", 435, 44, 50), txPort,
            L("虚拟集线器", 20, 79, 105), txHub,
            L("用户名", 20, 114, 105), txUser,
            L("密码", 20, 149, 105), txPass, ckShow });

        btnConnect = B("一键连接", 130, 190, 150, 38, true);
        btnConnect.BackColor = Color.FromArgb(46, 125, 50);
        btnConnect.ForeColor = Color.White;
        btnConnect.FlatStyle = FlatStyle.Flat;
        btnConnect.Click += delegate { StartConnect(); };

        btnDisconnect = B("断开", 295, 190, 100, 38);
        btnDisconnect.Click += delegate
        {
            RunResult r = Util.VpnCmd("AccountDisconnect " + Util.AccountName, 15000);
            Log(logConn, r.Ok ? "已断开连接。" : "当前本来就没连接。", Color.Black);
            SetConnStatus("● 未连接", Color.Gray);
        };

        btnManual = B("手动下载客户端", 410, 190, 150, 38);
        btnManual.Click += delegate
        {
            try { Process.Start(Util.ReleasePage); } catch { }
            Log(logConn, "已打开下载页。装完回来再点【一键连接】。", Color.Black);
        };

        tp.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, btnManual });

        lbConnStatus = new Label();
        lbConnStatus.Text = "● 未连接";
        lbConnStatus.Location = new Point(20, 240);
        lbConnStatus.Size = new Size(750, 26);
        lbConnStatus.ForeColor = Color.Gray;
        lbConnStatus.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        tp.Controls.Add(lbConnStatus);

        logConn = LogBox(20, 272, 758, 330);
        tp.Controls.Add(logConn);
        return tp;
    }

    static Button B(string t, int x, int y, int w, int h, bool dummy) { return B(t, x, y, w, h); }

    void SetConnStatus(string t, Color c)
    {
        if (lbConnStatus.InvokeRequired) { lbConnStatus.BeginInvoke(new Action<string, Color>(SetConnStatus), t, c); return; }
        lbConnStatus.Text = t; lbConnStatus.ForeColor = c;
    }

    void StartConnect()
    {
        if (txServer.Text.Trim().Length == 0 || txUser.Text.Trim().Length == 0)
        {
            Log(logConn, "请先把服务器地址和用户名填完整。", Color.Red);
            return;
        }
        Config c = new Config();
        c.Server = txServer.Text.Trim(); c.Port = txPort.Text.Trim();
        c.Hub = txHub.Text.Trim(); c.User = txUser.Text.Trim(); c.Pass = txPass.Text;

        logConn.Clear();
        btnConnect.Enabled = false;
        SetConnStatus("● 正在连接...", Color.DarkOrange);
        ThreadPool.QueueUserWorkItem(delegate { ConnectWorker(c); });
    }

    void ConnectWorker(Config c)
    {
        try
        {
            if (!EnsureClient()) { SetConnStatus("● 连接失败", Color.Red); return; }
            Util.SaveConfig(c);

            // 虚拟网卡 = 给电脑插一块看不见的网卡，用它接进虚拟局域网
            Log(logConn, "[2/6] 准备虚拟网卡...", Color.Black);
            RunResult r = Util.VpnCmd("NicCreate " + Util.NicName, 30000);
            Log(logConn, r.Ok ? "      新建成功 ✓" : "      已存在，直接复用 ✓", Color.Green);

            Log(logConn, "[3/6] 写入连接配置...", Color.Black);
            Util.VpnCmd("AccountDisconnect " + Util.AccountName, 15000);
            Util.VpnCmd("AccountDelete " + Util.AccountName, 15000);
            r = Util.VpnCmd("AccountCreate " + Util.AccountName
                            + " /SERVER:" + c.Server + ":" + c.Port
                            + " /HUB:" + c.Hub
                            + " /USERNAME:" + c.User
                            + " /NICNAME:" + Util.NicName, 20000);
            if (!r.Ok)
            {
                Log(logConn, "      失败：" + r.Output, Color.Red);
                SetConnStatus("● 连接失败", Color.Red);
                return;
            }
            Util.VpnCmd("AccountPasswordSet " + Util.AccountName
                        + " /PASSWORD:" + c.Pass + " /TYPE:standard", 15000);
            Log(logConn, "      完成 ✓", Color.Green);

            Log(logConn, "[4/6] 正在拨号...", Color.Black);
            r = Util.VpnCmd("AccountConnect " + Util.AccountName, 40000);
            if (!r.Ok)
            {
                Log(logConn, "      失败：" + r.Output, Color.Red);
                Log(logConn, "      常见原因：地址或密码填错、房主没在安全组放行 443 端口。", Color.Red);
                SetConnStatus("● 连接失败", Color.Red);
                return;
            }
            Log(logConn, "      隧道已建立 ✓", Color.Green);

            Log(logConn, "[5/6] 等待分配局域网 IP（最多 40 秒）...", Color.Black);
            string ip = null;
            for (int i = 0; i < 40 && ip == null; i++) { Thread.Sleep(1000); ip = Util.MyLanIp(); }
            if (ip != null) Log(logConn, "      拿到地址：" + ip + " ✓", Color.Green);
            else Log(logConn, "      没拿到 192.168.30.x，可能房主那边 DHCP 没开。", Color.DarkOrange);

            Log(logConn, "[6/6] 放行防火墙...", Color.Black);
            ApplyFirewall();

            Log(logConn, "", Color.Black);
            Log(logConn, "========== 全部就绪，可以开群星了 ==========", Color.Green);
            Log(logConn, "切到【状态监视】那一页，能看到还有谁在线。", Color.Green);
            SetConnStatus(ip != null ? "● 已连接    我的局域网地址：" + ip : "● 已连接（未取得局域网地址）",
                          ip != null ? Color.Green : Color.DarkOrange);
            StartMonRefresh();
        }
        catch (Exception ex)
        {
            Log(logConn, "出错：" + ex.Message, Color.Red);
            SetConnStatus("● 连接失败", Color.Red);
        }
        finally
        {
            try { BeginInvoke(new Action(delegate { btnConnect.Enabled = true; })); } catch { }
        }
    }

    bool EnsureClient()
    {
        string exe = Util.FindVpnCmd();
        ServiceController svc = Util.ClientService();

        if (exe != null && svc != null)
        {
            Log(logConn, "[1/6] 客户端已安装 ✓", Color.Green);
            Log(logConn, "      位置：" + exe, Color.Gray);
            try
            {
                svc.Refresh();
                if (svc.Status != ServiceControllerStatus.Running)
                {
                    Log(logConn, "      客户端服务没在运行，正在启动...", Color.DarkOrange);
                    svc.Start();
                    svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    Log(logConn, "      已启动 ✓", Color.Green);
                }
            }
            catch (Exception ex)
            {
                Log(logConn, "      启动服务失败：" + ex.Message, Color.Red);
                Log(logConn, "      多半是没有管理员权限，请右键以管理员身份重开本程序。", Color.Red);
                return false;
            }
            return true;
        }

        if (exe != null && svc == null)
        {
            Log(logConn, "[1/6] 检测到 vpncmd，但缺少 SoftEther 客户端服务", Color.Red);
            Log(logConn, "      位置：" + exe, Color.Gray);
            Log(logConn, "      原因：安装时选成了【Client Manager (Tools Only)】。", Color.Red);
            Log(logConn, "      请重新运行安装包，第一个界面务必选【SoftEther VPN Client】。", Color.Red);
            return false;
        }

        Log(logConn, "[1/6] 没检测到 SoftEther 客户端", Color.Black);
        string setup = FindBundledInstaller();
        if (setup != null)
        {
            Log(logConn, "      找到随附安装包：" + Path.GetFileName(setup) + " ✓", Color.Green);
        }
        else
        {
            Log(logConn, "      文件夹里没有安装包，尝试从 GitHub 下载（约 55 MB，国内多半会失败）...", Color.Black);
            setup = Path.Combine(Path.GetTempPath(), "softether-vpnclient-setup.exe");
            try
            {
                using (WebClient wc = new WebClient()) wc.DownloadFile(Util.ClientUrl, setup);
                Log(logConn, "      下载完成。", Color.Green);
            }
            catch (Exception ex)
            {
                Log(logConn, "      下载失败：" + ex.Message, Color.Red);
                Log(logConn, "      解决办法：找房主要「SoftEther-VPN-Client-安装包.exe」，", Color.Red);
                Log(logConn, "      跟本程序放在同一个文件夹里，再点一次【一键连接】。", Color.Red);
                return false;
            }
        }

        Log(logConn, "      正在打开安装程序。", Color.Black);
        Log(logConn, "      >>> 请在弹出窗口里选【SoftEther VPN Client】，一路 Next 到底 <<<", Color.Blue);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(setup);
            psi.UseShellExecute = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(); }
        }
        catch (Exception ex) { Log(logConn, "      启动安装程序失败：" + ex.Message, Color.Red); return false; }

        if (Util.FindVpnCmd() != null && Util.ClientService() != null)
        {
            Log(logConn, "      安装成功 ✓", Color.Green);
            return true;
        }

        Log(logConn, "      装完仍未检测到可用的客户端。检查过的位置：", Color.Red);
        foreach (string p in Util.VpnCmdCandidates())
            Log(logConn, "        " + (File.Exists(p) ? "[有] " : "[无] ") + p, Color.Gray);
        Log(logConn, "        客户端服务 SEVPNCLIENT：" + (Util.ClientService() == null ? "不存在" : "存在"), Color.Gray);
        Log(logConn, "      切到【状态监视】页点【复制诊断报告】，把内容发给房主。", Color.Red);
        return false;
    }

    string FindBundledInstaller()
    {
        try
        {
            string self = Application.ExecutablePath;
            foreach (string f in Directory.GetFiles(Util.ExeDir, "*.exe"))
            {
                if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                string n = Path.GetFileName(f);
                if (Regex.IsMatch(n, "vpnclient|VPN-Client", RegexOptions.IgnoreCase)) return f;
            }
        }
        catch { }
        return null;
    }

    void ApplyFirewall()
    {
        if (!Util.IsAdmin())
        {
            Log(logConn, "      当前不是管理员身份，这一步跳过了。", Color.DarkOrange);
            Log(logConn, "      如果进游戏后看不见别人的房间，右键以管理员身份重开本程序。", Color.DarkOrange);
            return;
        }
        // 先把虚拟网卡从"公用网络"改成"专用网络"，否则 Windows 会拦掉一切外来连接
        string ps = Path.Combine(Path.GetTempPath(), "stellaris_fw.ps1");
        try
        {
            File.WriteAllText(ps,
                "Get-NetAdapter | Where-Object { $_.InterfaceDescription -like '*VPN Client Adapter*' } |" +
                " ForEach-Object { Set-NetConnectionProfile -InterfaceIndex $_.ifIndex" +
                " -NetworkCategory Private -ErrorAction SilentlyContinue }",
                new UTF8Encoding(true));
            Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\"", 30000);
        }
        catch { }

        Util.Run("netsh", "advfirewall firewall delete rule name=\"" + Util.FirewallRule + "\"", 15000);
        RunResult r = Util.Run("netsh", "advfirewall firewall add rule name=\"" + Util.FirewallRule
                               + "\" dir=in action=allow remoteip=" + Util.Subnet + " profile=any", 15000);
        if (r.Ok)
        {
            Log(logConn, "      已允许来自 " + Util.Subnet + " 的连接 ✓", Color.Green);
            Log(logConn, "      （只对这个虚拟局域网开放，不影响平时上网安全）", Color.Gray);
        }
        else Log(logConn, "      防火墙设置失败：" + r.Output, Color.DarkOrange);
    }

    // ============================================================
    //  第二页：状态监视
    // ============================================================

    TabPage BuildMonitorTab()
    {
        TabPage tp = new TabPage("  状态监视  ");
        tp.BackColor = Color.White;

        lbMonBanner = new Label();
        lbMonBanner.Dock = DockStyle.Top;
        lbMonBanner.Height = 54;
        lbMonBanner.TextAlign = ContentAlignment.MiddleLeft;
        lbMonBanner.Padding = new Padding(16, 0, 0, 0);
        lbMonBanner.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        lbMonBanner.Text = "正在检测...";
        lbMonBanner.ForeColor = Color.Gray;

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 80;
        btnMonRefresh = B("立即刷新", 16, 8, 120, 34);
        btnMonRefresh.Click += delegate { StartMonRefresh(); };
        btnMonCopy = B("复制诊断报告", 148, 8, 140, 34);
        btnMonCopy.Click += delegate { CopyReport(); };
        lbMonFoot = new Label();
        lbMonFoot.Location = new Point(16, 50); lbMonFoot.Size = new Size(760, 22);
        lbMonFoot.ForeColor = Color.Gray; lbMonFoot.Text = "每 6 秒自动刷新一次";
        bottom.Controls.AddRange(new Control[] { btnMonRefresh, btnMonCopy, lbMonFoot });

        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Fill;
        grid.ColumnCount = 2; grid.RowCount = 2;
        grid.Padding = new Padding(12, 8, 12, 8);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        grid.Controls.Add(Head("本机状态"), 0, 0);
        grid.Controls.Add(Head("虚拟局域网里的其他人"), 1, 0);
        lvInfo = MakeList(new string[] { "项目", "值" }, new int[] { 130, 250 });
        lvPeers = MakeList(new string[] { "地址", "延迟", "状态" }, new int[] { 120, 70, 140 });
        grid.Controls.Add(lvInfo, 0, 1);
        grid.Controls.Add(lvPeers, 1, 1);

        tp.Controls.Add(grid);
        tp.Controls.Add(bottom);
        tp.Controls.Add(lbMonBanner);
        return tp;
    }

    static Label Head(string t)
    {
        Label l = new Label();
        l.Text = t; l.Dock = DockStyle.Fill;
        l.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        l.ForeColor = Color.FromArgb(60, 60, 60);
        return l;
    }

    static ListView MakeList(string[] cols, int[] w)
    {
        ListView lv = new ListView();
        lv.Dock = DockStyle.Fill; lv.View = View.Details;
        lv.FullRowSelect = true; lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], w[i]);
        return lv;
    }

    void StartMonRefresh()
    {
        if (Interlocked.CompareExchange(ref monBusy, 1, 0) != 0) return;
        btnMonRefresh.Enabled = false;
        lbMonFoot.Text = "正在扫描...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            Snapshot s = new Snapshot();
            try { Gather(s); }
            catch (Exception ex) { s.Error = ex.Message; }
            try { BeginInvoke(new Action<Snapshot>(RenderMon), s); }
            catch { Interlocked.Exchange(ref monBusy, 0); }
        });
    }

    void Gather(Snapshot s)
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && ua.Address.ToString().StartsWith(Util.Prefix))
                {
                    s.MyLanIp = ua.Address.ToString();
                    s.AdapterName = ni.Name;
                    s.Connected = (ni.OperationalStatus == OperationalStatus.Up);
                }

        ServiceController sc = Util.ClientService();
        s.ServiceState = (sc == null) ? "未安装" : sc.Status.ToString();

        string srv = txServer.Text.Trim();
        if (srv.Length == 0) srv = Util.LoadConfig().Server;
        if (srv.Length > 0) s.PingServer = Util.PingOnce(srv, 1500);
        if (s.Connected) s.PingGateway = Util.PingOnce(Util.Gateway, 1200);

        foreach (Process p in Process.GetProcesses())
        {
            string n;
            try { n = p.ProcessName.ToLowerInvariant(); } catch { continue; }
            if (n.StartsWith("stellaris")) s.StellarisRunning = true;
            if (n == "steam") s.SteamRunning = true;
        }

        if (s.Connected) s.Peers = ScanPeers(s.MyLanIp);
    }

    static List<Peer> ScanPeers(string myIp)
    {
        Dictionary<string, Peer> map = new Dictionary<string, Peer>();
        object gate = new object();
        int pending = 90;
        ManualResetEvent done = new ManualResetEvent(false);

        for (int i = 10; i <= 99; i++)
        {
            string addr = Util.Prefix + i;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long rtt = Util.PingOnce(addr, 600);
                if (rtt >= 0)
                    lock (gate)
                    {
                        Peer pe = new Peer();
                        pe.Ip = addr; pe.Rtt = rtt; pe.IsMe = (addr == myIp);
                        map[addr] = pe;
                    }
                if (Interlocked.Decrement(ref pending) == 0) done.Set();
            });
        }
        done.WaitOne(5000);

        // ping 不通不代表不在线 —— Windows 默认就拦 ICMP。
        // 但 ARP 是二层协议，网卡自己会回应，拦不掉。查 ARP 表补漏。
        foreach (string ip in ReadArp())
        {
            if (!map.ContainsKey(ip))
            {
                Peer pe = new Peer();
                pe.Ip = ip; pe.Rtt = -1; pe.IsMe = (ip == myIp);
                map[ip] = pe;
            }
            map[ip].ArpSeen = true;
        }

        List<Peer> list = new List<Peer>(map.Values);
        list.Sort(delegate (Peer a, Peer b)
        {
            return int.Parse(a.Ip.Substring(Util.Prefix.Length))
                 .CompareTo(int.Parse(b.Ip.Substring(Util.Prefix.Length)));
        });
        return list;
    }

    static List<string> ReadArp()
    {
        List<string> found = new List<string>();
        RunResult r = Util.Run("arp", "-a", 5000);
        foreach (Match m in Regex.Matches(r.Output, @"(192\.168\.30\.\d+)\s+([0-9a-fA-F\-]{17})"))
        {
            string ip = m.Groups[1].Value, mac = m.Groups[2].Value;
            if (ip.EndsWith(".255") || mac.StartsWith("ff-ff")) continue;
            if (ip == Util.Gateway) continue;      // 虚拟网关不是玩家
            if (!found.Contains(ip)) found.Add(ip);
        }
        return found;
    }

    void RenderMon(Snapshot s)
    {
        lastSnap = s;
        if (s.Connected)
        {
            lbMonBanner.Text = "● 已连接虚拟局域网    我的地址 " + s.MyLanIp;
            lbMonBanner.ForeColor = Color.FromArgb(27, 120, 50);
        }
        else
        {
            lbMonBanner.Text = "● 未连接虚拟局域网";
            lbMonBanner.ForeColor = Color.FromArgb(190, 60, 50);
        }

        lvInfo.BeginUpdate(); lvInfo.Items.Clear();
        AddInfo("连接状态", s.Connected ? "已连接" : "未连接");
        AddInfo("我的局域网地址", s.MyLanIp);
        AddInfo("虚拟网卡", s.AdapterName);
        AddInfo("客户端服务", s.ServiceState);
        AddInfo("到服务器延迟", Fmt(s.PingServer));
        AddInfo("到虚拟网关延迟", Fmt(s.PingGateway));
        AddInfo("管理员权限", Util.IsAdmin() ? "有" : "无（防火墙无法自动配置）");
        AddInfo("群星", s.StellarisRunning ? "运行中" : "未运行");
        AddInfo("Steam", s.SteamRunning ? "运行中" : "未运行");
        if (s.Error != null) AddInfo("采集异常", s.Error);
        lvInfo.EndUpdate();

        lvPeers.BeginUpdate(); lvPeers.Items.Clear();
        int others = 0;
        foreach (Peer p in s.Peers)
        {
            string st;
            if (p.IsMe) st = "我自己";
            else if (p.Rtt >= 0) { st = "在线"; others++; }
            else if (p.ArpSeen) { st = "在线(拦了ping)"; others++; }
            else st = "-";
            ListViewItem it = new ListViewItem(new string[] {
                p.Ip, p.Rtt >= 0 ? p.Rtt + " ms" : "-", st });
            it.ForeColor = p.IsMe ? Color.Gray : Color.FromArgb(27, 120, 50);
            lvPeers.Items.Add(it);
        }
        if (s.Peers.Count == 0)
            lvPeers.Items.Add(new ListViewItem(new string[] { "-", "-", "没扫到任何人" }));
        lvPeers.EndUpdate();

        lbMonFoot.Text = "上次刷新 " + DateTime.Now.ToString("HH:mm:ss")
                       + "    局域网里除你之外有 " + others + " 人在线    每 6 秒自动刷新";
        btnMonRefresh.Enabled = true;
        Interlocked.Exchange(ref monBusy, 0);
    }

    void AddInfo(string k, string v) { lvInfo.Items.Add(new ListViewItem(new string[] { k, v })); }
    static string Fmt(long ms) { return ms >= 0 ? ms + " ms" : "不通"; }

    void CopyReport()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("【群星联机 诊断报告】 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("机器名: " + Environment.MachineName);
        sb.AppendLine("系统: " + Environment.OSVersion.VersionString);
        sb.AppendLine("管理员权限: " + (Util.IsAdmin() ? "有" : "无"));
        sb.AppendLine("服务器: " + txServer.Text.Trim() + ":" + txPort.Text.Trim()
                      + "  集线器 " + txHub.Text.Trim() + "  用户 " + txUser.Text.Trim());
        sb.AppendLine("连接状态: " + (lastSnap.Connected ? "已连接" : "未连接"));
        sb.AppendLine("我的局域网地址: " + lastSnap.MyLanIp);
        sb.AppendLine("虚拟网卡: " + lastSnap.AdapterName);
        sb.AppendLine("客户端服务: " + lastSnap.ServiceState);
        sb.AppendLine("到服务器延迟: " + Fmt(lastSnap.PingServer));
        sb.AppendLine("到虚拟网关延迟: " + Fmt(lastSnap.PingGateway));
        sb.AppendLine("群星: " + (lastSnap.StellarisRunning ? "运行中" : "未运行"));
        sb.AppendLine("Steam: " + (lastSnap.SteamRunning ? "运行中" : "未运行"));
        sb.AppendLine("--- 局域网成员 ---");
        if (lastSnap.Peers.Count == 0) sb.AppendLine("(空)");
        foreach (Peer p in lastSnap.Peers)
            sb.AppendLine("  " + p.Ip + "  " + (p.Rtt >= 0 ? p.Rtt + "ms" : "ping不通")
                          + (p.ArpSeen ? "  ARP可见" : "") + (p.IsMe ? "  <- 我" : ""));
        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show("诊断报告已复制到剪贴板，直接粘贴发出去即可。", "群星联机助手");
        }
        catch (Exception ex) { MessageBox.Show("复制失败：" + ex.Message, "群星联机助手"); }
    }

    // ============================================================
    //  第三页：部署服务器
    // ============================================================

    TabPage BuildDeployTab()
    {
        TabPage tp = new TabPage("  部署服务器（房主用）  ");
        tp.BackColor = Color.White;

        Label warn = new Label();
        warn.Text = "⚠ 警告：服务器如果已经部署过，执行这里生成的命令会把现有配置全部推倒重来，"
                  + "所有账号密码重置。没特殊理由不要碰这一页。";
        warn.Location = new Point(20, 12); warn.Size = new Size(760, 40);
        warn.ForeColor = Color.FromArgb(190, 60, 50);
        tp.Controls.Add(warn);

        txDServer = T(130, 60, 250, "");
        txDHub = T(130, 95, 250, "STELLARIS");
        txDAdmin = T(130, 130, 250, "");
        txDGame = T(130, 165, 250, "");
        numCount = new NumericUpDown();
        numCount.Location = new Point(490, 165); numCount.Size = new Size(60, 24);
        numCount.Minimum = 1; numCount.Maximum = 30; numCount.Value = 8;

        tp.Controls.AddRange(new Control[] {
            L("服务器IP", 20, 60, 105), txDServer,
            L("虚拟集线器", 20, 95, 105), txDHub,
            L("管理密码", 20, 130, 105), txDAdmin,
            L("玩家密码", 20, 165, 105), txDGame,
            L("账号个数", 400, 165, 85), numCount });

        Label hint = new Label();
        hint.Text = "「管理密码」是 SoftEther 的管理口令，不要填服务器的 root 登录密码 —— 两把钥匙必须不一样。";
        hint.Location = new Point(20, 196); hint.Size = new Size(760, 22); hint.ForeColor = Color.DimGray;
        tp.Controls.Add(hint);

        btnGen = B("生成部署命令并复制", 130, 224, 190, 36);
        btnGen.BackColor = Color.FromArgb(21, 101, 192);
        btnGen.ForeColor = Color.White; btnGen.FlatStyle = FlatStyle.Flat;
        btnGen.Click += delegate { GenDeploy(); };

        btnSsh = B("打开SSH窗口", 335, 224, 130, 36);
        btnSsh.Click += delegate
        {
            if (txDServer.Text.Trim().Length == 0) { Log(logDeploy, "先把服务器 IP 填上。", Color.Red); return; }
            try { Process.Start("cmd.exe", "/k ssh root@" + txDServer.Text.Trim()); } catch (Exception ex)
            { Log(logDeploy, "打开失败：" + ex.Message, Color.Red); }
            Log(logDeploy, "已打开 SSH 窗口，请在里面输入 root 密码（输入时不显示字符，正常）。", Color.Black);
        };

        btnInfo = B("生成给朋友的信息", 480, 224, 155, 36);
        btnInfo.Click += delegate { GenFriendInfo(); };

        tp.Controls.AddRange(new Control[] { btnGen, btnSsh, btnInfo });

        Label steps = new Label();
        steps.Text = "① 云控制台安全组放行 TCP 443 → ② 点【生成】 → ③ 点【打开SSH窗口】输 root 密码 → ④ 右键粘贴回车";
        steps.Location = new Point(20, 268); steps.Size = new Size(760, 22);
        steps.ForeColor = Color.FromArgb(21, 101, 192);
        tp.Controls.Add(steps);

        logDeploy = LogBox(20, 296, 758, 306);
        tp.Controls.Add(logDeploy);
        return tp;
    }

    void GenDeploy()
    {
        logDeploy.Clear();
        string hub = txDHub.Text.Trim(), adm = txDAdmin.Text, game = txDGame.Text;
        if (adm.Length == 0 || game.Length == 0)
        {
            Log(logDeploy, "管理密码和玩家密码都要填，不能留空。", Color.Red); return;
        }
        foreach (string bad in new string[] { "'", "\"", "\\", "$", "`", " " })
            if (adm.Contains(bad) || game.Contains(bad) || hub.Contains(bad))
            {
                Log(logDeploy, "密码和集线器名里不要用 引号 反斜杠 美元符 反引号 空格，会把命令拆坏。", Color.Red);
                Log(logDeploy, "建议只用字母和数字，比如 Milkyway2077", Color.Red);
                return;
            }
        if (adm == game)
        {
            Log(logDeploy, "管理密码和玩家密码不能一样 —— 玩家密码是要发给别人的。", Color.Red); return;
        }

        string script = BuildServerScript(hub, adm, game, (int)numCount.Value);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string cmd = "echo " + b64 + " | base64 -d > /root/setup.sh && bash /root/setup.sh";
        try { Clipboard.SetText(cmd); } catch { }
        try { File.WriteAllText(Path.Combine(Util.ExeDir, "setup_generated.sh"), script, new UTF8Encoding(false)); }
        catch { }

        Log(logDeploy, "部署命令已生成，并复制到剪贴板 ✓", Color.Green);
        Log(logDeploy, "脚本原文另存了一份：setup_generated.sh", Color.Gray);
        Log(logDeploy, "", Color.Black);
        Log(logDeploy, "接下来三步：", Color.Blue);
        Log(logDeploy, "  1. 点【打开SSH窗口】，输入服务器 root 密码", Color.Black);
        Log(logDeploy, "  2. 在黑窗口里点鼠标右键 = 粘贴，然后回车", Color.Black);
        Log(logDeploy, "  3. 等 1~3 分钟，看到「部署完成」就成了", Color.Black);
        Log(logDeploy, "", Color.Black);
        Log(logDeploy, "命令总长 " + cmd.Length + " 字符，是完整的一行，粘贴时别手动断行。", Color.Gray);
    }

    // 生成部署到服务器上的 bash 脚本。注意：必须用 \n 换行，Windows 的 \r\n 会让 bash 报错。
    static string BuildServerScript(string hub, string adminPw, string gamePw, int count)
    {
        List<string> players = new List<string>();
        for (int i = 1; i <= count; i++) players.Add("p" + i);
        string plist = string.Join(" ", players.ToArray());
        string V = "/usr/local/vpnserver/vpncmd /SERVER localhost:5555 /PASSWORD:'" + adminPw + "'";

        StringBuilder s = new StringBuilder();
        s.Append("#!/bin/bash\n");
        s.Append("set -e\n");
        s.Append("export DEBIAN_FRONTEND=noninteractive\n");
        s.Append("echo '==> [1/6] 安装编译工具'\n");
        s.Append("apt-get update -y\n");
        s.Append("apt-get install -y build-essential wget curl\n");
        s.Append("echo '==> [2/6] 下载并编译 SoftEther v4.44'\n");
        s.Append("cd /usr/local\n");
        s.Append("rm -rf vpnserver\n");
        s.Append("wget -q -O st.tar.gz 'https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/");
        s.Append("v4.44-9807-rtm/softether-vpnserver-v4.44-9807-rtm-2025.04.16-linux-x64-64bit.tar.gz'\n");
        s.Append("tar xzf st.tar.gz\n");
        s.Append("rm -f st.tar.gz\n");
        s.Append("cd /usr/local/vpnserver\n");
        s.Append("printf '1\\n1\\n1\\n' | make >/dev/null\n");
        s.Append("chmod 600 *\n");
        s.Append("chmod 700 vpnserver vpncmd\n");
        s.Append("echo '==> [3/6] 注册开机自启服务'\n");
        s.Append("cat > /etc/systemd/system/softether-vpnserver.service <<'EOS'\n");
        s.Append("[Unit]\nDescription=SoftEther VPN Server\nAfter=network.target\n");
        s.Append("[Service]\nType=forking\n");
        s.Append("ExecStart=/usr/local/vpnserver/vpnserver start\n");
        s.Append("ExecStop=/usr/local/vpnserver/vpnserver stop\n");
        s.Append("Restart=on-failure\nWorkingDirectory=/usr/local/vpnserver\n");
        s.Append("[Install]\nWantedBy=multi-user.target\nEOS\n");
        s.Append("systemctl daemon-reload\n");
        s.Append("systemctl enable --now softether-vpnserver\n");
        s.Append("sleep 4\n");
        s.Append("echo '==> [4/6] 设置管理密码、建虚拟集线器'\n");
        s.Append("/usr/local/vpnserver/vpncmd /SERVER localhost:5555 /PASSWORD: /CMD ServerPasswordSet '" + adminPw + "'\n");
        s.Append(V + " /CMD HubCreate '" + hub + "' /PASSWORD:'" + adminPw + "'\n");
        s.Append("echo '==> [5/6] 开启 SecureNAT 自动发地址'\n");
        s.Append(V + " /ADMINHUB:'" + hub + "' /CMD SecureNatEnable\n");
        // 只发 IP，不发默认网关：否则大家上网都要绕道服务器，又慢又费流量
        s.Append(V + " /ADMINHUB:'" + hub + "' /CMD DhcpSet /START:192.168.30.10 /END:192.168.30.99");
        s.Append(" /MASK:255.255.255.0 /EXPIRE:7200 /GW:none /DNS:none /DNS2:none /DOMAIN:none /LOG:yes");
        s.Append(" || echo '(DHCP 微调跳过)'\n");
        s.Append("echo '==> [6/6] 创建玩家账号'\n");
        // 一次连接批量执行：逐条开新连接会触发 SoftEther 的防护而报 Error code 2
        s.Append("cat > /root/users.txt <<'EOU'\n");
        foreach (string u in players)
        {
            s.Append("UserCreate " + u + " /GROUP:none /REALNAME:none /NOTE:none\n");
            s.Append("UserPasswordSet " + u + " /PASSWORD:" + gamePw + "\n");
        }
        s.Append("EOU\n");
        s.Append(V + " /ADMINHUB:'" + hub + "' /IN:/root/users.txt\n");
        s.Append("shred -u /root/users.txt 2>/dev/null || rm -f /root/users.txt\n");
        s.Append("echo\n");
        s.Append("echo '=================== 部署完成 ==================='\n");
        s.Append("echo \"  服务器地址 : $(curl -s4 --max-time 5 ifconfig.me)\"\n");
        s.Append("echo '  端口       : 443'\n");
        s.Append("echo '  虚拟集线器 : " + hub + "'\n");
        s.Append("echo '  用户名     : " + plist + "'\n");
        s.Append("echo '  密码       : " + gamePw + "'\n");
        s.Append("echo '==============================================='\n");
        return s.ToString();
    }

    void GenFriendInfo()
    {
        logDeploy.Clear();
        List<string> ps = new List<string>();
        for (int i = 1; i <= (int)numCount.Value; i++) ps.Add("p" + i);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("【群星联机 · 连接信息】");
        sb.AppendLine("服务器地址：" + txDServer.Text.Trim());
        sb.AppendLine("端口：443");
        sb.AppendLine("虚拟集线器：" + txDHub.Text.Trim());
        sb.AppendLine("用户名：" + string.Join(" / ", ps.ToArray()) + "   （每人挑一个，不要重复）");
        sb.AppendLine("密码：" + txDGame.Text);
        sb.AppendLine();
        sb.AppendLine("用法：打开「群星联机助手.exe」，第一页填上面这些，点【一键连接】。");
        sb.AppendLine("连上后进游戏 → 多人游戏。");
        sb.AppendLine("注意：所有人的游戏版本和 MOD 必须完全一致，否则连不上。");
        try { Clipboard.SetText(sb.ToString()); } catch { }
        Log(logDeploy, sb.ToString(), Color.Black);
        Log(logDeploy, "↑ 已复制到剪贴板。", Color.Green);
        Log(logDeploy, "注意：这里显示的是你输入框里的内容，不是从服务器读的 —— "
                     + "如果和服务器实际配置不一致，发出去别人一样连不上。", Color.DarkOrange);
    }

    // ============================================================

    [STAThread]
    public static void Main(string[] argv)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 配置防火墙、创建虚拟网卡都需要管理员权限，所以先把自己提权重开一次。
        // 用户点「否」也不强求，程序照常运行，只是那两步会跳过。
        bool skip = Environment.GetEnvironmentVariable("STELLARIS_NO_ELEVATE") == "1";
        bool relaunched = argv != null && argv.Length > 0 && argv[0] == "--elevated";
        if (!Util.IsAdmin() && !skip && !relaunched)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return;
            }
            catch { /* 用户拒绝了 UAC，继续以普通权限跑 */ }
        }

        Application.Run(new MainForm());
    }
}
