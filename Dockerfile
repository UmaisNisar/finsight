# syntax=docker/dockerfile:1

# FinSight: the ASP.NET Core API serving the built React app, in one image.
#
#   docker build -t finsight .
#   docker compose up          (app + PostgreSQL, see docker-compose.yml)
#
# Base images are pinned by tag and digest; Dependabot (docker ecosystem) proposes updates. The build stages run on the
# build machine's architecture and cross-publish for the target one, so multi-arch builds need no emulation.

# ---- Web app: static files only, identical on every architecture ----------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim@sha256:2fe369e969550cde8e867afc3fe370b260140cab4a23d467074295b42163d553 AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.ts writes to ../server/FinSight.Api/wwwroot
RUN npm run build

# ---- API: restore (cached until a project file changes), then publish -------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY server/FinSight.Core/FinSight.Core.csproj server/FinSight.Core/
COPY server/FinSight.Infrastructure/FinSight.Infrastructure.csproj server/FinSight.Infrastructure/
COPY server/FinSight.Migrations.Postgres/FinSight.Migrations.Postgres.csproj server/FinSight.Migrations.Postgres/
COPY server/FinSight.Api/FinSight.Api.csproj server/FinSight.Api/
RUN --mount=type=cache,id=nuget-$TARGETARCH,target=/root/.nuget/packages \
    dotnet restore server/FinSight.Api/FinSight.Api.csproj -a $TARGETARCH

COPY server/ server/
# Release build with the repository's analyzers and TreatWarningsAsErrors (Directory.Build.props) intact.
RUN --mount=type=cache,id=nuget-$TARGETARCH,target=/root/.nuget/packages \
    dotnet publish server/FinSight.Api/FinSight.Api.csproj \
      --configuration Release --arch $TARGETARCH --self-contained false --no-restore \
      --output /app -p:UseAppHost=false -p:DebugType=none
COPY --from=web /src/server/FinSight.Api/wwwroot /app/wwwroot
# Writable data directory for the Data Protection key ring (and a SQLite file, if used); a volume is mounted here.
RUN mkdir -p /data/keys

# ---- Runtime: chiseled Ubuntu, no shell or package manager, runs as a non-root user -------------------------------------
# The "extra" variant adds ICU and time zone data, so culture-aware formatting and time zones behave as they do in development.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:6385dc0eaef704fad88d3f65c334e791a371bbe448f52ca39d83d2df49251e28 AS final

LABEL org.opencontainers.image.title="FinSight" \
      org.opencontainers.image.description="AI personal finance analyzer: bank statements in, explained spending out." \
      org.opencontainers.image.source="https://github.com/UmaisNisar/finsight"

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Logging__Console__FormatterName=json \
    Logging__Console__FormatterOptions__UseUtcTimestamp=true \
    Logging__Console__FormatterOptions__TimestampFormat="yyyy-MM-ddTHH:mm:ss.fffZ" \
    DataProtection__KeysPath=/data/keys

# 1654 is APP_UID, the non-root "app" user defined by the .NET base images.
COPY --from=build --chown=1654:1654 /data /data
COPY --from=build /app ./

USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]

# No HEALTHCHECK: the chiseled image has no shell or curl to run one. Orchestrators probe /health/live and /health/ready.
ENTRYPOINT ["dotnet", "FinSight.Api.dll"]
