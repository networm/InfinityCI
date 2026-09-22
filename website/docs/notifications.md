---
title: 通知渠道
description: 企业微信、钉钉、Slack、Webhook 与邮件通知
sidebar_position: 7
---

# 通知渠道

构建结束时（成功或失败），Infinity CI 会向配置的通知渠道推送消息。支持五个渠道：

| 渠道 | 接入方式 |
| --- | --- |
| 企业微信 | 群机器人 Webhook 地址；成功/失败使用不同颜色的 markdown 卡片 |
| 钉钉 | 群机器人 Webhook（支持加签） |
| Slack | Incoming Webhook |
| 通用 Webhook | POST JSON 到任意 URL，方便对接自有系统 |
| 邮件 | SMTP 发送（Docker 部署时通过环境变量配置 SMTP） |

## 按渠道事件过滤

每个渠道可以独立配置关心的事件，例如：

- 仅失败时通知（避免成功消息刷屏）；
- 成功与失败都通知；
- 仅特定任务的通知。

通知发送失败**不影响 Run 本身**——通知系统的故障被完全隔离。

## 典型配置：Unity 打包 + 企业微信

游戏团队最常用的组合：任务绑定 Windows 打包机 Agent，构建结束推企业微信群卡片，测试同学第一时间知道新包已产出：

```text
┌ Infinity CI 任务通知 ───────────────┐
│ ✅ UnityCIGame #42 构建成功          │
│ 分支: main · 耗时: 14分22秒          │
│ 产物: Builds/Windows/UnityCIGame.exe │
└─────────────────────────────────────┘
```
