# 计划：纠正 git 实现划分——配置仓库回归 LibGit2Sharp，源码 SCM 检出改用 git 命令行

## 背景澄清
上一轮我把「任务配置仓库」（WorkflowGitStore，存 workflow.yml 的那个）改成了 git CLI——改错了对象。本轮纠正：
- **任务配置仓库 → 恢复 LibGit2Sharp**（原实现一直在 git 历史里）
- **工作流 YAML 里配置的 `scm:` 源码检出（GitSourceFetcher）→ 改用 git 命令行**，本地执行与 agent 上执行共用这一份代码（GitSourceFetcher 在 Core，Agent 的 RemoteBuildRunner 与 Server 的 JobRunExecutor 都调用它），以 git CLI 应对大型仓库

## 改动 1：WorkflowGitStore 恢复 LibGit2Sharp 版本
- `git show 8acb3c4:src/InfinityCI.Server/Jobs/WorkflowGitStore.cs` 还原（4e7acb9 改坏前的版本）
- 无需动 csproj（LibGit2Sharp 引用仍在）；测试构造函数签名不变（options, logger）

## 改动 2：GitSourceFetcher（Core）改为 git 命令行实现
保持公共 API 不变：`Fetch(scm, workspace, credential, log) → CheckoutResult(sha, branch)`，内部全部换 git 子进程：
- **首次**：`git clone --no-checkout <url> <dir>`（等价原 CloneOptions.Checkout=false）
- **增量**：`git fetch --prune origin`
- **检出语义逐条对齐原实现**：
  - 配置 `ref`：校验存在（rev-parse）→ `git checkout --force <ref>`（detached），报告 `ref <ref>`
  - 配置 `branch`：校验 `origin/<branch>` 存在 → `git checkout --force -B <branch> --track origin/<branch>`，报告分支名
  - 都不配：读 `refs/remotes/origin/HEAD`（clone 会设置）检出默认分支；读不到则 `git checkout --force` 兜底，报 "default"
- **凭据**：HTTP(S) + 用户名时用 `-c http.extraHeader="Authorization: Basic base64(user:pass)"`（凭据不落 .git/config、不经 shell、无 URL 转义问题）；设 `GIT_TERMINAL_PROMPT=0` 与空 `credential.helper` 防止交互挂起
- **无超时等待**：clone/fetch 大仓库可能很久，不设进程超时（与原 LibGit2Sharp 行为一致）
- 日志文案保持原样（`[server] checked out …`，集成测试断言依赖它）

## 改动 3：文档与注释
- GitSourceFetcher 类注释、WorkflowGitStore 注释、README 特性行：改为「任务配置仓库 = LibGit2Sharp；源码 SCM 检出 = git 命令行（应对大型仓库），agent 与本地共用」

## 验证
- `dotnet test`：ScmIntegrationTests 会真实走新的 git CLI 检出路径（测试源仓库仍用 LibGit2Sharp 创建）；WorkflowStoreTests 等回到 LibGit2Sharp 路径
- 提交两笔：① WorkflowGitStore 回退 LibGit2Sharp；② GitSourceFetcher 改 git CLI