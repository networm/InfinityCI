---
title: Deployment
description: Run from source, a single-file EXE, or Docker
sidebar_position: 9
---

# Deployment

Infinity CI offers three deployment options — pick per scenario.

## Option 1: run from source (dev / trial)

```cmd
scripts\start-server.cmd          :: Server (web http://127.0.0.1:5000, gRPC 5001)
scripts\start-agent.cmd           :: Agent (issue an enroll token from the web first)
```

On bash, use the matching `.sh` scripts.

## Option 2: single-file EXE (recommended for Windows build machines)

```cmd
scripts\publish.cmd
```

Publishes a win-x64 **self-contained single-file EXE** into `publish\`. Build machines need no .NET runtime — copy one EXE and run. Ideal for distributing across multiple Windows build machines.

## Option 3: Docker

```bash
docker compose up -d
```

- Multi-stage build: node:22-alpine builds the frontend → dotnet/sdk:10 publishes the backend → aspnet:10 runtime (with git / tzdata / libldap);
- Ports: 5000 (Web/SignalR), 5001 (Agent gRPC);
- Volume: `infinityci-data` mounted at `/app/data`.

Useful environment variables:

| Variable | Purpose |
| --- | --- |
| `TZ` | Timezone for cron triggers (e.g. `Asia/Shanghai`) |
| `PublicOrigin` | Public origin behind a reverse proxy / TLS terminator |
| SMTP variables | Enable the email notification channel |

## Data & backups

All state (the SQLite database, job config repos, logs, workspaces) lives under `data/`:

```text
data/
├── infinityci.db      # SQLite
└── {job}/            # config repo + logs + workspaces
```

**Backup = copying the `data/` directory** (the data volume, when deployed via Docker). Migrating to a new machine = copy the directory and start — there is no hidden state.

## Reverse proxy

The server is a standard ASP.NET Core Kestrel app and can sit behind Nginx / Caddy. Notes:

- WebSockets (SignalR) require the proxy to forward `Upgrade` headers;
- Set `PublicOrigin` to the externally visible origin.
