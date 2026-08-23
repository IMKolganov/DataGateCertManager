# Fast OpenVPN-over-WebSocket UDP proxy (Rust) for A/B vs DataGateOpenVpnManager.
# Wire-compatible: GET /api/proxy?mode=udp  + binary frames [u16_be len][payload]...
FROM rust:1-bookworm AS build
WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY src ./src
RUN cargo test --release && cargo build --release

FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/target/release/wss-udp-proxy /usr/local/bin/wss-udp-proxy
ENV LISTEN=0.0.0.0:5013
ENV VPN_HOST=127.0.0.1
ENV PORT=1194
EXPOSE 5013
ENTRYPOINT ["/usr/local/bin/wss-udp-proxy"]
