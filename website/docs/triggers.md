---
title: 触发方式
description: 手动、webhook、PR/MR、cron 与 API 触发
sidebar_position: 4
---

# 触发方式

Infinity CI 支持五种触发方式。

## Web 手动触发

在任务页点击「运行」，触发对话框中可以填写 [参数化构建](/docs/workflow#params-参数化构建) 的参数值。

## Git push webhook

每个任务可以生成一个 webhook 入口：

```text
POST /api/webhooks/{token}
```

特性：

- **HMAC-SHA256 验签**：校验 `X-Hub-Signature-256` 或 `X-Signature` 请求头，防伪造
- **分支过滤**：通配符匹配，如 `main,release/*`，仅命中的分支触发
- **多平台载荷兼容**：GitHub / GitHub Enterprise / GitLab / Gitea 的 push 事件 `ref` 载荷直接支持，同时提供泛化 JSON 载荷适配其他平台
- **PR / MR 触发**：支持 Pull Request / Merge Request 事件，并自动检出 PR 源分支

在 GitHub / GitLab 项目的 Webhook 设置里填入上述 URL 即可。构建结束后，pending 与终态会自动回写为 **commit status**（GitHub/GHE）或 pipeline status（GitLab），PR 页面直接显示 CI 结果。

## cron 定时

在工作流 YAML 顶层声明 [schedule](/docs/workflow#schedule-定时触发)，到点自动触发，支持多个时间表达式。

## API Token 触发

每个用户可以签发个人 API Token（`Authorization: Bearer <token>`），供 CLI / 脚本 / 第三方系统以该用户身份调用 REST API 触发构建或查询状态。

## 任务启停

管理员可以在 Web 上禁用 / 启用任务：禁用后所有触发方式（包括 webhook 与 cron）都不会再产生新的 Run。
