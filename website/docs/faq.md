---
title: FAQ
description: 常见问题
sidebar_position: 11
---

# FAQ

## 默认账号是什么？

首次启动自动创建超级管理员 **admin / admin** 与 Default 项目。生产环境请立即修改密码。

## 需要装数据库吗？

不需要。存储是 **SQLite 单文件**（`data/infinityci.db`），备份就是拷贝 `data/` 目录。

## 构建机在内网 / NAT 后面能用 Agent 吗？

可以，这正是 Agent 的设计场景：Agent 只发起**出站** gRPC 连接（Master 的 5001 端口），构建机不需要开放任何入站端口，也不需要公网 IP。

## Windows 下中文日志乱码吗？

不乱码。子进程输出按控制台代码页（如 GBK）解码，并且注入 `FORCE_COLOR` 等环境变量保证彩色输出在 xterm.js 终端正确渲染。

## 一个 Job 卡死了怎么办？

Job / Step 级 `timeout_minutes`（或 `timeout_seconds`）到点后自动 **kill 整个进程树**（包括 Unity、编译器等子进程）。也可以在 Web 上手动取消 Run。

## 支持哪些 Git 平台？

webhook 载荷兼容 **GitHub / GitHub Enterprise / GitLab / Gitea**（另提供泛化 JSON 载荷），commit status 回写支持 GitHub/GHE 与 GitLab。源码检出用 git 命令行，任何 Git 服务器（包括自建）都可以。

## 如何定义「依赖失败也要跑」的 Job？

给 Job 加 `if: always()`，典型用途是清理与通知。依赖失败的下游 Job 默认自动跳过。

## 服务重启后状态会丢吗？

不会。所有状态（数据库、配置仓库、日志、工作区）都在 `data/` 目录，重启即恢复；Agent 掉线时未完成任务自动重排队。

## 前端页面需要手动刷新吗？

不需要。每个页面建立独立的 SignalR 连接，数据变化实时上屏；连接异常时心跳看门狗按指数回退重连，连续失败会自动刷新页面。
