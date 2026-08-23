#!/usr/bin/env bash
# Permanent host UFW rules for a VPN subnet — by CIDR, not by tunN name.
# Run once on the OpenVPN host (e.g. dg-vpn) as root after each new VPN_SUBNET.
#
# Example (UDP WSS pool + DNS via Pi-hole on TCP WSS tun):
#   sudo ./host-ufw-vpn-subnet.sh 10.51.32.0/24 10.51.30.1
#
set -euo pipefail

VPN_CIDR="${1:?usage: $0 <vpn-cidr> [dns-ip]}"
DNS_IP="${2:-}"

echo "[ufw] allow forward for $VPN_CIDR (any iface name)"
ufw route allow from "$VPN_CIDR" to any comment "vpn-subnet-out-$VPN_CIDR" || true
ufw route allow from any to "$VPN_CIDR" comment "vpn-subnet-in-$VPN_CIDR" || true

if [ -n "$DNS_IP" ]; then
  echo "[ufw] allow DNS from $VPN_CIDR to $DNS_IP:53"
  ufw allow from "$VPN_CIDR" to "$DNS_IP" port 53 proto udp comment "vpn-dns-udp-$VPN_CIDR" || true
  ufw allow from "$VPN_CIDR" to "$DNS_IP" port 53 proto tcp comment "vpn-dns-tcp-$VPN_CIDR" || true
fi

ufw status numbered | grep -E "$VPN_CIDR|${DNS_IP:-__none__}" || true
echo "[ufw] done — rules survive reboot; safe across tun renames"
