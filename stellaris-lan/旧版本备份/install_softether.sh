#!/bin/bash
# ============================================================
#  群星联机 · SoftEther 虚拟局域网 一键安装脚本
#  适用系统：Debian / Ubuntu (64 位)
#  用法：改好下面 4 个密码 -> 上传到服务器 -> bash install_softether.sh
# ============================================================

# ---------- ① 请务必修改下面这些密码 ----------
VPN_ADMIN_PW="换成你自己的管理密码"     # 服务器总管理密码（只有你知道）
HUB_NAME="STELLARIS"                    # 虚拟集线器名字，可以不改
HUB_ADMIN_PW="换成另一个管理密码"       # 集线器管理密码（只有你知道）
GAME_PW="换成给朋友用的密码"            # 发给朋友的连接密码
PLAYERS="p1 p2 p3 p4 p5 p6"             # 给几个人就写几个账号名
# ------------------------------------------------

set -e   # 任何一步出错就立刻停下，避免装出个半成品

echo "==> [1/6] 安装编译工具"
# SoftEther 官方发的是源码包，需要 gcc 之类的工具把它编译成能运行的程序
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y build-essential wget curl

echo "==> [2/6] 下载并编译 SoftEther VPN Server v4.44"
cd /usr/local
wget -O softether.tar.gz \
  "https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/softether-vpnserver-v4.44-9807-rtm-2025.04.16-linux-x64-64bit.tar.gz"
tar xzf softether.tar.gz
rm -f softether.tar.gz
cd /usr/local/vpnserver
# 编译过程会弹三次许可协议问你同不同意，下面这行等于替你连按三次 "1"（同意）
printf '1\n1\n1\n' | make
# 收紧权限：这些文件里存着密钥，只允许 root 读写
chmod 600 *
chmod 700 vpnserver vpncmd

echo "==> [3/6] 注册成开机自启的系统服务"
cat > /etc/systemd/system/softether-vpnserver.service <<'EOF'
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
EOF
systemctl daemon-reload
systemctl enable --now softether-vpnserver
sleep 3   # 等服务真正起来，不然下一步连不上

echo "==> [4/6] 设置管理密码、创建虚拟集线器"
VC="/usr/local/vpnserver/vpncmd /SERVER localhost:5555"
# 刚装好时管理密码是空的，先立刻设上，这一步最要紧
$VC /PASSWORD: /CMD ServerPasswordSet "$VPN_ADMIN_PW"
# 建一个专门给群星用的虚拟集线器，相当于插一台虚拟交换机
$VC /PASSWORD:"$VPN_ADMIN_PW" /CMD HubCreate "$HUB_NAME" /PASSWORD:"$HUB_ADMIN_PW"

echo "==> [5/6] 打开 SecureNAT（自动给每个人发 IP 地址）"
VH="$VC /PASSWORD:$VPN_ADMIN_PW /ADMINHUB:$HUB_NAME"
$VH /CMD SecureNatEnable
# 只发 IP，不发默认网关和 DNS。
# 为什么？否则你们的电脑会把"上网的默认出口"也改到香港服务器，
# 平时刷网页都要绕一圈，又慢又费流量。我们只要局域网，不要它当出口。
$VH /CMD DhcpSet /START:192.168.30.10 /END:192.168.30.99 /MASK:255.255.255.0 \
    /EXPIRE:7200 /GW:none /DNS:none /DNS2:none /DOMAIN:none /LOG:yes || \
    echo "（DHCP 微调失败，不影响使用，可稍后在图形管理器里改）"

echo "==> [6/6] 创建玩家账号"
for u in $PLAYERS; do
  $VH /CMD UserCreate "$u" /GROUP:none /REALNAME:none /NOTE:none
  $VH /CMD UserPasswordSet "$u" /PASSWORD:"$GAME_PW"
  echo "    已创建账号: $u"
done

IP=$(curl -s4 --max-time 5 ifconfig.me || echo "你的服务器公网IP")

cat <<EOF

============================================================
  安装完成！把下面这段发给朋友们即可
============================================================
  服务器地址 : $IP
  端口       : 443
  虚拟集线器 : $HUB_NAME
  用户名     : p1 / p2 / p3 ... （每人挑一个不重复的）
  密码       : $GAME_PW
============================================================
  别忘了在椰子云控制台的【安全组】里放行 TCP 443 / 992 / 5555
============================================================
EOF
