# ── Stage 1: Build ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# 프로젝트 파일만 먼저 복사해서 restore 레이어 캐시 활용
COPY DotNetty.sln .
COPY GameServer/GameServer.csproj                   GameServer/
COPY GameServer.Protocol/GameServer.Protocol.csproj GameServer.Protocol/
COPY GameServer.Database/GameServer.Database.csproj GameServer.Database/
COPY Common.Server/Common.Server.csproj             Common.Server/
COPY Common.Shared/Common.Shared.csproj             Common.Shared/

RUN dotnet restore GameServer/GameServer.csproj

# 소스 전체 복사 후 publish
COPY GameServer/         GameServer/
COPY GameServer.Protocol/ GameServer.Protocol/
COPY GameServer.Database/ GameServer.Database/
COPY Common.Server/      Common.Server/
COPY Common.Shared/      Common.Shared/

RUN dotnet publish GameServer/GameServer.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 2: Runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 7777
EXPOSE 8080

ENTRYPOINT ["dotnet", "GameServer.dll"]
