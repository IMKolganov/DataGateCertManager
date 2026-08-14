# ==========================
# Stage 1: Build OpenVPN from source (override: --build-arg OPENVPN_VERSION=2.7.6)
# ==========================
FROM debian:12-slim AS openvpn-build

ARG OPENVPN_VERSION=2.7.6

RUN apt-get update && \
    apt-get install -y \
        build-essential \
        libssl-dev \
        liblz4-dev \
        liblzo2-dev \
        libpam0g-dev \
        libpkcs11-helper1-dev \
        libnl-3-dev \
        libnl-genl-3-dev \
        libcap-ng-dev \
        pkg-config \
        wget && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /tmp

RUN wget -O openvpn.tar.gz "https://swupdate.openvpn.net/community/releases/openvpn-${OPENVPN_VERSION}.tar.gz" && \
    tar xzf openvpn.tar.gz && \
    cd "openvpn-${OPENVPN_VERSION}" && \
    ./configure --disable-debug --disable-dependency-tracking && \
    make -j"$(nproc)" && \
    make install && \
    strip /usr/local/sbin/openvpn
# ==========================
# Stage 2: Build .NET app (.NET 10)
# ==========================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

LABEL maintainer="Ivan Kolganov with ❤️ via Kyle Manna's template"

WORKDIR /src

COPY ["DataGateOpenVpnManager/DataGateOpenVpnManager.csproj", "DataGateOpenVpnManager/"]
WORKDIR /src/DataGateOpenVpnManager
RUN dotnet restore "DataGateOpenVpnManager.csproj"

WORKDIR /src
COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN echo "Using build configuration: $BUILD_CONFIGURATION" && \
    dotnet publish "DataGateOpenVpnManager/DataGateOpenVpnManager.csproj" \
      -c $BUILD_CONFIGURATION \
      -o /app/publish


# ==========================
# Stage 3: Final runtime image (.NET 10 + OpenVPN)
# ==========================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

USER root

RUN apt-get update && \
    apt-get install -y \
        curl \
        nano \
        iptables \
        iproute2 \
        easy-rsa \
        liblz4-1 \
        liblzo2-2 \
        libpkcs11-helper1 \
        libnl-3-200 \
        libnl-genl-3-200 \
        libcap-ng0 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=openvpn-build /usr/local/sbin/openvpn /usr/local/sbin/openvpn

WORKDIR /app

COPY --from=publish /app/publish .

COPY scripts /scripts
COPY entrypoint.sh /entrypoint.sh

RUN sed -i 's/\r$//' /entrypoint.sh && \
    find /scripts -name '*.sh' -exec sed -i 's/\r$//' {} + && \
    chmod +x /entrypoint.sh && chmod +x /scripts/*.sh

ENTRYPOINT ["/entrypoint.sh"]
