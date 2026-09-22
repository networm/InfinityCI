---
title: 权限与认证
description: RBAC 角色、项目可见性、API Token 与 LDAP
sidebar_position: 6
---

# 权限与认证

## 角色模型

Infinity CI 内置三级角色：

| 角色 | 能力 |
| --- | --- |
| 超级管理员 | 全部能力，包括用户管理、系统设置、Agent 管理 |
| 管理员 | 项目与任务管理、任务启停、签发注册令牌 |
| 普通用户 | 查看可见项目、触发运行、查看日志 |

## 项目可见性

每个用户可被授予若干项目的可见性。**可见性过滤同时作用于 REST API 与实时推送**——用户不会通过 SignalR 收到未授权项目的任何数据变化。

## 个人 API Token

在用户设置中可以签发个人 API Token，用于 CLI、脚本或第三方系统接入：

```bash
curl -H "Authorization: Bearer <token>" \
  http://ci.example.com:5000/api/jobs/my-task/runs
```

Token 拥有与用户相同的权限，可随时吊销。

## LDAP 域登录

面向企业内网场景，Infinity CI 支持 LDAP 认证（如 Active Directory）：

- **配置存数据库、热生效**：在 Web 管理页直接配置 LDAP 服务器地址、Bind DN、搜索基等，无需重启服务；
- **用户自动开通**：域账号首次登录成功后自动创建本地用户；
- **组 → Admin 映射**：指定 AD 组的成员自动授予管理员角色；
- **StartTLS 支持**与**连接测试诊断**：配置页一键测试连通性，快速定位 bind 失败、搜索基错误等问题。

本地账号与 LDAP 账号可以并存：`admin` 超管始终可以本地登录，作为域服务故障时的保底入口。
