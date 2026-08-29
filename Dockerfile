# The NUC is x86_64; build with --platform linux/amd64 when publishing from an Apple Silicon Mac.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/PoolSync/PoolSync.csproj src/PoolSync/
RUN dotnet restore src/PoolSync/PoolSync.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/PoolSync/PoolSync.csproj -a $TARGETARCH -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here so the container healthcheck has something to call.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# A fresh named volume inherits this ownership, so state.json stays writable as the non-root user.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data

USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data

HEALTHCHECK --interval=60s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "PoolSync.dll"]
