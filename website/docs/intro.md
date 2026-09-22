---
title: 简介
description: Infinity CI 是什么，能做什么
sidebar_position: 1
---

# 简介

**Infinity CI** 是一个使用 C# / .NET 10 构建的类 Jenkins / GitHub Actions 持续集成服务器。它把 GitHub Actions 的运行模型与使用体验搬到你自己的机器上：单二进制 + SQLite 即可起步，不需要 Java、不需要数据库、不需要插件生态。

## 它解决什么问题

- **Jenkins 太重**：Java 生态 + 插件地狱 + 外置数据库，个人与小型团队维护成本高；
- **GitHub Actions 在云端**：游戏工程动辄数 GB，出包环境依赖本机软件，很多代码不能出内网；
- **TeamStyle 商业产品收费**：按并发/用户收费，小团队用不起。

Infinity CI 的答案：**单 EXE 或一个 Docker 容器，跑在你自己的 Windows/Linux 机器上**，Agent 可以部署在任何能出网的地方（包括家里的打包机），任务通过 YAML 定义，构建日志实时推到浏览器，结果推到企业微信/钉钉。

## 核心特性

- **并行 Run / Job / Step**：一个工作流的多个 Job 并行执行；每个 Step 独立控制台输出
- **Job 依赖图（needs）**：`needs: [a, b]` 声明依赖形成 DAG；依赖失败下游自动跳过；`if: always()` 的 Job 仍会运行；Run 页面渲染 Blue Ocean 风格依赖图，点击节点切换日志
- **超时与重试**：Job / Step 级 `timeout_minutes` 超时自动 kill 整个进程树；Step `retry: n` 失败自动重试
- **分布式 Agent**：Agent 主动外连 Master（gRPC），心跳租约、拉取式领任务、断线自动重连、孤儿任务自动重排队——**打包机无需开放任何入站端口**
- **全页面实时**：每个页面独立 SignalR 连接，数据变化实时上屏；心跳看门狗指数回退重连，全站没有手动刷新按钮
- **逐行时间戳日志**：每个 Job 独立 JSONL 日志文件，行号即断点续传游标；xterm.js 渲染 ANSI 全彩
- **多种触发**：Web 手动（支持参数化）、Git push webhook（HMAC-SHA256 验签 + 分支通配过滤）、PR/MR 触发、cron 定时
- **SCM 集成**：源码自动检出（git 命令行，支持大型仓库）；commit status 回写 GitHub/GHE/GitLab
- **任务配置 Git 仓库**：每个任务独立仓库，保存即提交，天然拥有历史/回滚/克隆
- **权限与安全**：超管/管理员/普通用户三级 RBAC + 项目可见性 + 个人 API Token + LDAP 域登录
- **通知**：企业微信 / 钉钉 / Slack / 通用 Webhook / 邮件 SMTP，按渠道事件过滤
- **中英双语 UI**、暗色主题

## 架构

| 项目 | 说明 |
| --- | --- |
| `InfinityCI.Core` | 工作流/运行模型、YAML 解析、跨平台 Shell 解析、gRPC 协议 |
| `InfinityCI.Server` | Web + REST + SignalR（5000）、Agent gRPC（5001）、并行调度引擎 |
| `InfinityCI.Agent` | Agent 进程：注册/心跳/拉取式领任务/远程执行 |
| `web` | React 19 + Rsbuild + Tailwind 前端（GitHub Actions 风格） |

```text
┌──────────┐   webhook/cron/手动   ┌─────────────────┐   gRPC(5001)   ┌──────────┐
│ Git 仓库 ├──────────────────────▶│   Server (:5000) ├◀──────────────┤  Agent 1  │
└──────────┘                       │  调度·日志·通知   │   拉取式领任务  └────┬─────┘
                                   │  SQLite + data/  │                     │ 本地执行
┌──────────┐   SignalR 实时推送     │                 │   gRPC(5001)   ┌────▼─────┐
│  浏览器  ◀───────────────────────┤                 ├◀───────────────┤  Agent N  │
└──────────┘                       └─────────────────┘                └──────────┘
```

## 适合谁

- **个人开发者**：想让 Unity/桌面软件打包自动化，但不想养一套 Jenkins；
- **小团队**：需要内网自托管 CI、域账号登录、企业微信通知；
- **有异构构建机的团队**：构建环境依赖特定机器（如装了 Unity、Android SDK 的 Windows 打包机），Agent 就地执行，工程不动、CI 来就你。
