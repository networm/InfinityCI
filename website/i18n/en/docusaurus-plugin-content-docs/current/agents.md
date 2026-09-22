---
title: Deploying Agents
description: Enroll agents, tag routing, concurrency, and local directories
sidebar_position: 5
---

# Deploying Agents

An Agent is a lightweight process on your build machine that claims jobs and executes them in place. Its communication model is friendly to any network:

- **Outbound connections only**: the Agent dials the Master (gRPC, port 5001). No inbound ports need to be opened — NAT, firewalls, and home broadband all just work;
- **Pull-based claiming**: idle Agents request work from the Master, which dispatches the full workflow YAML, SCM credentials, parameters, and environment variables;
- **Self-healing**: heartbeat leases and auto-reconnect; when an Agent goes offline, its unfinished jobs are automatically requeued elsewhere.

## Enrollment

1. Issue a one-time enroll token from the **Agents page** in the web UI (prevents unauthorized machines from joining);
2. Start the Agent on the build machine:

```cmd
scripts\start-agent.cmd --Agent:EnrollToken=<token> --Agent:AgentName=build-1
```

3. The Agent appears in the Agents list with online status and current activity.

## Tag routing

Tag agents (e.g. `unity`, `windows`, `android`) from the agent config dialog, then target them in workflows:

```yaml
jobs:
  build:
    runs_on: agent:unity   # only runs on agents tagged unity
```

## Concurrency and environment variables

The agent config dialog also supports:

- **Max concurrency**: cap on jobs executing simultaneously on that machine;
- **Agent-level environment variables**: injected into every step the agent executes — ideal for `UNITY_PATH`, SDK paths, and other machine-specific settings.

## Binding local directories

When the build depends on machine-local software (a Unity project, an Android SDK), don't put it in Git — bind the task directly to a **local directory** on the build machine and the agent executes in place:

```text
C:\Users\me\Work\Projects\MyUnityGame
```

Relative-path commands in the workflow (like `python CI/platform/windows/build.py`) run against that directory. See the [Unity build recipe](/docs/unity).

## High-availability behavior

| Scenario | Behavior |
| --- | --- |
| Agent goes offline | After lease expiry, unfinished jobs are requeued |
| Master restarts | Agents reconnect automatically |
| Stuck job process | Job/Step timeout kills the entire process tree |
