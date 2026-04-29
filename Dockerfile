# syntax=docker/dockerfile:1.7

# =========================================================
# Storage Service Dockerfile
# =========================================================
# Multi-stage build: SDK builds, runtime runs.
# Built from the repo root (context=.) so we can access Shared.
# Factor 5 (Build/release/run): image is the immutable build artifact.
# Factor 10 (Dev/prod parity): same image on dev laptop and Fargate.
# =========================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only csproj files first to maximise Docker layer caching.
# Restore won't re-run unless a csproj changes.
COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj                  src/Shared/
COPY src/Storage.Api/Storage.Api.csproj        src/Storage.Api/
RUN dotnet restore src/Storage.Api/Storage.Api.csproj

# Now copy the actual source.
COPY src/Shared/          src/Shared/
COPY src/Storage.Api/     src/Storage.Api/

RUN dotnet publish src/Storage.Api/Storage.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# -----------------------------------------------------------------------------
# Runtime stage — ASP.NET Core (HTTP services need it; Email.Worker does not).
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl is used by docker-compose healthcheck.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Non-root (the .NET 8 image ships an `app` user; $APP_UID is pre-set).
USER $APP_UID

# .NET 8 aspnet images listen on :8080 by default.
EXPOSE 8080

ENTRYPOINT ["dotnet", "EmailPlatform.Storage.Api.dll"]
