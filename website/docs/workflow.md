---
title: 工作流语法
description: 工作流 YAML 完整字段参考
sidebar_position: 3
---

# 工作流语法

工作流用 YAML 定义，语法对 GitHub Actions 用户非常熟悉。顶层字段：

```yaml
name: string              # 任务名（唯一标识，也用于 URL）
project: string           # 所属项目
schedule: string | list   # 可选，5 字段 cron，如 "*/5 * * * *"，支持列表
params:                   # 可选，参数化构建，导出为环境变量
  CONFIG: Release
scm:                      # 可选，构建前自动检出源码
  url: https://github.com/org/repo.git
  ref: main
  credentials: my-git     # 凭据名（在服务端配置）
jobs:
  <job-id>:
    needs: [a, b]         # 依赖的 Job，形成 DAG
    runs_on: local | agent | agent:<标签>
    timeout_minutes: 30   # Job 级超时（或 timeout_seconds）
    if: always()          # 依赖失败后仍运行（如清理、通知 Job）
    steps:
      - name: Step 名称
        command: 任意 shell 命令
        timeout_seconds: 600        # Step 级超时
        retry: 2                    # 失败自动重试次数
        continue_on_error: true     # 失败不阻塞后续 Step
```

## jobs

一个工作流包含多个 Job。**多个 Job 默认并行执行**，除非用 `needs` 声明依赖：

```yaml
jobs:
  lint:
    runs_on: local
    steps:
      - name: Lint
        command: npm run lint
  build:
    needs: [lint]          # lint 成功后才启动
    runs_on: agent:windows # 只派发给带 windows 标签的 Agent
    steps:
      - name: Build
        command: npm run build
  notify:
    needs: [build]
    if: always()           # 即使 build 失败也运行
    steps:
      - name: Notify
        command: curl -X POST https://hooks.example.com/...
```

依赖失败的下游 Job 自动跳过；`if: always()` 的 Job 例外，适合清理、通知场景。

## runs_on

| 值 | 行为 |
| --- | --- |
| `local` | 在 Server 本机进程内执行（受 `MaxConcurrentJobs` 并发限制，默认 2） |
| `agent` | 派发给任意空闲 Agent |
| `agent:<标签>` | 只派发给带指定标签的 Agent（如 `agent:unity`、`agent:windows`） |

## steps

Step 按 `ShellResolver` 的规则跨平台执行：Windows 默认 cmd（可选 powershell/pwsh），Unix 为 sh/bash/pwsh。执行时自动注入：

- `CI=true`、`INFINITY_RUN_ID`、`INFINITY_JOB_KEY`
- `FORCE_COLOR=1`、`COLORTERM=truecolor`、`TERM=xterm-256color`（强制彩色日志）
- 工作流 `params` 参数与 Agent 级环境变量

Windows 下按控制台代码页（如 GBK）解码子进程输出，中文不乱码。

超时到点会 **kill 整个进程树**（防止僵尸 Unity/编译器进程），`retry: n` 对失败 Step 自动重试。

## scm 源码检出

声明 `scm:` 块后，Job 执行前先在执行机（本地或 Agent）上检出源码：

```yaml
scm:
  url: https://github.com/org/repo.git
  ref: main               # 分支 / tag / commit
  credentials: my-git     # 可选，私有仓库凭据
```

源码检出使用 **git 命令行**执行（Server 与 Agent 双端一致），以应对大型仓库。

## params 参数化构建

`params` 中的键值会导出为环境变量，Web 手动触发时可以在触发对话框里修改参数值。

## schedule 定时触发

```yaml
schedule: "*/5 * * * *"            # 每 5 分钟
schedule:                          # 或列表
  - "0 2 * * *"                    # 每天凌晨 2 点
  - "0 9 * * 1-5"                  # 工作日早上 9 点
```

5 字段 cron（分 时 日 月 周），时区跟随服务端（Docker 部署可用 `TZ` 环境变量控制）。
