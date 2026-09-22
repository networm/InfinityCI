---
title: Agent 部署指南
description: 注册 Agent、标签路由、并发与本地目录
sidebar_position: 5
---

# Agent 部署指南

Agent 是运行在构建机上的轻量进程，负责领取任务并就地执行。它的通信模型对网络环境非常友好：

- **只发起出站连接**：Agent 主动外连 Master（gRPC，端口 5001），不需要在构建机上开放任何入站端口，NAT / 防火墙 / 家庭宽带环境直接可用；
- **拉取式领任务**：Agent 空闲时向 Master 请求任务，Master 下发完整的工作流 YAML、SCM 凭据、参数与环境变量；
- **自愈能力**：心跳租约、断线自动重连，Agent 掉线后其未完成任务自动重排队给其他 Agent。

## 注册 Agent

1. 在 Web 的 **Agents 页**点击签发注册令牌（EnrollToken），令牌为一次性使用，防止未授权机器接入；
2. 在构建机上启动 Agent：

```cmd
scripts\start-agent.cmd --Agent:EnrollToken=<令牌> --Agent:AgentName=build-1
```

3. Agent 注册成功后出现在 Agents 列表中，显示在线状态与当前任务。

## 标签路由

在 Agent 配置对话框（Web Agents 页）中可以为 Agent 打标签，如 `unity`、`windows`、`android`。工作流中用 `runs_on: agent:<标签>` 把任务定向到特定构建机：

```yaml
jobs:
  build:
    runs_on: agent:unity   # 只会在带 unity 标签的 Agent 上执行
```

## 并发与环境变量

Agent 配置对话框还支持：

- **最大并发数**：该构建机同时执行的 Job 上限；
- **Agent 级环境变量**：注入到该 Agent 执行的所有 Step，适合放 `UNITY_PATH`、SDK 路径等机器特定配置。

## 任务绑定本地目录

构建环境依赖本机软件（如 Unity 工程、Android SDK）时，不必把工程放进 Git——在任务设置里直接绑定构建机上的**本地目录**，Agent 就地执行：

```text
C:\Users\me\Work\Projects\MyUnityGame
```

工作流中的相对路径命令（如 `python CI/platform/windows/build.py`）都基于该目录执行。详见 [Unity 打包实战](/docs/unity)。

## 高可用行为

| 场景 | 行为 |
| --- | --- |
| Agent 掉线 | 租约到期后，其未完成任务自动重排队 |
| Master 重启 | Agent 自动重连 |
| 任务进程僵死 | Job/Step 超时到点 kill 整个进程树 |
