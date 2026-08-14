#!/bin/bash
set -e

API_PORT=${API_PORT:-5010}
API_HOST=${API_HOST:-127.0.0.1}
PORT=${PORT:-1194}
PROTO=${PROTO:-udp}
OpenVpnManagement__Port=${OpenVpnManagement__Port:-5092}
DATA_DIR=${DATA_DIR:-/mnt}
DNS1=${DNS1:-8.8.8.8}
DNS2=${DNS2:-8.8.4.4}
VPN_SUBNET=${VPN_SUBNET:-10.51.28.0}
VPN_NETMASK=${VPN_NETMASK:-255.255.255.0}
TUN_IF="${TUN_IF:-tun0}"
WAN_IF="${WAN_IF:-eth0}"
DCO="${DCO:-false}"
# Optional push to clients (e.g. 1200 for WSS/UDP tunnels with reduced effective MTU).
MSSFIX="${MSSFIX:-}"
# Cipher: override with CIPHER / DATA_CIPHERS. DCO needs AEAD (GCM/ChaCha), not CBC.
CIPHER="${CIPHER:-}"
DATA_CIPHERS="${DATA_CIPHERS:-}"

EASYRSA_DIR="$DATA_DIR/easy-rsa"
SCRIPT_SOURCE="/scripts"

# .NET port
if [ -n "$API_PORT" ]; then
  export ASPNETCORE_HTTP_PORTS="$API_PORT"
  echo "[entrypoint] Set ASPNETCORE_HTTP_PORTS to $ASPNETCORE_HTTP_PORTS"
fi

echo "===== STARTING OPENVPN CONTAINER ====="

# NAT/forward
iptables -P FORWARD ACCEPT
iptables -C FORWARD -i "$TUN_IF" -j ACCEPT 2>/dev/null || iptables -A FORWARD -i "$TUN_IF" -j ACCEPT
iptables -C FORWARD -o "$TUN_IF" -j ACCEPT 2>/dev/null || iptables -A FORWARD -o "$TUN_IF" -j ACCEPT
iptables -t nat -C POSTROUTING -s "$VPN_SUBNET/$VPN_NETMASK" -o "$WAN_IF" -j MASQUERADE 2>/dev/null \
  || iptables -t nat -A POSTROUTING -s "$VPN_SUBNET/$VPN_NETMASK" -o "$WAN_IF" -j MASQUERADE

echo "===== Copying OpenVPN hook scripts ====="
mkdir -p /etc/openvpn/scripts
for SCRIPT in client-connect.sh client-disconnect.sh learn-address.sh tls-verify.sh log-watcher.sh; do
  SRC="$SCRIPT_SOURCE/$SCRIPT"; DST="/etc/openvpn/scripts/$SCRIPT"
  if [ -f "$SRC" ]; then
    cp -f "$SRC" "$DST"
    chmod +x "$DST"
    echo "✅ $SCRIPT copied"
  else
    echo "⚠️ $SCRIPT not found in $SCRIPT_SOURCE"
  fi
done

echo "===== Checking contents of $DATA_DIR before starting... ====="
ls -l "$DATA_DIR"

if [ ! -x "$EASYRSA_DIR/easyrsa" ]; then
    echo "Copying Easy-RSA to $EASYRSA_DIR..."
    mkdir -p "$EASYRSA_DIR"
    cp -r /usr/share/easy-rsa/* "$EASYRSA_DIR"
    chmod +x "$EASYRSA_DIR/easyrsa"
fi

if [ ! -f "$EASYRSA_DIR/easyrsa" ] || [ ! -x "$EASYRSA_DIR/easyrsa" ]; then
    echo "ERROR: Easy-RSA script not found or not executable at $EASYRSA_DIR/easyrsa"
    echo "Please ensure Easy-RSA v3 is installed and placed correctly."
    exit 1
fi

if [ ! -d "$EASYRSA_DIR/pki" ]; then
    echo "PKI not found. Initializing..."
    cd "$EASYRSA_DIR"

    export EASYRSA_BATCH=1
    export EASYRSA_REQ_CN="OpenVPN-Server"
    export EASYRSA_PKI="$EASYRSA_DIR/pki"

    ./easyrsa --batch init-pki
    ./easyrsa --batch build-ca nopass
    ./easyrsa --batch gen-req server nopass
    ./easyrsa --batch sign-req server server
    
    unset EASYRSA_REQ_CN
fi

# Ensure ta.key exists
if [ ! -f "$EASYRSA_DIR/pki/ta.key" ]; then
    echo "===== Generating new ta.key (tls-crypt)..."
    openvpn --genkey secret "$EASYRSA_DIR/pki/ta.key"
else
    echo "ta.key already exists."
fi

# Ensure all required files are in /etc/openvpn
echo "===== Copying necessary certs and keys to /etc/openvpn... ====="
declare -A FILES_TO_COPY=(
  ["$EASYRSA_DIR/pki/ca.crt"]="/etc/openvpn/ca.crt"
  ["$EASYRSA_DIR/pki/issued/server.crt"]="/etc/openvpn/server.crt"
  ["$EASYRSA_DIR/pki/private/server.key"]="/etc/openvpn/server.key"
  ["$EASYRSA_DIR/pki/ta.key"]="/etc/openvpn/ta.key"
)

for SRC in "${!FILES_TO_COPY[@]}"; do
  DEST=${FILES_TO_COPY[$SRC]}
  if [ -f "$SRC" ]; then
    cp "$SRC" "$DEST"
  else
    echo "ERROR: Required file $SRC not found, exiting."
    exit 1
  fi
done

# Generate default server.conf if not present
echo "Generating server.conf from environment..."
# DCO=false (default) -> disable-dco in config; DCO=true -> DCO enabled, no disable-dco
# DCO data-path needs AEAD (GCM/ChaCha); CBC stays in userspace and caps throughput.
if [ "$DCO" = "true" ] || [ "$DCO" = "1" ] || [ "$DCO" = "yes" ]; then
  DCO_OPTION=""
  CIPHER="${CIPHER:-AES-128-GCM}"
  DATA_CIPHERS="${DATA_CIPHERS:-AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305}"
else
  DCO_OPTION="disable-dco"
  CIPHER="${CIPHER:-AES-256-CBC}"
  # leave DATA_CIPHERS empty unless explicitly set
fi

if [ -n "$DATA_CIPHERS" ]; then
  CIPHER_LINES="data-ciphers $DATA_CIPHERS
cipher $CIPHER"
else
  CIPHER_LINES="cipher $CIPHER"
fi
echo "[entrypoint] DCO=${DCO:-false} cipher=$CIPHER data-ciphers=${DATA_CIPHERS:-<none>}"

MSSFIX_LINE=""
if [ -n "$MSSFIX" ]; then
  MSSFIX_LINE="push \"mssfix $MSSFIX\""
  echo "[entrypoint] MSSFIX push enabled: $MSSFIX"
fi
cat <<EOF > "$DATA_DIR/server.conf"
port $PORT
proto $PROTO
dev tun
$DCO_OPTION

ca /etc/openvpn/ca.crt
cert /etc/openvpn/server.crt
key /etc/openvpn/server.key
dh none
tls-groups prime256v1

topology subnet
server $VPN_SUBNET $VPN_NETMASK
ifconfig-pool-persist /etc/openvpn/ipp.txt

push "dhcp-option DNS $DNS1"
push "dhcp-option DNS $DNS2"
push "block-outside-dns"
push "redirect-gateway def1"
${MSSFIX_LINE}

keepalive 15 120

remote-cert-tls client
tls-version-min 1.2
tls-crypt /etc/openvpn/ta.key

$CIPHER_LINES
auth SHA256

user nobody
group nogroup

persist-key
persist-tun

crl-verify $EASYRSA_DIR/pki/crl.pem

status $DATA_DIR/openvpn-status.log
status-version 3
log $DATA_DIR/openvpn.log
log-append $DATA_DIR/openvpn.log
syslog

management 127.0.0.1 $OpenVpnManagement__Port

script-security 2
setenv API_PORT $API_PORT
setenv API_HOST $API_HOST
client-connect /etc/openvpn/scripts/client-connect.sh
client-disconnect /etc/openvpn/scripts/client-disconnect.sh
learn-address /etc/openvpn/scripts/learn-address.sh
tls-verify /etc/openvpn/scripts/tls-verify.sh

verb 4
EOF

# Prepare log files
echo "===== Clearing logs... ====="
truncate -s 0 "$DATA_DIR/openvpn.log" || touch "$DATA_DIR/openvpn.log"
truncate -s 0 "$DATA_DIR/openvpn-status.log" || touch "$DATA_DIR/openvpn-status.log"
chmod 777 "$DATA_DIR/openvpn.log" "$DATA_DIR/openvpn-status.log"

# Ensure crl.pem exists
if [ ! -f "$EASYRSA_DIR/pki/crl.pem" ]; then
    echo "Generating crl.pem (empty revocation list)..."
    cd "$EASYRSA_DIR"
    export EASYRSA_PKI="$EASYRSA_DIR/pki"
    ./easyrsa gen-crl
else
    echo "crl.pem already exists."
fi
chmod 644 "$EASYRSA_DIR/pki/crl.pem"

# Add read/execute permissions to everything inside DATA_DIR
echo "===== Setting permissions for $DATA_DIR recursively... ====="
chmod -R a+rX "$DATA_DIR"

echo "===== Fixing permissions to allow OpenVPN (user: nobody) read crl.pem ====="

chmod 644 "$EASYRSA_DIR/pki/crl.pem" || echo "❌ Failed to chmod crl.pem"
chmod o+rx "$DATA_DIR" || echo "❌ Failed to chmod $DATA_DIR"
chmod o+rx "$EASYRSA_DIR" || echo "❌ Failed to chmod $EASYRSA_DIR"
chmod o+rx "$EASYRSA_DIR/pki" || echo "❌ Failed to chmod $EASYRSA_DIR/pki"

echo "===== Preparing Pi-hole state files in $DATA_DIR... ====="
# Cursor watermark (same pattern as openvpn.log — pre-create so volume mounts are writable/readable).
touch "$DATA_DIR/pihole-query-cursor.txt"
chmod 644 "$DATA_DIR/pihole-query-cursor.txt"

# Runtime config is created by the .NET app on dashboard Save & apply; do not touch an empty JSON file.
# Re-apply 600 after chmod -R above (recursive pass would widen an existing secrets file to world-readable).
if [ -f "$DATA_DIR/pihole-runtime-config.json" ]; then
  chmod 600 "$DATA_DIR/pihole-runtime-config.json"
  echo "✅ pihole-runtime-config.json permissions set to 600"
else
  echo "ℹ️ pihole-runtime-config.json not yet present (created on dashboard Save & apply)"
fi

echo "✅ crl.pem permission fix complete"

echo "===== FINAL CHECK BEFORE STARTING OPENVPN ====="
ls -l "$DATA_DIR"
[ -d "$EASYRSA_DIR/pki" ] && ls -l "$EASYRSA_DIR/pki"

echo "===== server.conf contents ====="
cat "$DATA_DIR/server.conf" || echo "server.conf not found!"

echo "===== Starting OpenVPN in background..."
openvpn --config "$DATA_DIR/server.conf" --writepid "$DATA_DIR/openvpn.pid" &
OPENVPN_PID=$!
echo "[entrypoint] OpenVPN pid=$OPENVPN_PID (also in $DATA_DIR/openvpn.pid)"

# 👇 Stream OpenVPN logs to Docker stdout (suppress raw tls-crypt probe noise; enrichment service re-logs with origin tags)
echo "===== Attaching OpenVPN log to stdout... ====="
tail -F "$DATA_DIR/openvpn.log" | grep -viE 'TLS Error: tls-crypt|tls-crypt unwrap|tls-crypt unwrapp|packet authentication failed' &
TAIL_PID=$!

echo "[entrypoint] Starting .NET application..."

# Wait for .NET files
echo "⏳ Waiting for DataGateOpenVpnManager.dll and dependencies to appear..."
timeout=10
elapsed=0

while [ ! -f /app/DataGateOpenVpnManager.dll ] || [ ! -f /app/DataGateOpenVpnManager.runtimeconfig.json ]; do
    if [ "$elapsed" -ge "$timeout" ]; then
        echo "❌ ERROR: .NET files not found after ${timeout}s, exiting"
        echo "🔍 /app contents:"
        ls -la /app
        exit 1
    fi
    echo "  ...still waiting (${elapsed}s)"
    sleep 1
    elapsed=$((elapsed + 1))
done

runuser -u nobody -- cat "$EASYRSA_DIR/pki/crl.pem" >/dev/null \
  && echo "✅ nobody can read crl.pem" \
  || echo "❌ nobody CANNOT read crl.pem"

echo "✅ Found required .NET files"
cd /app

dotnet DataGateOpenVpnManager.dll &
DOTNET_PID=$!

# Keep container alive on the .NET manager. OpenVPN may be killed/restarted via
# POST /api/openvpn/{start|restart|kill} without exiting the container.
wait $DOTNET_PID
DOTNET_EXIT_CODE=$?

# Stop OpenVPN (current pid file or original entrypoint pid) when manager exits.
if [ -f "$DATA_DIR/openvpn.pid" ]; then
  OPENVPN_CURRENT_PID="$(tr -d '[:space:]' < "$DATA_DIR/openvpn.pid" || true)"
  if [ -n "$OPENVPN_CURRENT_PID" ] && kill -0 "$OPENVPN_CURRENT_PID" 2>/dev/null; then
    echo "[entrypoint] Stopping OpenVPN pid=$OPENVPN_CURRENT_PID after manager exit"
    kill "$OPENVPN_CURRENT_PID" 2>/dev/null || true
  fi
elif kill -0 "$OPENVPN_PID" 2>/dev/null; then
  echo "[entrypoint] Stopping original OpenVPN pid=$OPENVPN_PID after manager exit"
  kill "$OPENVPN_PID" 2>/dev/null || true
fi

# Kill tail (optional, for clean shutdown)
kill $TAIL_PID 2>/dev/null || true

if [ $DOTNET_EXIT_CODE -ne 0 ]; then
  echo ".NET app exited with code $DOTNET_EXIT_CODE"
  exit $DOTNET_EXIT_CODE
fi

exit 0