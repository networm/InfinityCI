---
title: 部署
description: 源码运行、单文件 EXE 与 Docker 三种部署方式
sidebar_position: 9
---

# 部署

Infinity CI 提供三种部署方式，按场景选择。

## 方式一：源码运行（开发 / 试用）

```cmd
scripts\start-server.cmd          :: Server（Web http://127.0.0.1:5000，gRPC 5001）
scripts\start-agent.cmd           :: Agent（需先在 Web 签发注册令牌）
```

bash 环境使用同目录下对应的 `.sh` 脚本。

## 方式二：单文件 EXE（Windows 打包机推荐）

```cmd
scripts\publish.cmd
```

发布 win-x64 **自包含单文件 EXE** 到 `publish\` 目录。构建机不需要安装 .NET 运行时，拷贝一个 EXE 即可运行——适合分发到多台 Windows 构建机。

## 方式三：Docker

```bash
docker compose up -d
```

- 多阶段构建：node:22-alpine 构建前端 → dotnet/sdk:10 发布后端 → aspnet:10 运行时（内置 git / tzdata / libldap）；
- 端口：5000（Web/SignalR）、5001（Agent gRPC）；
- 数据卷：`infinityci-data` 挂载到 `/app/data`。

常用环境变量：

| 变量 | 说明 |
| --- | --- |
| `TZ` | cron 定时触发的时区（如 `Asia/Shanghai`） |
| `PublicOrigin` | 反向代理 / HTTPS 终止场景下的对外地址 |
| SMTP 相关 | 启用邮件通知渠道 |

## 数据与备份

所有状态（SQLite 数据库、任务配置 Git 仓库、日志、工作区）都在 `data/` 目录：

```text
data/
├── infinityci.db      # SQLite
└── {任务}/            # 配置仓库 + 日志 + 工作区
```

**备份 = 拷贝 `data/` 目录**（Docker 部署即拷贝数据卷）。迁移到新机器 = 拷贝目录 + 启动，没有隐藏状态。

## 反向代理

Server 是标准的 ASP.NET Core Kestrel 应用，可以被 Nginx / Caddy 反代。注意：

- WebSocket（SignalR）需要代理支持 `Upgrade` 头；
- 配置 `PublicOrigin` 指向对外域名。
