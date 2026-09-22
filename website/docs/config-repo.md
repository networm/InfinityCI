---
title: 任务配置 Git 仓库
description: 配置即代码：每个任务一个 Git 仓库，保存即提交
sidebar_position: 8
---

# 任务配置 Git 仓库

Infinity CI 的任务配置不存数据库——**每个任务的 YAML 配置本身就是一个 Git 仓库**（`data/{任务}/.git`，由 LibGit2Sharp 实现）。

## 保存即提交

在 Web 任务编辑器里每次保存（表单或 YAML），配置自动 commit 一次。这带来三个天然能力：

- **历史**：随时查看某次修改是谁、什么时候、改了什么；
- **回滚**：一键回滚到任意历史版本，改坏配置不再需要运维救火；
- **克隆**：`git clone` 任务配置仓库到本地批量修改，或纳入审计流程。

## 批量管理示例

```bash
# 克隆某个任务的配置仓库（只读）
git clone http://ci.example.com:5000/repos/UnityCIGame.git

# 本地修改后提交，Web 端即时看到新版本
```

## 与 SCM 检出是两回事

| | 任务配置仓库 | 工作流 `scm:` 源码检出 |
| --- | --- | --- |
| 存什么 | 任务的工作流 YAML | 你的业务源码 |
| 实现方式 | LibGit2Sharp（对象级操作，精细控制） | git 命令行（应对大型仓库） |
| 在哪 | Server 的 `data/{任务}/` | 执行机（本地或 Agent）的工作区 |

配置仓库需要高频的提交、历史、diff 操作，用内嵌的 LibGit2Sharp；源码检出面向大仓库、在任意执行机上运行，用 git 命令行——两套机制各司其职。
