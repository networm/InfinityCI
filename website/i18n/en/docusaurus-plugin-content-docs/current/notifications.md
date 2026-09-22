---
title: Notification Channels
description: WeCom, DingTalk, Slack, webhooks, and email
sidebar_position: 7
---

# Notification Channels

When a run finishes (success or failure), Infinity CI pushes a message to every configured channel. Five channels are supported:

| Channel | Integration |
| --- | --- |
| WeCom | Group robot webhook; success/failure use differently colored markdown cards |
| DingTalk | Group robot webhook (signing supported) |
| Slack | Incoming webhook |
| Generic webhook | POST JSON to any URL — wire up your own systems |
| Email | SMTP (configure via environment variables in Docker deployments) |

## Per-channel event filters

Each channel independently chooses the events it cares about, for example:

- Notify on failure only (keep success out of the group chat);
- Notify on both success and failure;
- Notify only for selected jobs.

Notification failures **never affect the run itself** — the notification subsystem is fully isolated.

## Typical setup: Unity builds + WeCom

The combo most game teams use: a job bound to a Windows build agent, results pushed to a WeCom group as a colored card, and QA knows a new build is ready instantly:

```text
┌ Infinity CI job notification ──────────┐
│ ✅ UnityCIGame #42 succeeded           │
│ Branch: main · Duration: 14m 22s       │
│ Artifact: Builds/Windows/UnityCIGame.exe│
└────────────────────────────────────────┘
```
