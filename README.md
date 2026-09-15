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

## 技术栈与依赖

所有依赖版本均已固定（前端精确到补丁号，.NET 由 `global.json` 与精确的 `PackageReference` 固定）。

### 运行环境要求

| 依赖 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.401 | 由 `global.json` 固定（rollForward=latestFeature）；目标框架 net10.0 |
| Node.js | ≥ 20 | 开发环境验证于 26.x |
| npm | ≥ 10 | 开发环境验证于 11.x |
| Git | 任意近期版本 | 任务配置仓库（历史/回滚/克隆）依赖 |

### .NET NuGet 包

**InfinityCI.Core（共享库）**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| Google.Protobuf | 3.36.1 | gRPC 协议消息序列化 |
| Grpc.Core.Api | 2.83.0 | gRPC 公共 API |
| Grpc.Tools | 2.83.0 | Protobuf 编译（仅编译期） |
| LibGit2Sharp | 0.32.0 | Git 检出 / 配置仓库 / SCM 集成 |
| YamlDotNet | 18.1.0 | 工作流 YAML 解析 |

**InfinityCI.Server**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| Grpc.AspNetCore | 2.83.0 | Agent gRPC 服务（端口 5001） |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.12 | SQLite 持久化 |
| Serilog.AspNetCore | 10.0.0 | 结构化日志 |
| System.DirectoryServices.Protocols | 10.0.12 | LDAP 认证 |

**InfinityCI.Agent**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| Grpc.Net.Client | 2.83.0 | 与 Master 的 gRPC 通信 |
| Microsoft.Extensions.Hosting | 10.0.12 | Worker 服务宿主 |

**测试项目**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| xunit / xunit.runner.visualstudio | 2.9.3 / 3.1.4 | 单元与集成测试 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 测试平台 |
| coverlet.collector | 6.0.4 | 覆盖率收集 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | WebApplicationFactory 集成测试 |
| Microsoft.AspNetCore.SignalR.Client | 10.0.12 | SignalR 端到端测试客户端 |

### 前端依赖（web/）

**运行时依赖**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| react / react-dom | 19.2.8 | UI 框架 |
| @tanstack/react-router | 1.170.33 | 类型安全路由 |
| @tanstack/react-query | 5.102.8 | 服务端状态管理 |
| @microsoft/signalr | 10.0.11 | 实时推送客户端（每标签页独立连接） |
| @xterm/xterm + @xterm/addon-fit | 6.0.0 / 0.11.0 | 构建日志终端 |
| zustand | 5.0.15 | 轻量客户端状态 |
| js-yaml | 5.4.1 | 任务编辑器 YAML 往返 |
| lucide-react | 1.43.0 | 图标 |
| i18next / react-i18next | 26.4.2 / 17.0.13 | 国际化 |
| clsx + tailwind-merge | 2.1.1 / 3.6.0 | 样式工具 |
| class-variance-authority | 0.7.1 | 组件变体 |

**开发依赖**

| 包 | 版本 | 用途 |
| --- | --- | --- |
| @rsbuild/core + @rsbuild/plugin-react | 2.2.5 / 2.1.0 | 构建（Rspack，dev 端口 3000，代理 /api 与 /hubs 到 5000） |
| typescript | 7.0.2 | 类型检查（`npm run typecheck`） |
| tailwindcss + @tailwindcss/postcss | 4.3.3 | Tailwind CSS v4（PostCSS 集成） |
| @types/react / @types/react-dom / @types/node / @types/js-yaml | 19.2.18 / 19.2.7 / 26.5.0 / 4.0.9 | 类型声明 |

### 构建与发布

- 前端：`npm run dev`（开发热更新）/ `npm run build`（产物输出 `dist/`，由 Server 托管）
- 后端：`scripts/start-server.cmd` / `scripts/start-agent.cmd`；`scripts/publish.cmd` 发布 win-x64 自包含单文件 EXE 到 `publish/`
