#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="karma-mapbox-proxy"
SERVICE_USER="karma-proxy"
INSTALL_DIR="/opt/karma-mapbox-proxy"
INSTALL_BIN="${INSTALL_DIR}/karma-mapbox-proxy"
LISTEN_ADDR="${KARMA_MAPBOX_PROXY_ADDR:-0.0.0.0:443}"

BINARY_PATH="${1:-./karma-mapbox-proxy-linux-amd64}"
DUCKDNS_DOMAIN="${DUCKDNS_DOMAIN:-}"
DUCKDNS_TOKEN="${DUCKDNS_TOKEN:-}"

usage() {
    cat <<'EOF'
Usage:
  sudo ./install-ubuntu-karma-proxy.sh [path-to-karma-mapbox-proxy-linux-amd64]

Optional DuckDNS setup:
  sudo DUCKDNS_DOMAIN=karmakontroller DUCKDNS_TOKEN=your-token ./install-ubuntu-karma-proxy.sh ./karma-mapbox-proxy-linux-amd64

Optional listen address:
  sudo KARMA_MAPBOX_PROXY_ADDR=0.0.0.0:443 ./install-ubuntu-karma-proxy.sh ./karma-mapbox-proxy-linux-amd64
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this with sudo." >&2
    usage >&2
    exit 1
fi

if [[ ! -f "${BINARY_PATH}" ]]; then
    echo "Proxy binary not found: ${BINARY_PATH}" >&2
    exit 1
fi

echo "Installing ${SERVICE_NAME} from ${BINARY_PATH}"

install -d -m 0755 "${INSTALL_DIR}"
install -m 0755 "${BINARY_PATH}" "${INSTALL_BIN}"

if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin --user-group "${SERVICE_USER}"
fi

cat >"/etc/systemd/system/${SERVICE_NAME}.service" <<EOF
[Unit]
Description=KarmaKontroller public Mapbox proxy
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_USER}
Environment=KARMA_MAPBOX_PROXY_ADDR=${LISTEN_ADDR}
ExecStart=${INSTALL_BIN}
Restart=always
RestartSec=5

AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=full

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}"

if command -v ufw >/dev/null 2>&1; then
    ufw allow 443/tcp >/dev/null || true
fi

if [[ -n "${DUCKDNS_DOMAIN}" || -n "${DUCKDNS_TOKEN}" ]]; then
    if [[ -z "${DUCKDNS_DOMAIN}" || -z "${DUCKDNS_TOKEN}" ]]; then
        echo "DuckDNS skipped: set both DUCKDNS_DOMAIN and DUCKDNS_TOKEN." >&2
    else
        echo "Installing DuckDNS updater for ${DUCKDNS_DOMAIN}.duckdns.org"
        install -d -m 0700 /opt/duckdns
        cat >/opt/duckdns/update.sh <<EOF
#!/bin/sh
DOMAIN="${DUCKDNS_DOMAIN}"
TOKEN="${DUCKDNS_TOKEN}"

curl -fsS "https://www.duckdns.org/update?domains=\${DOMAIN}&token=\${TOKEN}&ip=" -o /opt/duckdns/duck.log
EOF
        chmod 0700 /opt/duckdns/update.sh
        /opt/duckdns/update.sh || true
        if [[ -f /opt/duckdns/duck.log ]]; then
            echo "DuckDNS update result: $(cat /opt/duckdns/duck.log)"
        fi

        tmp_cron="$(mktemp)"
        crontab -l 2>/dev/null | grep -v '/opt/duckdns/update.sh' >"${tmp_cron}" || true
        echo '*/5 * * * * /opt/duckdns/update.sh >/dev/null 2>&1' >>"${tmp_cron}"
        crontab "${tmp_cron}"
        rm -f "${tmp_cron}"
    fi
fi

echo
echo "Service status:"
systemctl --no-pager --full status "${SERVICE_NAME}" || true

echo
echo "Port 443 listener:"
ss -ltnp 'sport = :443' || true

echo
echo "Local proxy test:"
if curl -k --resolve api.mapbox.com:443:127.0.0.1 --max-time 10 https://api.mapbox.com/; then
    echo
    echo "Install complete."
else
    echo
    echo "Install finished, but local curl test failed. Check logs with:" >&2
    echo "  journalctl -u ${SERVICE_NAME} -f" >&2
    exit 1
fi
