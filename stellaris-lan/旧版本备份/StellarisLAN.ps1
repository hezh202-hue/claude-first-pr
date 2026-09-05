# ============================================================
#  群星联机助手  v1.0
#  作用：把 SoftEther 虚拟局域网的部署和连接，变成点几下鼠标的事
#  环境：Windows 自带 PowerShell，不需要额外安装任何东西
# ============================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 万一出错，弹窗告诉你原因，而不是黑框一闪就没了
trap {
    $msg = "程序出错了：" + [Environment]::NewLine + [Environment]::NewLine +
           $_.Exception.Message + [Environment]::NewLine + [Environment]::NewLine +
           "位置：" + $_.InvocationInfo.PositionMessage
    try { [System.Windows.Forms.MessageBox]::Show($msg, '群星联机助手 - 出错') | Out-Null }
    catch { Write-Host $msg -ForegroundColor Red; Read-Host '按回车关闭' }
    exit 1
}
$script:CfgDir      = Join-Path $env:APPDATA 'StellarisLan'
$script:CfgFile     = Join-Path $script:CfgDir 'config.json'
$script:ClientUrl   = 'https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/softether-vpnclient-v4.44-9807-rtm-2025.04.16-windows-x86_x64-intel.exe'
$script:ReleasePage = 'https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/tag/v4.44-9807-rtm'
$script:AccountName = 'Stellaris'
$script:NicName     = 'VPN'
$script:Subnet      = '192.168.30.0/24'
$script:Root        = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

# ---------------- 通用小工具 ----------------

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 自动配置防火墙需要管理员权限。
# 如果当前不是管理员，就以管理员身份把自己重新启动一次（会弹 UAC 让你点"是"）。
if (-not (Test-Admin) -and -not $env:STELLARIS_NO_ELEVATE) {
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ErrorAction Stop -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File',
            ([char]34 + $PSCommandPath + [char]34))
        exit 0
    } catch {
        # 用户点了"否"：用普通权限继续跑，只是防火墙那步会自动跳过
    }
}
function Add-Log {
    param($Box, [string]$Text = '', [string]$Color = 'Black')
    $Box.SelectionStart  = $Box.TextLength
    $Box.SelectionLength = 0
    $Box.SelectionColor  = [System.Drawing.Color]::FromName($Color)
    $Box.AppendText($Text + [Environment]::NewLine)
    $Box.ScrollToCaret()
    [System.Windows.Forms.Application]::DoEvents()
}

function Get-VpnCmdCandidates {
    # 列出所有可能的安装位置，找不到时也用来打印诊断信息
    $c = @()
    $bases = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432,
               'C:\Program Files', 'C:\Program Files (x86)') |
             Where-Object { $_ } | Select-Object -Unique
    $names = @('SoftEther VPN Client', 'SoftEther VPN Client Manager', 'SoftEther VPN')
    foreach ($b in $bases) { foreach ($n in $names) { $c += (Join-Path $b ($n + '\vpncmd.exe')) } }
    # 从卸载注册表里读实际安装目录（应对装到自定义路径的情况）
    try {
        $uks = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
        Get-ItemProperty $uks -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like '*SoftEther*' -and $_.InstallLocation } |
            ForEach-Object { $c += (Join-Path $_.InstallLocation 'vpncmd.exe') }
    } catch { }
    return ($c | Select-Object -Unique)
}

function Find-VpnCmd {
    foreach ($path in (Get-VpnCmdCandidates)) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    # 最后兜底：在 Program Files 下浅扫两层
    foreach ($b in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $b) { continue }
        $hit = Get-ChildItem -Path $b -Filter 'vpncmd.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Invoke-VpnCmd {
    # 调用 vpncmd，统一返回 @{ ok = 真/假 ; out = 输出文本 }
    param([string[]]$CmdArgs)
    $exe = Find-VpnCmd
    if (-not $exe) { return @{ ok = $false; out = '找不到 vpncmd.exe' } }
    $all = @('/CLIENT', 'localhost', '/CMD') + $CmdArgs
    $out = & $exe @all 2>&1 | Out-String
    return @{ ok = ($LASTEXITCODE -eq 0); out = $out.Trim() }
}

function Save-Config {
    param($Data)
    if (-not (Test-Path $script:CfgDir)) {
        New-Item -ItemType Directory -Path $script:CfgDir -Force | Out-Null
    }
    $Data | ConvertTo-Json | Set-Content -Path $script:CfgFile -Encoding UTF8
}

function Read-Config {
    if (Test-Path $script:CfgFile) {
        try { return (Get-Content $script:CfgFile -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { }
    }
    return $null
}

# ---------------- 界面骨架 ----------------

function New-Label {
    param([string]$Text, [int]$X, [int]$Y, [int]$W = 90)
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $Text
    $l.Location = New-Object System.Drawing.Point($X, ($Y + 4))
    $l.Size = New-Object System.Drawing.Size($W, 22)
    return $l
}
function New-Text {
    param([int]$X, [int]$Y, [int]$W, [string]$Value = '')
    $t = New-Object System.Windows.Forms.TextBox
    $t.Location = New-Object System.Drawing.Point($X, $Y)
    $t.Size = New-Object System.Drawing.Size($W, 24)
    $t.Text = $Value
    return $t
}
function New-Button {
    param([string]$Text, [int]$X, [int]$Y, [int]$W = 130, [int]$H = 34)
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text
    $b.Location = New-Object System.Drawing.Point($X, $Y)
    $b.Size = New-Object System.Drawing.Size($W, $H)
    return $b
}
function New-LogBox {
    param([int]$X, [int]$Y, [int]$W, [int]$H)
    $r = New-Object System.Windows.Forms.RichTextBox
    $r.Location = New-Object System.Drawing.Point($X, $Y)
    $r.Size = New-Object System.Drawing.Size($W, $H)
    $r.ReadOnly = $true
    $r.BackColor = 'White'
    $r.Font = New-Object System.Drawing.Font('Consolas', 9)
    return $r
}

$form = New-Object System.Windows.Forms.Form
$form.Text = '群星联机助手'
$form.Size = New-Object System.Drawing.Size(730, 660)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Dock = 'Fill'
$tabs.Padding = New-Object System.Drawing.Point(14, 6)
$form.Controls.Add($tabs)

$tabPlayer = New-Object System.Windows.Forms.TabPage
$tabPlayer.Text = '  一键连接（所有人用）  '
$tabPlayer.BackColor = 'White'
$tabHost = New-Object System.Windows.Forms.TabPage
$tabHost.Text = '  部署服务器（房主用）  '
$tabHost.BackColor = 'White'
$tabs.TabPages.AddRange(@($tabPlayer, $tabHost))

# ================= 第一页：一键连接 =================

$pTip = New-Object System.Windows.Forms.Label
$pTip.Text = '把房主给你的信息填进去，点【一键连接】。第一次会自动帮你装客户端、开防火墙。'
$pTip.Location = New-Object System.Drawing.Point(20, 15)
$pTip.Size = New-Object System.Drawing.Size(660, 22)
$pTip.ForeColor = 'DimGray'
$tabPlayer.Controls.Add($pTip)

$cfg = Read-Config
$defServer = if ($cfg) { [string]$cfg.server } else { '' }
$defPort   = if ($cfg -and $cfg.port) { [string]$cfg.port } else { '443' }
$defHub    = if ($cfg -and $cfg.hub)  { [string]$cfg.hub }  else { 'STELLARIS' }
$defUser   = if ($cfg) { [string]$cfg.user } else { '' }
$defPass   = if ($cfg) { [string]$cfg.pass } else { '' }

$pServer = New-Text 130 45  240 $defServer
$pPort   = New-Text 480 45  70  $defPort
$pHub    = New-Text 130 80  240 $defHub
$pUser   = New-Text 130 115 240 $defUser
$pPass   = New-Text 130 150 240 $defPass
$pPass.UseSystemPasswordChar = $true

$pShow = New-Object System.Windows.Forms.CheckBox
$pShow.Text = '显示密码'
$pShow.Location = New-Object System.Drawing.Point(385, 152)
$pShow.Size = New-Object System.Drawing.Size(100, 22)
$pShow.Add_CheckedChanged({ $pPass.UseSystemPasswordChar = -not $pShow.Checked })

$tabPlayer.Controls.AddRange(@(
    (New-Label '服务器地址' 20 45 105),  $pServer,
    (New-Label '端口' 425 45 50),        $pPort,
    (New-Label '虚拟集线器' 20 80 105),  $pHub,
    (New-Label '用户名' 20 115 105),     $pUser,
    (New-Label '密码' 20 150 105),       $pPass, $pShow
))

$btnConnect = New-Button '一键连接' 130 190 150 38
$btnConnect.BackColor = [System.Drawing.Color]::FromArgb(46, 125, 50)
$btnConnect.ForeColor = 'White'
$btnConnect.FlatStyle = 'Flat'
$btnDisconnect = New-Button '断开' 295 190 100 38
$btnCheck      = New-Button '检查状态' 410 190 100 38
$btnManual     = New-Button '手动下载客户端' 525 190 150 38
$tabPlayer.Controls.AddRange(@($btnConnect, $btnDisconnect, $btnCheck, $btnManual))

$pStatus = New-Object System.Windows.Forms.Label
$pStatus.Text = '● 未连接'
$pStatus.Location = New-Object System.Drawing.Point(20, 240)
$pStatus.Size = New-Object System.Drawing.Size(660, 26)
$pStatus.ForeColor = 'Gray'
$pStatus.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 11, [System.Drawing.FontStyle]::Bold)
$tabPlayer.Controls.Add($pStatus)

$pLog = New-LogBox 20 272 670 300
$tabPlayer.Controls.Add($pLog)

function Set-Status {
    param([string]$Text, [string]$Color)
    $pStatus.Text = $Text
    $pStatus.ForeColor = [System.Drawing.Color]::FromName($Color)
    [System.Windows.Forms.Application]::DoEvents()
}

function Get-LanIp {
    $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
          Where-Object { $_.IPAddress -like '192.168.30.*' } | Select-Object -First 1
    if ($ip) { return $ip.IPAddress }
    return $null
}

function Install-Client {
    # 确保 SoftEther 客户端已安装；没装就下载并拉起安装程序
    $exe = Find-VpnCmd
    $svc = Get-Service -Name 'SEVPNCLIENT' -ErrorAction SilentlyContinue
    if ($exe -and $svc) {
        Add-Log $pLog '[1/6] 客户端已安装 ✓' 'Green'
        Add-Log $pLog ('      位置：' + $exe) 'Gray'
        if ($svc.Status -ne 'Running') {
            Add-Log $pLog '      客户端服务没在运行，正在启动...' 'DarkOrange'
            try { Start-Service -Name 'SEVPNCLIENT' -ErrorAction Stop; Add-Log $pLog '      已启动 ✓' 'Green' }
            catch { Add-Log $pLog ('      启动失败：' + $_.Exception.Message) 'Red'; return $false }
        }
        return $true
    }
    if ($exe -and -not $svc) {
        # 装了"仅管理工具"版：有 vpncmd 但没有客户端服务，连不了
        Add-Log $pLog '[1/6] 检测到 vpncmd，但缺少 SoftEther 客户端服务' 'Red'
        Add-Log $pLog ('      位置：' + $exe) 'Gray'
        Add-Log $pLog '      原因：安装时选成了【SoftEther VPN Client Manager (Tools Only)】。' 'Red'
        Add-Log $pLog '      请重新运行安装包，第一个界面务必选【SoftEther VPN Client】。' 'Red'
        return $false
    }

    Add-Log $pLog '[1/6] 没检测到 SoftEther 客户端'
    # 先找同文件夹里随附的安装包。
    # 为什么：国内直连 GitHub 经常失败，所以安装包是跟工具一起发下来的，不用现下。
    $dst = $null
    $bundled = Get-ChildItem -Path $script:Root -Filter '*.exe' -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match 'vpnclient|VPN-Client' } | Select-Object -First 1
    if ($bundled) {
        $dst = $bundled.FullName
        Add-Log $pLog ('      找到随附安装包：' + $bundled.Name + ' ✓') 'Green'
    } else {
        Add-Log $pLog '      文件夹里没有安装包，尝试从 GitHub 下载（约 55 MB，国内多半会失败）...'
        $dst = Join-Path $env:TEMP 'softether-vpnclient-setup.exe'
        try {
            Invoke-WebRequest -Uri $script:ClientUrl -OutFile $dst -UseBasicParsing -TimeoutSec 900
        } catch {
            Add-Log $pLog ('      下载失败：' + $_.Exception.Message) 'Red'
            Add-Log $pLog '      解决办法：找房主要「SoftEther-VPN-Client-安装包.exe」，' 'Red'
            Add-Log $pLog '      跟本工具放在同一个文件夹里，再点一次【一键连接】即可。' 'Red'
            return $false
        }
        Add-Log $pLog '      下载完成。' 'Green'
    }
    Add-Log $pLog '      正在打开安装程序。'
    Add-Log $pLog '      >>> 请在弹出窗口里选【SoftEther VPN Client】，然后一路 Next 到底 <<<' 'Blue'
    Start-Process -FilePath $dst -Wait
    if ((Find-VpnCmd) -and (Get-Service -Name 'SEVPNCLIENT' -ErrorAction SilentlyContinue)) {
        Add-Log $pLog '      安装成功 ✓' 'Green'
        return $true
    }
    Add-Log $pLog '      装完仍未检测到可用的客户端。以下是检查过的位置：' 'Red'
    foreach ($path in (Get-VpnCmdCandidates)) {
        $mark = if (Test-Path $path) { '[有]' } else { '[无]' }
        Add-Log $pLog ('        ' + $mark + ' ' + $path) 'Gray'
    }
    $svc2 = Get-Service -Name 'SEVPNCLIENT' -ErrorAction SilentlyContinue
    Add-Log $pLog ('        客户端服务 SEVPNCLIENT：' + $(if ($svc2) { $svc2.Status } else { '不存在' })) 'Gray'
    Add-Log $pLog '      把上面这段截图发给房主。' 'Red'
    return $false
}

function Start-Connect {
    $pLog.Clear()
    $btnConnect.Enabled = $false
    Set-Status '● 正在连接...' 'DarkOrange'
    try {
        if ([string]::IsNullOrWhiteSpace($pServer.Text) -or [string]::IsNullOrWhiteSpace($pUser.Text)) {
            Add-Log $pLog '请先把服务器地址和用户名填完整。' 'Red'
            Set-Status '● 未连接' 'Gray'
            return
        }
        if (-not (Install-Client)) { Set-Status '● 连接失败' 'Red'; return }

        Save-Config @{ server = $pServer.Text; port = $pPort.Text; hub = $pHub.Text
                       user = $pUser.Text; pass = $pPass.Text }

        # 虚拟网卡：等于给电脑插一块"看不见的网卡"，用它接进虚拟局域网
        Add-Log $pLog '[2/6] 准备虚拟网卡...'
        $r = Invoke-VpnCmd @('NicCreate', $script:NicName)
        if ($r.ok) { Add-Log $pLog '      新建成功 ✓' 'Green' }
        else { Add-Log $pLog '      已存在，直接复用 ✓' 'Green' }

        Add-Log $pLog '[3/6] 写入连接配置...'
        Invoke-VpnCmd @('AccountDisconnect', $script:AccountName) | Out-Null
        Invoke-VpnCmd @('AccountDelete',     $script:AccountName) | Out-Null
        $r = Invoke-VpnCmd @('AccountCreate', $script:AccountName,
                             ('/SERVER:' + $pServer.Text + ':' + $pPort.Text),
                             ('/HUB:' + $pHub.Text),
                             ('/USERNAME:' + $pUser.Text),
                             ('/NICNAME:' + $script:NicName))
        if (-not $r.ok) {
            Add-Log $pLog ('      失败：' + $r.out) 'Red'
            Set-Status '● 连接失败' 'Red'
            return
        }
        Invoke-VpnCmd @('AccountPasswordSet', $script:AccountName,
                        ('/PASSWORD:' + $pPass.Text), '/TYPE:standard') | Out-Null
        Add-Log $pLog '      完成 ✓' 'Green'

        Add-Log $pLog '[4/6] 正在拨号...'
        $r = Invoke-VpnCmd @('AccountConnect', $script:AccountName)
        if (-not $r.ok) {
            Add-Log $pLog ('      失败：' + $r.out) 'Red'
            Add-Log $pLog '      常见原因：地址或密码填错、房主没在安全组放行 443 端口。' 'Red'
            Set-Status '● 连接失败' 'Red'
            return
        }
        Add-Log $pLog '      隧道已建立 ✓' 'Green'

        Add-Log $pLog '[5/6] 等待分配局域网 IP（最多 40 秒）...'
        $myIp = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Seconds 1
            $myIp = Get-LanIp
            if ($myIp) { break }
            [System.Windows.Forms.Application]::DoEvents()
        }
        if ($myIp) { Add-Log $pLog ('      拿到地址：' + $myIp + ' ✓') 'Green' }
        else { Add-Log $pLog '      没拿到 192.168.30.x，可能房主那边 DHCP 没开。' 'DarkOrange' }

        Add-Log $pLog '[6/6] 放行防火墙...'
        if (Test-Admin) {
            try {
                Get-NetAdapter -ErrorAction SilentlyContinue |
                    Where-Object { $_.InterfaceDescription -like '*VPN Client Adapter*' } |
                    ForEach-Object {
                        Set-NetConnectionProfile -InterfaceIndex $_.ifIndex `
                            -NetworkCategory Private -ErrorAction SilentlyContinue
                    }
                $ruleName = '群星联机-虚拟局域网'
                Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
                    Remove-NetFirewallRule -ErrorAction SilentlyContinue
                New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
                    -RemoteAddress $script:Subnet -Profile Any -ErrorAction Stop | Out-Null
                Add-Log $pLog ('      已允许来自 ' + $script:Subnet + ' 的连接 ✓') 'Green'
                Add-Log $pLog '      （只对这个虚拟局域网开放，不影响平时上网安全）' 'Gray'
            } catch {
                Add-Log $pLog ('      防火墙设置失败：' + $_.Exception.Message) 'DarkOrange'
            }
        } else {
            Add-Log $pLog '      当前不是管理员身份，这一步跳过了。' 'DarkOrange'
            Add-Log $pLog '      如果进游戏后看不见别人的房间，用【启动.bat】重开一次本工具。' 'DarkOrange'
        }

        Add-Log $pLog
        Add-Log $pLog '========== 全部就绪，可以开群星了 ==========' 'Green'
        Add-Log $pLog '游戏里走：多人游戏 → 局域网 / LAN 标签页' 'Green'
        Add-Log $pLog '提醒：所有人游戏版本和 MOD 必须完全一致，否则连不上。' 'Gray'
        if ($myIp) { Set-Status ('● 已连接    我的局域网地址：' + $myIp) 'Green' }
        else { Set-Status '● 已连接（未取得局域网地址）' 'DarkOrange' }
    }
    finally { $btnConnect.Enabled = $true }
}

$btnConnect.Add_Click({ Start-Connect })

$btnDisconnect.Add_Click({
    $r = Invoke-VpnCmd @('AccountDisconnect', $script:AccountName)
    if ($r.ok) { Add-Log $pLog '已断开连接。' } else { Add-Log $pLog '当前本来就没连接。' }
    Set-Status '● 未连接' 'Gray'
})

$btnCheck.Add_Click({
    $pLog.Clear()
    if (-not (Find-VpnCmd)) { Add-Log $pLog '还没安装 SoftEther 客户端。' 'Red'; return }
    $r = Invoke-VpnCmd @('AccountStatusGet', $script:AccountName)
    Add-Log $pLog $r.out
    $myIp = Get-LanIp
    if ($myIp) { Set-Status ('● 已连接    我的局域网地址：' + $myIp) 'Green' }
    else { Set-Status '● 未连接' 'Gray' }
})

$btnManual.Add_Click({
    Start-Process $script:ReleasePage
    Add-Log $pLog '已打开下载页。找 softether-vpnclient-...-windows-x86_x64-intel.exe 下载安装。'
    Add-Log $pLog '装完回来再点【一键连接】即可。'
})

# ================= 第二页：部署服务器 =================

$hTip = New-Object System.Windows.Forms.Label
$hTip.Text = '这一页只有房主用，而且只用一次。填完点生成，再照着提示粘到服务器上执行。'
$hTip.Location = New-Object System.Drawing.Point(20, 15)
$hTip.Size = New-Object System.Drawing.Size(670, 22)
$hTip.ForeColor = 'DimGray'
$tabHost.Controls.Add($hTip)

$hServer  = New-Text 130 45  240
$hHub     = New-Text 130 80  240 'STELLARIS'
$hAdminPw = New-Text 130 115 240
$hGamePw  = New-Text 130 150 240
$hCount = New-Object System.Windows.Forms.NumericUpDown
$hCount.Location = New-Object System.Drawing.Point(480, 150)
$hCount.Size = New-Object System.Drawing.Size(60, 24)
$hCount.Minimum = 1
$hCount.Maximum = 30
$hCount.Value = 6

$tabHost.Controls.AddRange(@(
    (New-Label '服务器IP' 20 45 105),   $hServer,
    (New-Label '虚拟集线器' 20 80 105), $hHub,
    (New-Label '管理密码' 20 115 105),  $hAdminPw,
    (New-Label '玩家密码' 20 150 105),  $hGamePw,
    (New-Label '账号个数' 395 150 80),  $hCount
))

$btnGen  = New-Button '生成部署命令并复制' 130 190 190 36
$btnGen.BackColor = [System.Drawing.Color]::FromArgb(21, 101, 192)
$btnGen.ForeColor = 'White'
$btnGen.FlatStyle = 'Flat'
$btnSsh  = New-Button '打开SSH窗口' 335 190 130 36
$btnInfo = New-Button '生成给朋友的信息' 480 190 155 36
$tabHost.Controls.AddRange(@($btnGen, $btnSsh, $btnInfo))

$hSteps = New-Object System.Windows.Forms.Label
$hSteps.Text = '① 椰子云安全组放行 TCP 443/992/5555 → ② 点【生成】 → ③ 点【打开SSH窗口】输 root 密码 → ④ 右键粘贴回车'
$hSteps.Location = New-Object System.Drawing.Point(20, 236)
$hSteps.Size = New-Object System.Drawing.Size(670, 22)
$hSteps.ForeColor = [System.Drawing.Color]::FromArgb(21, 101, 192)
$tabHost.Controls.Add($hSteps)

$hLog = New-LogBox 20 264 670 308
$tabHost.Controls.Add($hLog)

function Get-ServerScript {
    param([string]$Hub, [string]$AdminPw, [string]$GamePw, [int]$Count)
    $players = ((1..$Count) | ForEach-Object { 'p' + $_ }) -join ' '
    # 注意：下面是 PowerShell 的可插值字符串，
    #       $Hub/$AdminPw 等会被替换成真实值；
    #       属于 bash 自己的变量必须用反引号转义，例如 `$u
    $s = @"
#!/bin/bash
set -e
export DEBIAN_FRONTEND=noninteractive
V=/usr/local/vpnserver/vpncmd
echo '==> [1/6] 安装编译工具'
apt-get update -y
apt-get install -y build-essential wget curl
echo '==> [2/6] 下载并编译 SoftEther v4.44'
cd /usr/local
rm -rf vpnserver
wget -q -O st.tar.gz 'https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/softether-vpnserver-v4.44-9807-rtm-2025.04.16-linux-x64-64bit.tar.gz'
tar xzf st.tar.gz
rm -f st.tar.gz
cd /usr/local/vpnserver
printf '1\n1\n1\n' | make >/dev/null
chmod 600 *
chmod 700 vpnserver vpncmd
echo '==> [3/6] 注册开机自启服务'
cat > /etc/systemd/system/softether-vpnserver.service <<'EOS'
[Unit]
Description=SoftEther VPN Server
After=network.target
[Service]
Type=forking
ExecStart=/usr/local/vpnserver/vpnserver start
ExecStop=/usr/local/vpnserver/vpnserver stop
Restart=on-failure
WorkingDirectory=/usr/local/vpnserver
[Install]
WantedBy=multi-user.target
EOS
systemctl daemon-reload
systemctl enable --now softether-vpnserver
sleep 4
echo '==> [4/6] 设置管理密码、建虚拟集线器'
`$V /SERVER localhost:5555 /PASSWORD: /CMD ServerPasswordSet '$AdminPw'
`$V /SERVER localhost:5555 /PASSWORD:'$AdminPw' /CMD HubCreate '$Hub' /PASSWORD:'$AdminPw'
echo '==> [5/6] 开启 SecureNAT 自动发地址'
`$V /SERVER localhost:5555 /PASSWORD:'$AdminPw' /ADMINHUB:'$Hub' /CMD SecureNatEnable
`$V /SERVER localhost:5555 /PASSWORD:'$AdminPw' /ADMINHUB:'$Hub' /CMD DhcpSet /START:192.168.30.10 /END:192.168.30.99 /MASK:255.255.255.0 /EXPIRE:7200 /GW:none /DNS:none /DNS2:none /DOMAIN:none /LOG:yes || echo '(DHCP 微调跳过，不影响使用)'
echo '==> [6/6] 创建玩家账号'
for u in $players; do
  `$V /SERVER localhost:5555 /PASSWORD:'$AdminPw' /ADMINHUB:'$Hub' /CMD UserCreate `$u /GROUP:none /REALNAME:none /NOTE:none
  `$V /SERVER localhost:5555 /PASSWORD:'$AdminPw' /ADMINHUB:'$Hub' /CMD UserPasswordSet `$u /PASSWORD:'$GamePw'
done
echo
echo '=================== 部署完成 ==================='
echo "  服务器地址 : `$(curl -s4 --max-time 5 ifconfig.me)"
echo '  端口       : 443'
echo '  虚拟集线器 : $Hub'
echo '  用户名     : $players'
echo '  密码       : $GamePw'
echo '==============================================='
"@
    return ($s -replace "`r`n", "`n")
}

$btnGen.Add_Click({
    $hLog.Clear()
    if ([string]::IsNullOrWhiteSpace($hAdminPw.Text) -or [string]::IsNullOrWhiteSpace($hGamePw.Text)) {
        Add-Log $hLog '管理密码和玩家密码都要填，不能留空。' 'Red'
        return
    }
    foreach ($c in @("'", '"', '\', '$', '`', ' ')) {
        if ($hAdminPw.Text.Contains($c) -or $hGamePw.Text.Contains($c) -or $hHub.Text.Contains($c)) {
            Add-Log $hLog '密码和集线器名里不要用 引号 反斜杠 美元符 反引号 空格，会把命令拆坏。' 'Red'
            Add-Log $hLog '建议只用字母和数字，比如 Milkyway2077' 'Red'
            return
        }
    }

    $body   = Get-ServerScript -Hub $hHub.Text -AdminPw $hAdminPw.Text -GamePw $hGamePw.Text -Count ([int]$hCount.Value)
    $b64    = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($body))
    $oneCmd = 'echo ' + $b64 + ' | base64 -d > /root/setup.sh && bash /root/setup.sh'
    [System.Windows.Forms.Clipboard]::SetText($oneCmd)

    # 本地也存一份原文，出问题时方便排查
    $local = Join-Path $script:Root 'setup_generated.sh'
    [IO.File]::WriteAllText($local, $body, (New-Object Text.UTF8Encoding($false)))

    Add-Log $hLog '部署命令已生成，并复制到剪贴板 ✓' 'Green'
    Add-Log $hLog ('脚本原文另存了一份：' + $local) 'Gray'
    Add-Log $hLog
    Add-Log $hLog '接下来三步：' 'Blue'
    Add-Log $hLog '  1. 点【打开SSH窗口】，输入服务器 root 密码（输入时不显示字符，正常现象）'
    Add-Log $hLog '  2. 在黑窗口里点鼠标右键 = 粘贴，然后按回车'
    Add-Log $hLog '  3. 等 1~3 分钟，看到"部署完成"就成了'
    Add-Log $hLog
    Add-Log $hLog ('命令总长 ' + $oneCmd.Length + ' 字符，是完整的一行，粘贴时别手动断行。') 'Gray'
})

$btnSsh.Add_Click({
    if ([string]::IsNullOrWhiteSpace($hServer.Text)) {
        Add-Log $hLog '先把服务器 IP 填上。' 'Red'
        return
    }
    Start-Process 'cmd.exe' -ArgumentList '/k', ('ssh root@' + $hServer.Text)
    Add-Log $hLog '已打开 SSH 窗口，请在里面输入 root 密码。'
})

$btnInfo.Add_Click({
    $hLog.Clear()
    $players = ((1..([int]$hCount.Value)) | ForEach-Object { 'p' + $_ }) -join ' / '
    $txt = @"
【群星联机 · 连接信息】
服务器地址：$($hServer.Text)
端口：443
虚拟集线器：$($hHub.Text)
用户名：$players   （每人挑一个，不要重复）
密码：$($hGamePw.Text)

用法：打开"群星联机助手"，第一页填上面这些，点【一键连接】。
连上后进游戏 → 多人游戏 → 局域网 / LAN 标签页。
注意：所有人的游戏版本和 MOD 必须完全一致，否则连不上。
"@
    [System.Windows.Forms.Clipboard]::SetText($txt)
    Add-Log $hLog $txt
    Add-Log $hLog
    Add-Log $hLog '↑ 已复制到剪贴板，直接粘给朋友即可。' 'Green'
})

# ---------------- 启动 ----------------

if (-not (Test-Admin)) {
    Add-Log $pLog '提示：当前不是管理员身份，自动配置防火墙那一步会被跳过。' 'DarkOrange'
    Add-Log $pLog '建议用【启动.bat】打开本工具，它会自动申请管理员权限。' 'DarkOrange'
    Add-Log $pLog
}
Add-Log $pLog '准备就绪。填好房主给的信息，点【一键连接】即可。'

[void]$form.ShowDialog()
