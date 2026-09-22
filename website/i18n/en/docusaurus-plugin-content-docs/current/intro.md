---
title: Introduction
description: What Infinity CI is and what it can do
sidebar_position: 1
---

# Introduction

**Infinity CI** is a Jenkins / GitHub Actions-style continuous integration server built with C# / .NET 10. It brings the GitHub Actions execution model and experience to your own hardware: a single binary plus SQLite gets you started — no Java, no external database, no plugin ecosystem.

## The problem it solves

- **Jenkins is heavy**: Java ecosystem, plugin hell, an external database — too much maintenance for individuals and small teams;
- **GitHub Actions lives in the cloud**: game projects are often gigabytes in size, build environments depend on local software, and code often can't leave the intranet;
- **Commercial products cost money**: priced per concurrency or seat — unaffordable for small teams.

Infinity CI's answer: **a single EXE or one Docker container running on your own Windows/Linux machine**, with Agents deployed anywhere that has outbound connectivity (including a home build box), jobs defined in YAML, build logs streamed live to the browser, and results pushed to WeCom/DingTalk.

## Core features

- **Parallel Run / Job / Step**: multiple jobs of a workflow run concurrently; every step gets its own console output
- **Job dependency graph (needs)**: `needs: [a, b]` forms a DAG; failed dependencies skip downstream jobs; jobs with `if: always()` still run; the Run page renders a Blue Ocean-style graph — click a node to switch logs
- **Timeouts & retries**: Job/Step-level `timeout_minutes` kills the whole process tree; Step `retry: n` retries failures automatically
- **Distributed Agents**: Agents dial out to the Master (gRPC) with heartbeat leases, pull-based job claiming, auto-reconnect, and orphaned-job requeue — **build machines need zero inbound ports**
- **Real-time everywhere**: every page holds its own SignalR connection; a heartbeat watchdog reconnects with exponential backoff — no manual refresh buttons anywhere
- **Per-line timestamped logs**: each job writes its own JSONL log file where line numbers double as resume cursors; xterm.js renders full ANSI color
- **Multiple triggers**: manual runs (with parameters), Git push webhooks (HMAC-SHA256 + branch globs), PR/MR triggers, cron schedules
- **SCM integration**: automatic source checkout via the git CLI (scales to large repos); commit status written back to GitHub/GHE/GitLab
- **Config as Git repos**: every job's config is its own repository — save commits automatically, with history/rollback/clone for free
- **Access control**: super admin / admin / user RBAC + per-project visibility + personal API tokens + LDAP domain login
- **Notifications**: WeCom / DingTalk / Slack / generic webhook / SMTP email, filterable per channel
- **Bilingual UI** (Chinese/English) and a dark theme

## Architecture

| Project | Description |
| --- | --- |
| `InfinityCI.Core` | Workflow/run models, YAML parsing, cross-platform shell resolution, gRPC protocol |
| `InfinityCI.Server` | Web + REST + SignalR (5000), Agent gRPC (5001), parallel scheduling engine |
| `InfinityCI.Agent` | Agent process: enroll/heartbeat/pull jobs/remote execution |
| `web` | React 19 + Rsbuild + Tailwind frontend (GitHub Actions style) |

```text
┌──────────┐  webhook/cron/manual  ┌─────────────────┐  gRPC (5001)  ┌──────────┐
│ Git repo ├──────────────────────▶│   Server (:5000) ├◀──────────────┤  Agent 1  │
└──────────┘                       │  schedule·log·notify            └────┬─────┘
                                   │  SQLite + data/  │  pull jobs     │ executes
┌──────────┐  SignalR live push    │                 │  gRPC (5001)   ┌────▼─────┐
│ Browser  ◀───────────────────────┤                 ├◀───────────────┤  Agent N  │
└──────────┘                       └─────────────────┘                └──────────┘
```

## Who it's for

- **Solo developers** who want Unity/desktop packaging automated without maintaining a Jenkins;
- **Small teams** that need intranet-hosted CI, domain login, and WeCom notifications;
- **Teams with picky build machines**: when builds depend on specific hardware (Unity, Android SDK), Agents execute in place — the project stays put and CI comes to it.
