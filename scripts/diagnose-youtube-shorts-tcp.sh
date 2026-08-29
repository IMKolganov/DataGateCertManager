#!/usr/bin/env bash
# Diagnose YouTube Shorts freezes on OpenVPN TCP WSS (hel-1 class).
#
# Background (2026-08-29 dg-vpn-hel-1):
#   - Symptom: one Short hangs forever; siblings still load; VPN reconnect clears it.
#   - Tunnel/WSS queues usually empty — not a whole-proxy HOL stall.
#   - During hang, tcpdump on tun-tcp showed Google TCP with almost no download
#     bytes (client upload KB, CDN→client ~KB) while other flows still chatted.
#   - YouTube uses QUIC (UDP/443) heavily; QUIC inside TCP-OpenVPN is fragile.
#   - Temporary iptables DROP/REJECT of UDP/443 on tun-tcp stopped reproduction
#     (YouTube fell back to HTTP/2). FORWARD alone is weak with DCO; raw PREROUTING
#     saw real counters. Always clean test rules after A/B.
#   - OpenVPN management is held by openvpn-*-wss (CommandQueue) — do NOT nc :5097.
#     Use manager HTTP: GET http://127.0.0.1:$TCP_API_PORT/api/diagnostics/proxy-sessions
#   - Do not use `exit` in paste-into-SSH one-liners — kills the login shell.
#
# Usage (on the VPN host, as a user with docker + sudo):
#   ./scripts/diagnose-youtube-shorts-tcp.sh
#   ./scripts/diagnose-youtube-shorts-tcp.sh capture 10.51.44.22
#   ./scripts/diagnose-youtube-shorts-tcp.sh capture   # auto-pick busiest CLIENT_LIST virt IP
#   ./scripts/diagnose-youtube-shorts-tcp.sh quic-block    # A/B: force HTTP/2 (keep until test done)
#   ./scripts/diagnose-youtube-shorts-tcp.sh quic-unblock  # remove A/B rules
#
# Env (optional):
#   ENV_FILE=~/site.env.installed
#   TCP_TUN_DEV=tun-tcp
#   PCAP=/tmp/shorts-stuck.pcap
#   CAPTURE_SEC=20
#   CAPTURE_PKTS=120
#
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CMD="${1:-check}"
ARG2="${2:-}"

section() { echo; echo "===== $* ====="; }
ok() { echo "OK   $*"; }
warn() { echo "WARN $*"; }
bad() { echo "FAIL $*"; }

find_env() {
  local c
  for c in \
    "${ENV_FILE:-}" \
    "${HOME}/site.env.installed" \
    "${HOME}/site.env" \
    "${INSTALL_HOME:-}/site.env.installed" \
    "${INSTALL_HOME:-}/site.env" \
    "$SCRIPT_DIR/../site.env"; do
    [[ -n "$c" && -f "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

load_env() {
  local f
  f="$(find_env)" || {
    warn "no site.env / site.env.installed — using defaults"
    TCP_MANAGEMENT_PORT="${TCP_MANAGEMENT_PORT:-5097}"
    UDP_MANAGEMENT_PORT="${UDP_MANAGEMENT_PORT:-5096}"
    TCP_API_PORT="${TCP_API_PORT:-5011}"
    UDP_API_PORT="${UDP_API_PORT:-5010}"
    TCP_TUN_DEV="${TCP_TUN_DEV:-tun-tcp}"
    TCP_VPN_SUBNET="${TCP_VPN_SUBNET:-10.51.44.0}"
    return 0
  }
  echo "ENV  $f"
  set -a
  # shellcheck disable=SC1090
  source "$f"
  set +a
  : "${TCP_MANAGEMENT_PORT:=5097}"
  : "${UDP_MANAGEMENT_PORT:=5096}"
  : "${TCP_API_PORT:=5011}"
  : "${UDP_API_PORT:=5010}"
  : "${TCP_TUN_DEV:=tun-tcp}"
  : "${TCP_VPN_SUBNET:=10.51.40.0}"
}

# Manager owns the single OpenVPN management TCP slot (network_mode:host).
# Clients/virt IPs come from DiagnosticsController → IOpenVpnManagementStatusCache.
proxy_sessions_json() {
  local api_port="${1:-$TCP_API_PORT}"
  curl -fsS --max-time 5 "http://127.0.0.1:${api_port}/api/diagnostics/proxy-sessions" 2>/dev/null || true
}

# Print rows: virt_ip  mgmt_recv  mgmt_sent  cn  real_client  proxy_s2c
parse_sessions_from_api() {
  python3 -c '
import json, sys
raw = sys.stdin.read().strip()
if not raw:
    raise SystemExit(0)
try:
    doc = json.loads(raw)
except json.JSONDecodeError:
    raise SystemExit(0)
data = doc.get("data") or doc.get("Data") or doc
sessions = data.get("sessions") or data.get("Sessions") or []
for s in sessions:
    virt = s.get("openVpnVirtualAddress") or s.get("OpenVpnVirtualAddress") or ""
    cn = s.get("openVpnCommonName") or s.get("OpenVpnCommonName") or "-"
    real = s.get("realClient") or s.get("RealClient") or "-"
    recv = s.get("managementBytesReceived") or s.get("ManagementBytesReceived") or 0
    sent = s.get("managementBytesSent") or s.get("ManagementBytesSent") or 0
    s2c = s.get("proxyServerToClientBytes") or s.get("ProxyServerToClientBytes") or 0
    if virt:
        print(f"{virt}\t{recv}\t{sent}\t{cn}\t{real}\t{s2c}")
'
}

pick_busiest_virt() {
  # highest managementBytesSent, else proxy S2C
  parse_sessions_from_api | sort -t$'\t' -k3 -n | tail -1 | cut -f1
}

have_quic_rules() {
  sudo iptables -L FORWARD -n 2>/dev/null | grep -qE 'tun-tcp.*udp dpt:443.*(REJECT|DROP)' \
    || sudo iptables -t raw -L PREROUTING -n 2>/dev/null | grep -qE 'tun-tcp.*udp dpt:443.*DROP'
}

cmd_check() {
  load_env
  local subnet_prefix="${TCP_VPN_SUBNET%.*}"

  section "1 CONTAINERS"
  for name in openvpn-tcp-wss openvpn-udp-wss nginx datagate-pihole datagate-monitor-xray; do
    local st
    st="$(docker inspect -f '{{.State.Status}}{{if .State.Health}}/{{.State.Health.Status}}{{end}}' "$name" 2>/dev/null || echo missing)"
    if [[ "$st" == running* ]] || [[ "$st" == running/* ]]; then
      ok "$name ($st)"
    elif [[ "$st" == missing ]]; then
      warn "$name not present"
    else
      bad "$name ($st) — clients may still have traffic via cache/other DNS; still fix if Exited"
    fi
  done

  section "2 TCP TUN / DCO / MSS"
  if ip link show "$TCP_TUN_DEV" &>/dev/null; then
    ok "iface $TCP_TUN_DEV exists"
    ip -br addr show "$TCP_TUN_DEV" || true
  else
    bad "iface $TCP_TUN_DEV missing"
  fi
  if lsmod 2>/dev/null | grep -qE '^ovpn($| )|^ovpn_dco'; then
    ok "DCO module loaded: $(lsmod | awk '/^ovpn/{print $1}' | tr '\n' ' ')"
  else
    warn "no ovpn/ovpn-dco in lsmod — userspace crypto or different module name"
  fi
  local mss
  mss="$(docker exec openvpn-tcp-wss sh -c 'grep -E "mssfix" /openvpn-tcp-wss/openvpn.log 2>/dev/null | tail -5' 2>/dev/null || true)"
  if [[ -n "$mss" ]]; then
    echo "$mss"
  else
    warn "could not read mssfix from openvpn-tcp-wss log"
  fi
  echo "sysctl: $(sysctl -n net.core.rmem_max 2>/dev/null || echo '?') (rmem_max; <4194304 may clamp UDP SO_RCVBUF)"

  section "3 LEFTOVER ANTI-QUIC TEST RULES"
  echo "--- FORWARD (head) ---"
  sudo iptables -L FORWARD -n -v --line-numbers 2>/dev/null | head -8 || warn "need sudo for iptables"
  echo "--- raw PREROUTING (head) ---"
  sudo iptables -t raw -L PREROUTING -n -v --line-numbers 2>/dev/null | head -8 || true
  if have_quic_rules; then
    warn "UDP/443 DROP|REJECT on tun-tcp still present — YouTube forced off QUIC; Shorts may look 'fixed'"
    warn "run: $0 quic-unblock   then reconnect phone and retest"
  else
    ok "no tun-tcp UDP/443 anti-QUIC test rules"
  fi

  section "4 PROXY SESSIONS (manager API :${TCP_API_PORT})"
  local json rows
  json="$(proxy_sessions_json "$TCP_API_PORT")"
  if [[ -z "$json" ]]; then
    bad "GET http://127.0.0.1:${TCP_API_PORT}/api/diagnostics/proxy-sessions failed"
    echo "Tip: curl that URL; management is held by openvpn-tcp-wss — do not nc :${TCP_MANAGEMENT_PORT}"
  else
    rows="$(echo "$json" | parse_sessions_from_api)"
    if [[ -z "$rows" ]]; then
      warn "API ok but no sessions with virt IP (nobody on TCP WSS right now?)"
      echo "$json" | python3 -m json.tool 2>/dev/null | head -40 || echo "$json" | head -c 800
    else
      echo "virt_ip          recv      sent      cn  (real)  proxy_s2c"
      echo "$rows" | while IFS=$'\t' read -r virt recv sent cn real s2c; do
        printf '%-15s %9s %9s  %s  (%s)  s2c=%s\n' "$virt" "$recv" "$sent" "$cn" "$real" "$s2c"
      done
      ok "sessions from /api/diagnostics/proxy-sessions (not nc management)"
    fi
  fi

  section "5 HOW TO CAPTURE A STUCK SHORT"
  cat <<'HELP'
While ONE Short is frozen (siblings may still load):

  $0 capture SUBNET.XX
  # or auto-pick highest bytes-sent peer:
  $0 capture

Expect during hang: several Google TCP flows, but CDN→client bytes tiny
(total download ~KB / 20s) while client→Google may still send KB — per-flow stall,
not dead WSS. Reconnect clears 5-tuples.

A/B: $0 quic-block → kill YouTube app → retest Shorts → $0 quic-unblock
If block stops hangs: prefer permanent UDP/443 deny on tun-tcp (host script/UFW)
or steer Shorts users to UDP OpenVPN / Xray.
HELP

  section "6 VERDICT HINTS"
  if have_quic_rules; then
    echo ">>> Anti-QUIC rules active — do not trust 'Shorts OK' until quic-unblock + reconnect."
  elif ! docker inspect -f '{{.State.Running}}' openvpn-tcp-wss 2>/dev/null | grep -q true; then
    echo ">>> openvpn-tcp-wss not running."
  else
    echo ">>> Stack looks idle-clean. Reproduce hang, then: $0 capture <virt_ip>"
  fi
}

summarize_pcap() {
  local pcap="$1"
  [[ -f "$pcap" ]] || { bad "pcap missing: $pcap"; return 1; }
  echo "--- top flows by payload bytes (tcpdump -q length field) ---"
  sudo tcpdump -nn -r "$pcap" -q 2>/dev/null \
    | awk '{
        s=$3; d=$5; gsub(/:$/,"",d); n=$NF
        if (n+0==n) { key=s" "d; c[key]++; b[key]+=n }
      }
      END {
        for (k in c) printf "%8d pkts %10d B  %s\n", c[k], b[k], k
      }' \
    | sort -k4 -n -r | head -20

  echo "--- direction totals (rough) ---"
  sudo tcpdump -nn -r "$pcap" -q 2>/dev/null \
    | awk -v client="$CLIENT" '
        {
          n=$NF; if (n+0!=n) next
          if (index($0, client) == 0) next
          # client appears as src or dst
          if ($3 ~ "^"client"\\.") up+=n
          else if ($5 ~ "^"client"\\.") down+=n
        }
        END {
          printf "client→inet ~ %d B\ninet→client ~ %d B\n", up+0, down+0
          if (down+0 < 20000 && up+0 > down+0) {
            print "HINT: download starved vs upload — classic stuck Short / MSS-QUIC-TCP-over-TCP pattern"
          }
        }'
}

cmd_capture() {
  load_env
  CLIENT="${ARG2:-}"
  PCAP="${PCAP:-/tmp/shorts-stuck.pcap}"
  CAPTURE_SEC="${CAPTURE_SEC:-20}"
  CAPTURE_PKTS="${CAPTURE_PKTS:-120}"

  if [[ -z "$CLIENT" ]]; then
    local json
    json="$(proxy_sessions_json "$TCP_API_PORT")"
    CLIENT="$(echo "$json" | pick_busiest_virt)"
    if [[ -z "$CLIENT" ]]; then
      bad "no CLIENT and API has no virt IPs"
      echo "Usage: $0 capture 10.51.XX.YY"
      echo "Or: curl -s http://127.0.0.1:${TCP_API_PORT}/api/diagnostics/proxy-sessions | python3 -m json.tool"
      return 1
    fi
    echo "AUTO CLIENT=$CLIENT (busiest managementBytesSent via API)"
  else
    echo "CLIENT=$CLIENT"
  fi

  if ! ip link show "$TCP_TUN_DEV" &>/dev/null; then
    bad "no $TCP_TUN_DEV"
    return 1
  fi

  section "CAPTURE ${CAPTURE_SEC}s / ${CAPTURE_PKTS}pkts on $TCP_TUN_DEV host $CLIENT port 443"
  echo "Start this WHILE the Short is frozen."
  sudo timeout "$CAPTURE_SEC" tcpdump -ni "$TCP_TUN_DEV" host "$CLIENT" and port 443 \
    -nn -c "$CAPTURE_PKTS" -w "$PCAP"
  echo "PCAP $PCAP ($(sudo stat -c%s "$PCAP" 2>/dev/null || echo 0) bytes)"
  summarize_pcap "$PCAP"
}

cmd_quic_block() {
  load_env
  section "QUIC BLOCK on $TCP_TUN_DEV (A/B test)"
  # raw PREROUTING survives DCO better than FORWARD alone (hel-1: FORWARD weak, raw counted).
  sudo iptables -t raw -C PREROUTING -i "$TCP_TUN_DEV" -p udp --dport 443 -j DROP 2>/dev/null \
    || sudo iptables -t raw -I PREROUTING -i "$TCP_TUN_DEV" -p udp --dport 443 -j DROP
  sudo iptables -C FORWARD -i "$TCP_TUN_DEV" -p udp --dport 443 -j REJECT --reject-with icmp-port-unreachable 2>/dev/null \
    || sudo iptables -I FORWARD -i "$TCP_TUN_DEV" -p udp --dport 443 -j REJECT --reject-with icmp-port-unreachable
  sudo iptables -t raw -L PREROUTING -n -v --line-numbers | head -6
  sudo iptables -L FORWARD -n -v --line-numbers | head -6
  warn "Force-kill YouTube on the phone, reconnect or wait, retest Shorts."
  warn "When done: $0 quic-unblock"
}

cmd_quic_unblock() {
  load_env
  section "QUIC UNBLOCK — remove test rules"
  local i
  for i in 1 2 3 4 5; do
    sudo iptables -D FORWARD -i "$TCP_TUN_DEV" -p udp --dport 443 -j REJECT --reject-with icmp-port-unreachable 2>/dev/null || break
  done
  for i in 1 2 3 4 5; do
    sudo iptables -t raw -D PREROUTING -i "$TCP_TUN_DEV" -p udp --dport 443 -j DROP 2>/dev/null || break
  done
  if have_quic_rules; then
    bad "rules still present — remove by line number manually"
    sudo iptables -L FORWARD -n -v --line-numbers | head -8
    sudo iptables -t raw -L PREROUTING -n -v --line-numbers | head -8
  else
    ok "no tun-tcp UDP/443 anti-QUIC test rules left"
  fi
  warn "Reconnect VPN on the phone before trusting Shorts results."
}

usage() {
  cat <<'USAGE'
Usage: diagnose-youtube-shorts-tcp.sh [check|capture [virt_ip]|quic-block|quic-unblock]
USAGE
}

case "$CMD" in
  check|"") cmd_check ;;
  capture) cmd_capture ;;
  quic-block) cmd_quic_block ;;
  quic-unblock) cmd_quic_unblock ;;
  -h|--help|help) usage ;;
  *) usage; echo "Unknown command: $CMD" >&2; exit 2 ;;
esac
