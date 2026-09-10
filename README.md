# Infinity CI

一个使用 C# / .NET 10 构建的类 Jenkins / GitHub Actions 持续集成服务器。

## 特性

- **并行 Run / Job / Step**：一个工作流的多个 Job 并行执行；每个步骤独立的控制台输出
- **Job 依赖图（needs）**：`needs: [a, b]` 声明依赖，形成 DAG；依赖失败时下游自动跳过；Run 页面渲染依赖图，点击节点切换日志
- **逐行时间戳日志**：每个 Job 独立 JSONL 日志文件，行号即断点续传游标
- **分布式 Agent**：Agent 主动外连 Master（gRPC，端口 5001），心跳租约、拉取式领任务、断线自动重连、孤儿任务自动重排队
- **多标签页实时**：每个浏览器标签页独立 SignalR 连接，按资源分组推送，互不影响
- **用户权限**：超级管理员 / 管理员 / 普通用户 + 按项目可见性
- **任务配置 Git 仓库**：`data/jobs` 自动 init 为 Git 仓库，每次修改自动提交；Web 查看历史、一键回滚；支持 `git clone http://user:pass@host:5000/git/jobs` 只读克隆

## 快速开始

```cmd
scripts\start-server.cmd          :: 启动 Server（Web http://127.0.0.1:5000，gRPC 5001）
scripts\start-agent.cmd           :: 启动 Agent（需先在 Web 的 Agents 页签发注册令牌）
scripts\start-agent.cmd --Agent:EnrollToken=<令牌> --Agent:AgentName=build-1
scripts\publish.cmd               :: 发布自包含单文件 EXE 到 publish\
```

bash 环境使用同目录下对应的 `.sh` 脚本。

首次启动自动创建超级管理员 **admin / admin** 与 Default 项目（请立即修改密码）。

## 定义工作流

在 Web「任务 → 新建任务」用表单或 YAML 创建；YAML 会被写入 `data/jobs/*.yml`
（Git 仓库，保存即提交）。也可以直接把文件放进该目录，保存即热加载：

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

## 架构

| 项目 | 说明 |
| --- | --- |
| `src/InfinityCI.Core` | 工作流/运行模型、YAML 解析、跨平台 Shell 解析、gRPC 协议 |
| `src/InfinityCI.Server` | Web + REST + SignalR（5000）、Agent gRPC（5001）、并行调度引擎 |
| `src/InfinityCI.Agent` | Agent 进程：注册/心跳/拉取式领任务/远程执行 |
| `web` | React 19 + Rsbuild + Tailwind 前端（GitHub Actions 风格） |
