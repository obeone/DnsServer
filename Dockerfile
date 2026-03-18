# syntax=docker/dockerfile:1

# ── Stage 1: build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /src

# Clone TechnitiumLibrary (external dependency).
# TechnitiumLibrary must sit alongside DnsServer so relative project
# references in the .csproj files resolve correctly.
RUN git clone --depth 1 https://github.com/TechnitiumSoftware/TechnitiumLibrary.git TechnitiumLibrary

# Copy local DnsServer source from the build context.
# Run `docker build .` from the DnsServer/ directory.
COPY . DnsServer/

# Build TechnitiumLibrary dependencies then publish DnsServer.
# NuGet packages are cached via BuildKit cache mount: even when the git clone
# layer is invalidated (new commits), restored packages are reused from cache.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build TechnitiumLibrary/TechnitiumLibrary.ByteTree/TechnitiumLibrary.ByteTree.csproj -c Release \
 && dotnet build TechnitiumLibrary/TechnitiumLibrary.Net/TechnitiumLibrary.Net.csproj -c Release \
 && dotnet build TechnitiumLibrary/TechnitiumLibrary.Security.OTP/TechnitiumLibrary.Security.OTP.csproj -c Release \
 && dotnet publish DnsServer/DnsServerApp/DnsServerApp.csproj -c Release

# ── Stage 2: runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0

# Configure apt to keep its package cache so BuildKit cache mounts are effective.
RUN rm -f /etc/apt/apt.conf.d/docker-clean \
 && echo 'Binary::apt::APT::Keep-Downloaded-Packages "true";' > /etc/apt/apt.conf.d/keep-cache

# Fetch the Microsoft package repository definition.
# ADD --link caches this layer independently: re-downloading is only triggered
# if the URL content changes, not when other layers are invalidated.
# ADD --link caches this layer independently of other layers.
# Destination is intentionally NOT /tmp: ADD --link creates its own overlay
# layer including the parent directory entry, which would override /tmp's
# sticky permissions (1777 → 0755) and break apt's temp file creation.
ADD --link https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb /packages-microsoft-prod.deb

# Install libmsquic (required for DNS-over-QUIC / HTTP/3) and dnsutils (dig,
# useful for in-container troubleshooting). Apt packages are served from the
# BuildKit cache mount on subsequent builds.
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    dpkg -i /packages-microsoft-prod.deb \
 && rm /packages-microsoft-prod.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends libmsquic dnsutils \
 && apt-get autoremove -y

WORKDIR /opt/technitium/dns

# Copy only the published output from the build stage — the SDK and all
# intermediate build artefacts stay in the builder stage and are not shipped.
COPY --link --from=builder /src/DnsServer/DnsServerApp/bin/Release/publish/ .

# Ensure the config directory exists even without an explicit volume mount.
RUN mkdir -p /etc/dns

# Persist DNS server state (config, zones, logs…) outside the container.
VOLUME ["/etc/dns"]

# Allow the container to shut down gracefully when sent SIGINT (Ctrl-C /
# docker stop), rather than being force-killed after the default timeout.
STOPSIGNAL SIGINT

EXPOSE \
  # Standard DNS
  53/udp 53/tcp \
  # DNS-over-QUIC (UDP) + DNS-over-TLS (TCP)
  853/udp 853/tcp \
  # DNS-over-HTTPS: HTTP/3 (UDP) and HTTP/1.1+2 (TCP)
  443/udp 443/tcp \
  # DNS-over-HTTP (behind a TLS-terminating reverse proxy)
  80/tcp 8053/tcp \
  # Technitium web console + API
  5380/tcp 53443/tcp \
  # DHCP
  67/udp

# https://specs.opencontainers.org/image-spec/annotations/
LABEL org.opencontainers.image.title="Technitium DNS Server" \
      org.opencontainers.image.vendor="Technitium" \
      org.opencontainers.image.source="https://github.com/TechnitiumSoftware/DnsServer" \
      org.opencontainers.image.url="https://technitium.com/dns/" \
      org.opencontainers.image.authors="support@technitium.com"

ENTRYPOINT ["/usr/bin/dotnet", "/opt/technitium/dns/DnsServerApp.dll"]
CMD ["/etc/dns"]
