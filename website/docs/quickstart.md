---
title: 快速开始
description: 两个命令跑起 Infinity CI，创建第一个工作流
sidebar_position: 2
---

# 快速开始

## 环境要求

| 依赖 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.401 | 由 `global.json` 固定（rollForward=latestFeature） |
| Node.js | ≥ 20 | 仅前端开发需要，生产用预构建产物 |
| npm | ≥ 10 | 同上 |
| Git | 任意近期版本 | 任务配置仓库 / SCM 检出依赖 |

## 启动 Server

```cmd
scripts\start-server.cmd
```

- Web 界面：http://127.0.0.1:5000
- Agent gRPC：5001
- 首次启动自动创建超级管理员 **admin / admin** 与 Default 项目（请立即修改密码）

## 启动 Agent

先在 Web 的 **Agents 页**签发一个一次性注册令牌（EnrollToken），然后在构建机上：

```cmd
scripts\start-agent.cmd --Agent:EnrollToken=<令牌> --Agent:AgentName=build-1
```

Agent 会主动外连 Master（只需出站连接），注册后进入待命状态。bash 环境使用同目录下对应的 `.sh` 脚本。

## 创建第一个工作流

进入 **任务 → 新建任务**，用表单或 YAML 创建。也可以直接把 YAML 文件放进 `data/jobs/` 目录，保存即热加载：

```yaml
name: demo
project: Default
jobs:
  build:
    steps:
      - name: Build
        command: dotnet build
  test:
    needs: [build]          # 依赖 build 成功后才运行
    runs_on: agent          # 在 Agent 上执行（可选 agent:<标签>）
    steps:
      - name: Test
        command: dotnet test
```

点击 **运行**，即可在 Run 页面看到依赖图与实时日志流。

## 目录约定

Infinity CI 采用「任务优先」布局，所有状态都在 `data/` 下：

```text
data/
└── {任务}/
    ├── .git        # 任务配置仓库（保存即提交）
    ├── logs/       # 按运行号存放的 JSONL 日志
    └── workspaces/ # 检出的工作区
```

**备份 = 拷贝 `data/` 目录**。
