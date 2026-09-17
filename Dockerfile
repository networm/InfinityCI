# Infinity CI server image.
# Multi-stage: build the web SPA, publish the server, then assemble a lean
# runtime with the git CLI (SCM checkout) and tzdata (cron schedules).

# -- stage 1: web SPA --
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# -- stage 2: server publish --
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY global.json ./
COPY src/InfinityCI.Core/Protos/ src/InfinityCI.Core/Protos/
COPY src/InfinityCI.Core/*.csproj src/InfinityCI.Core/
COPY src/InfinityCI.Server/*.csproj src/InfinityCI.Server/
RUN dotnet restore src/InfinityCI.Server/InfinityCI.Server.csproj
COPY src/InfinityCI.Core/ src/InfinityCI.Core/
COPY src/InfinityCI.Server/ src/InfinityCI.Server/
RUN dotnet publish src/InfinityCI.Server -c Release -o /app/publish

# -- stage 3: runtime --
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends git tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=server /app/publish .
COPY --from=web /src/web/dist ./wwwroot

# All persistent state (SQLite, workflow config repos, logs, workspaces)
# lives under /app/data — mount a volume there.
ENV InfinityCI__DataDir=/app/data \
    InfinityCI__WebDistDir=wwwroot \
    InfinityCI__ListenHost=0.0.0.0
EXPOSE 5000 5001

RUN useradd -m ci && mkdir -p /app/data && chown -R ci:ci /app
USER ci
ENTRYPOINT ["dotnet", "InfinityCI.Server.dll"]
