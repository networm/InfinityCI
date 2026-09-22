---
title: Quick Start
description: Two commands to boot Infinity CI and create your first workflow
sidebar_position: 2
---

# Quick Start

## Requirements

| Dependency | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.401 | Pinned via `global.json` (rollForward=latestFeature) |
| Node.js | ≥ 20 | Only for frontend development |
| npm | ≥ 10 | Only for frontend development |
| Git | any recent version | Required by config repos / SCM checkout |

## Start the Server

```cmd
scripts\start-server.cmd
```

- Web UI: http://127.0.0.1:5000
- Agent gRPC: 5001
- First start automatically creates the super admin **admin / admin** and a Default project (change the password immediately)

## Start an Agent

Issue a one-time enroll token from the **Agents page** in the web UI, then run on your build machine:

```cmd
scripts\start-agent.cmd --Agent:EnrollToken=<token> --Agent:AgentName=build-1
```

The Agent dials out to the Master (outbound connections only), enrolls, and stands by. On bash, use the matching `.sh` scripts.

## Create your first workflow

Open **Jobs → New job** and create it with the form or YAML. You can also drop a YAML file into `data/jobs/` — it hot-reloads:

```yaml
name: demo
project: Default
jobs:
  build:
    steps:
      - name: Build
        command: dotnet build
  test:
    needs: [build]          # runs only after build succeeds
    runs_on: agent          # runs on an Agent (or agent:<tag>)
    steps:
      - name: Test
        command: dotnet test
```

Click **Run** and watch the dependency graph and live log stream on the Run page.

## Directory layout

Infinity CI uses a job-first layout — all state lives under `data/`:

```text
data/
└── {job}/
    ├── .git        # job config repo (save = commit)
    ├── logs/       # JSONL logs, one file per run number
    └── workspaces/ # checkouts
```

**Backups = copying the `data/` directory.**
