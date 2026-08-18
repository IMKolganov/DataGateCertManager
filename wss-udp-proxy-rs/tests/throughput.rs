//! Loopback proxy throughput. Not a unit test of correctness — measures Mbps.
//!
//! ```bash
//! cargo test --release throughput_loopback -- --ignored --nocapture
//! ```
//!
//! Compares poorly to production WSS (no nginx TLS, no OpenVPN crypto, localhost only).
//! Useful to catch datapath regressions and ballpark Rust pump capacity.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use futures_util::{SinkExt, StreamExt};
use tokio::net::TcpListener;
use tokio::net::UdpSocket;
use tokio_tungstenite::{connect_async, tungstenite::Message};

/// Start a connected-friendly UDP echo on 127.0.0.1:0.
async fn spawn_udp_echo() -> (SocketAddr, tokio::task::JoinHandle<()>) {
    let sock = UdpSocket::bind("127.0.0.1:0").await.expect("echo bind");
    let addr = sock.local_addr().unwrap();
    let handle = tokio::spawn(async move {
        let mut buf = vec![0u8; 65535];
        loop {
            match sock.recv_from(&mut buf).await {
                Ok((n, peer)) => {
                    let _ = sock.send_to(&buf[..n], peer).await;
                }
                Err(_) => break,
            }
        }
    });
    (addr, handle)
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "perf: cargo test --release throughput_loopback -- --ignored --nocapture"]
async fn throughput_loopback_roundtrip_mbps() {
    let (vpn_addr, echo_task) = spawn_udp_echo().await;

    // Run the same binary the image ships — avoids duplicating pump logic in tests.
    let listen = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let proxy_addr = listen.local_addr().unwrap();
    drop(listen);

    let bin = env!("CARGO_BIN_EXE_wss-udp-proxy");
    let mut child = tokio::process::Command::new(bin)
        .env("LISTEN", proxy_addr.to_string())
        .env("VPN_HOST", "127.0.0.1")
        .env("PORT", vpn_addr.port().to_string())
        .env("RUST_LOG", "error")
        .kill_on_drop(true)
        .spawn()
        .expect("spawn proxy");

    // Wait until healthz is up.
    let health = format!("http://{proxy_addr}/healthz");
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if Instant::now() > deadline {
            panic!("proxy did not become ready");
        }
        if let Ok(body) = reqwest_get_health(&health).await {
            if body == "ok" {
                break;
            }
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    let ws_url = format!("ws://{proxy_addr}/api/proxy?mode=udp");
    let (mut ws, _) = connect_async(&ws_url).await.expect("ws connect");

    let payload_len = 1200usize;
    let frame_len = 2 + payload_len;
    let mut frame = vec![0u8; frame_len];
    frame[0] = (payload_len >> 8) as u8;
    frame[1] = (payload_len & 0xff) as u8;
    for b in &mut frame[2..] {
        *b = 0xA5;
    }

    let run_for = Duration::from_secs(3);
    let t0 = Instant::now();
    let mut sent = 0u64;
    let mut recv = 0u64;

    // Pipeline a few in-flight messages; drain as we go.
    let inflight_max = 64usize;
    let mut inflight = 0usize;

    while t0.elapsed() < run_for {
        while inflight < inflight_max && t0.elapsed() < run_for {
            ws.send(Message::Binary(frame.clone().into()))
                .await
                .expect("send");
            sent += payload_len as u64;
            inflight += 1;
        }

        match tokio::time::timeout(Duration::from_millis(50), ws.next()).await {
            Ok(Some(Ok(Message::Binary(data)))) => {
                let mut off = 0;
                while off + 2 <= data.len() {
                    let len = ((data[off] as usize) << 8) | (data[off + 1] as usize);
                    off += 2;
                    if off + len > data.len() {
                        break;
                    }
                    recv += len as u64;
                    off += len;
                    if inflight > 0 {
                        inflight -= 1;
                    }
                }
            }
            Ok(Some(Ok(_))) => {}
            Ok(Some(Err(e))) => panic!("ws error: {e}"),
            Ok(None) => break,
            Err(_) => {}
        }
    }

    // Drain remaining briefly.
    let drain_until = Instant::now() + Duration::from_millis(500);
    while inflight > 0 && Instant::now() < drain_until {
        if let Ok(Some(Ok(Message::Binary(data)))) =
            tokio::time::timeout(Duration::from_millis(50), ws.next()).await
        {
            let mut off = 0;
            while off + 2 <= data.len() {
                let len = ((data[off] as usize) << 8) | (data[off + 1] as usize);
                off += 2;
                if off + len > data.len() {
                    break;
                }
                recv += len as u64;
                off += len;
                if inflight > 0 {
                    inflight -= 1;
                }
            }
        } else {
            break;
        }
    }

    let secs = t0.elapsed().as_secs_f64().max(1e-9);
    let send_mbps = (sent as f64) * 8.0 / secs / 1_000_000.0;
    let recv_mbps = (recv as f64) * 8.0 / secs / 1_000_000.0;
    eprintln!(
        "loopback roundtrip: send={send_mbps:.0} Mbps recv={recv_mbps:.0} Mbps \
         (payload {payload_len}B, {secs:.2}s, sent={sent} recv={recv})"
    );

    let _ = child.kill().await;
    echo_task.abort();

    // Localhost pump should crush the ~140 Mbps WSS production ceiling.
    // Keep a conservative floor so CI VMs still pass.
    assert!(
        recv_mbps > 200.0,
        "recv {recv_mbps:.0} Mbps below floor — proxy datapath regression?"
    );
}

async fn reqwest_get_health(url: &str) -> Result<String, ()> {
    // Avoid reqwest dep: tiny TCP HTTP/1.0 GET.
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let addr: SocketAddr = url
        .trim_start_matches("http://")
        .split('/')
        .next()
        .unwrap()
        .parse()
        .map_err(|_| ())?;
    let mut stream = tokio::net::TcpStream::connect(addr).await.map_err(|_| ())?;
    stream
        .write_all(b"GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n")
        .await
        .map_err(|_| ())?;
    let mut buf = vec![0u8; 1024];
    let n = stream.read(&mut buf).await.map_err(|_| ())?;
    let text = String::from_utf8_lossy(&buf[..n]);
    let body = text.split("\r\n\r\n").nth(1).unwrap_or("").trim();
    Ok(body.to_string())
}
