FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
ARG DUMPLINGS_COMMIT=unknown
RUN dotnet publish Dumplings.Cli -c Release -o /out -v q \
    && printf '%s\n' "$DUMPLINGS_COMMIT" > /out/COMMIT

FROM mcr.microsoft.com/dotnet/runtime:8.0
ARG S5CMD_VERSION=2.3.0
ARG TARGETARCH
RUN set -eux; \
    apt-get update; \
    apt-get install --yes --no-install-recommends ca-certificates curl jq; \
    rm -rf /var/lib/apt/lists/*; \
    case "${TARGETARCH:-$(uname -m)}" in \
      amd64|x86_64) s5arch=64bit ;; \
      arm64|aarch64) s5arch=ARM64 ;; \
      *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/s5cmd.tar.gz \
      "https://github.com/peak/s5cmd/releases/download/v${S5CMD_VERSION}/s5cmd_${S5CMD_VERSION}_Linux-${s5arch}.tar.gz"; \
    tar -xzf /tmp/s5cmd.tar.gz -C /usr/local/bin s5cmd; \
    rm /tmp/s5cmd.tar.gz
COPY --from=build /out /app
COPY docker-entrypoint.sh /usr/local/bin/dumplings-service
RUN chmod 0755 /usr/local/bin/dumplings-service && mkdir -m 1777 /data
WORKDIR /data
ENTRYPOINT ["dumplings-service"]
