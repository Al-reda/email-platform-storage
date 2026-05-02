# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY shared/Shared.csproj                  shared/
COPY storage/Storage.Api.csproj            storage/
RUN dotnet restore storage/Storage.Api.csproj

COPY shared/          shared/
COPY storage/         storage/

RUN dotnet publish storage/Storage.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Storage.Api.dll"]
