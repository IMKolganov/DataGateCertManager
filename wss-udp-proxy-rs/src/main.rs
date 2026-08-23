mod framing;

use std::env;
use std::net::SocketAddr;
use std::sync::Arc;

use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::Query;
use axum::response::IntoResponse;
use axum::routing::get;
use axum::Router;
use bytes::{Bytes, BytesMut};
use framing::{can_append, try_parse_next, write_frame, BATCH_CAPACITY, BATCH_TARGET, MAX_PAYLOAD};
use futures_util::{SinkExt, StreamExt};
use tokio::net::{TcpListener, UdpSocket};
use tokio::sync::mpsc;
use tracing::{info, warn};

#[derive(Clone)]
struct Config {
    listen: SocketAddr,
    vpn_host: String,
    vpn_port: u16,
    udp_buf_bytes: usize,
}

#[derive(Debug, serde::Deserialize)]
struct ProxyQuery {
    #[serde(default = "default_mode")]
    mode: String,
}

fn default_mode() -> String {
    "tcp".into()
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "wss_udp_proxy=info".into()),
        )
        .init();

    let cfg = Arc::new(Config {
        listen: env::var("LISTEN")
            .unwrap_or_else(|_| "0.0.0.0:5013".into())
            .parse()
            .expect("LISTEN"),
        vpn_host: env::var("VPN_HOST").unwrap_or_else(|_| "127.0.0.1".into()),
        vpn_port: env::var("VPN_PORT")
            .or_else(|_| env::var("PORT"))
            .unwrap_or_else(|_| "1194".into())
            .parse()
            .expect("VPN_PORT"),
        udp_buf_bytes: env::var("UDP_BUF_BYTES")
            .unwrap_or_else(|_| "4194304".into())
            .parse()
            .unwrap_or(4 * 1024 * 1024),
    });

    let app = Router::new()
        .route("/api/proxy", get(ws_upgrade))
        .route("/healthz", get(|| async { "ok" }))
        .route("/version", get(version))
        .with_state(cfg.clone());

    let listener = TcpListener::bind(cfg.listen).await.expect("bind");
    info!(
        version = env!("CARGO_PKG_VERSION"),
        listen = %cfg.listen,
        vpn = %format!("{}:{}", cfg.vpn_host, cfg.vpn_port),
        "wss-udp-proxy listening"
    );
    axum::serve(listener, app).await.expect("serve");
}

async fn version() -> String {
    format!(
        "wss-udp-proxy {} (udp-only)\n",
        env!("CARGO_PKG_VERSION")
    )
}

async fn ws_upgrade(
    ws: WebSocketUpgrade,
    Query(q): Query<ProxyQuery>,
    axum::extract::State(cfg): axum::extract::State<Arc<Config>>,
) -> impl IntoResponse {
    let mode = q.mode.trim().to_ascii_lowercase();
    if mode != "udp" {
        return (
            axum::http::StatusCode::BAD_REQUEST,
            "only mode=udp is supported by this test proxy",
        )
            .into_response();
    }
    ws.on_upgrade(move |socket| handle_udp(socket, cfg))
}

fn new_batch() -> BytesMut {
    let mut b = BytesMut::with_capacity(BATCH_CAPACITY);
    b.resize(BATCH_CAPACITY, 0);
    b
}

async fn handle_udp(socket: WebSocket, cfg: Arc<Config>) {
    let vpn: SocketAddr = format!("{}:{}", cfg.vpn_host, cfg.vpn_port)
        .parse()
        .expect("vpn addr");

    let udp = match UdpSocket::bind("127.0.0.1:0").await {
        Ok(s) => s,
        Err(e) => {
            warn!(error = %e, "udp bind failed");
            return;
        }
    };
    if let Ok(std_sock) = udp.into_std() {
        let sock = socket2::Socket::from(std_sock);
        let _ = sock.set_recv_buffer_size(cfg.udp_buf_bytes);
        let _ = sock.set_send_buffer_size(cfg.udp_buf_bytes);
        let std_sock: std::net::UdpSocket = sock.into();
        std_sock.set_nonblocking(true).ok();
        match UdpSocket::from_std(std_sock) {
            Ok(s) => {
                if let Err(e) = s.connect(vpn).await {
                    warn!(error = %e, %vpn, "udp connect failed");
                    return;
                }
                run_proxy(socket, s, vpn).await;
            }
            Err(e) => warn!(error = %e, "udp from_std failed"),
        }
    }
}

async fn run_proxy(socket: WebSocket, udp: UdpSocket, vpn: SocketAddr) {
    info!(local = ?udp.local_addr().ok(), %vpn, "UDP proxy started");

    let (mut ws_tx, mut ws_rx) = socket.split();
    let (batch_tx, mut batch_rx) = mpsc::channel::<Bytes>(4);

    let udp_recv = Arc::new(udp);
    let udp_send = udp_recv.clone();

    let producer = {
        let udp = udp_recv.clone();
        async move {
            let mut scratch = vec![0u8; MAX_PAYLOAD];
            loop {
                let n = match udp.recv(&mut scratch).await {
                    Ok(n) => n,
                    Err(e) => {
                        warn!(error = %e, "udp recv");
                        break;
                    }
                };
                if n == 0 || n > MAX_PAYLOAD {
                    continue;
                }

                let mut batch = new_batch();
                let mut offset = 0usize;
                match write_frame(&mut batch[offset..], &scratch[..n]) {
                    Some(w) => offset += w,
                    None => continue,
                }

                loop {
                    match udp.try_recv(&mut scratch) {
                        Ok(m) if (1..=MAX_PAYLOAD).contains(&m) => {
                            if !can_append(offset, m, BATCH_CAPACITY, BATCH_TARGET) {
                                batch.truncate(offset);
                                if batch_tx.send(batch.freeze()).await.is_err() {
                                    return;
                                }
                                batch = new_batch();
                                offset = 0;
                            }
                            match write_frame(&mut batch[offset..], &scratch[..m]) {
                                Some(w) => {
                                    offset += w;
                                    if offset >= BATCH_TARGET {
                                        break;
                                    }
                                }
                                None => break,
                            }
                        }
                        Ok(_) => {}
                        Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                        Err(e) => {
                            warn!(error = %e, "udp try_recv");
                            return;
                        }
                    }
                }

                batch.truncate(offset);
                if !batch.is_empty() && batch_tx.send(batch.freeze()).await.is_err() {
                    break;
                }
            }
        }
    };

    let sender = async move {
        while let Some(batch) = batch_rx.recv().await {
            if ws_tx.send(Message::Binary(batch)).await.is_err() {
                break;
            }
        }
    };

    let ws_to_udp = async move {
        while let Some(Ok(msg)) = ws_rx.next().await {
            match msg {
                Message::Binary(data) => {
                    let mut off = 0;
                    while let Some((next, frame)) = try_parse_next(&data, off) {
                        if udp_send.send(frame).await.is_err() {
                            return;
                        }
                        off = next;
                    }
                }
                Message::Close(_) => break,
                _ => {}
            }
        }
    };

    tokio::select! {
        _ = producer => {},
        _ = sender => {},
        _ = ws_to_udp => {},
    }
    info!("UDP proxy session closed");
}
