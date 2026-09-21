# 9 天、90 个提交：我用 Z Code 从零写出一个 CI 服务器

> 全程只用了 Z Code 一款 AI 编程工具。第一天开工约 2 小时，一个基本可用的 CI 就跑起来了；9 天后，它在我的 Windows 打包机上稳定执行 Unity 6 打包。

## 为什么要自己写一个 CI

我是做 Unity 开发的，一直有个老大难问题：**打包**。

每次出包，要在 Unity 编辑器里点 Build，盯着进度条，等十几分钟，然后手动拷贝产物、通知测试。想挂到 CI 上自动化？看了看市面上的方案：

- **Jenkins**：功能全，但 Java 生态 + 插件地狱 + 要装数据库，为了打个包维护一整套 Jenkins 太重了；
- **GitHub Actions**：体验很好，但它是云端服务，游戏项目动辄几个 GB 的工程和出包环境，而且很多时候代码根本不能出内网；
- **TeamCity / GitLab CI**：要么收费要么又背上一个大平台。

我想要的其实很简单：一个**轻量的、跑在自己机器上的 CI**，有个 Web 界面能看到构建日志，能分发任务到装了 Unity 的打包机，微信里能收到构建结果通知。这个需求看似简单，但把 Jenkins/GitHub Actions 的核心体验（并行任务、依赖图、实时日志、分布式 Agent）都做齐，传统认知里没有几人月下不来。

于是 9 月 9 日晚上 11 点 40 分，我打开了 Z Code，发了一条消息：

> 现在使用 C# .NET 10 做一个类似 Jenkins 的 CI 服务器软件，应该做哪些技术选型？前端后端都算上

这篇文章记录的，就是接下来 9 天里，我如何用 Z Code 这一款 AI 工具，从这一条消息写到 Unity 6 打包 Windows 全流程跑通。

## 先看结果：Infinity CI 是什么

先介绍下这个软件本身。它叫 **Infinity CI**，一个用 C# / .NET 10 构建的类 Jenkins / GitHub Actions 持续集成服务器。最终数据：**9 天、90 个提交、净增 22,212 行代码**，全部由 Z Code 编写，我一个字一个字敲的代码行数为零。

核心能力清单：

- **并行 Run / Job / Step**：GitHub Actions 式的运行模型，`needs` 声明依赖形成 DAG，依赖失败下游自动跳过，Run 页面渲染可点击的依赖图（Jenkins Blue Ocean 风格）
- **分布式 Agent**：Agent 主动外连 Master（gRPC），拉取式领任务、心跳租约、断线自动重连、孤儿任务自动重排队——打包机不需要开任何入站端口，NAT / 防火墙友好
- **全页面实时**：每个页面一条独立 SignalR 连接，数据变化实时上屏，整个项目没有一个手动刷新按钮；心跳看门狗指数回退重连
- **xterm.js 彩色日志**：逐行时间戳、JSONL 断点续传、按 Step 下载、ANSI 全彩渲染
- **触发方式**：Web 手动 / Git push webhook（HMAC 验签 + 分支通配过滤）/ PR 触发 / cron 定时
- **配置即 Git 仓库**：每个任务的 YAML 配置本身是一个 Git 仓库，Web 表单保存即自动 commit，天然获得历史、回滚、克隆
- **企业级补全**：RBAC 三级角色 + 项目可见性 + 个人 API Token + LDAP 域登录
- **通知**：企业微信 / 钉钉 / Slack / 通用 Webhook / 邮件，按事件过滤
- **部署**：SQLite 单文件存储，可发布为 win-x64 自包含单文件 EXE，也有多阶段 Dockerfile

技术栈一览：

| 层 | 技术 |
| --- | --- |
| 服务端 | C# / .NET 10、EF Core + SQLite、SignalR、gRPC（Agent 通道）、Serilog、LDAP |
| 前端 | React 19、TypeScript、Rsbuild（Rspack）、Tailwind CSS v4、xterm.js 6、i18next 中英双语 |
| Agent | .NET Worker + gRPC 双向流 |
| 协议 | Protobuf 定义 Agent 协议、YAML 定义工作流 |

架构一句话：**Server（调度 + Web）→ Agent（装在打包机上领任务干活）→ Web UI（实时看板）**，外部对接 Git 仓库和通知渠道。

> 📷 截图位：Infinity CI 首页 Dashboard（Jenkins 风格任务卡片）

## 第一天：约 2 小时，从一条消息到能用的 CI

9 月 9 日 23:40，我发出第一条消息（就是上面那条问技术选型的）。Z Code 给出了完整的技术选型分析，我补充了一条关键需求：

> 打开的每一个网页都作为一个单独的客户端实时与服务器连接，能拿到最新的状态。
> 用户可能在一台机器上打开多个网页，分别查看不同任务，互相之间不能有影响。

然后就是见证效率的时刻了。看当天的 git log：

```text
00:22  chore: scaffold .NET 10 solution (Core/Server/Agent/Tests)
00:26  feat(core): job 定义模型、YAML 解析校验、跨平台 shell 解析器
01:01  feat(server): 构建队列、进程执行引擎、基于偏移量的日志存储
01:15  feat(server): SignalR hub（按连接分组订阅 + offset 续传）
01:19  feat(server): jobs/builds REST API + 分页日志
01:26  feat(web): Rsbuild + React 19 + Tailwind v4 前端脚手架
01:37  feat(web): 实时仪表盘 + xterm.js 日志流详情页
01:52  feat(agent): gRPC Agent 协议（拉取式派发 + 租约恢复）
02:01  feat(web): Agent 管理页；gRPC 独立 HTTP/2 端口 5001
```

从 23:40 的第一条消息到凌晨 2:01 最后一个提交，**大约 2 小时 20 分钟**，一个 CI 服务器需要的全部骨架——.NET 10 后端（任务队列、进程执行引擎、SignalR 实时推送、REST API）、React 19 前端（仪表盘 + 日志流）、gRPC 分布式 Agent——三线全部打通，而且每一块都有提交、有测试项目。这中间我几乎没有再打字，Z Code 在持续自主施工。

到这里还没完。第二天早上 7 点 57 分，我睡醒后发了一条大需求：

> UI 风格改为 https://github.com/actions/runner-images/actions/runs/34350318609/job/102461706314
> 需要支持并行任务
> 每一步改为单独的 Console，像上面链接中的一样
> 每一条日志需要增加时间戳
> 需要增加任务配置界面
> 需要增加用户权限系统，支持超级管理员、普通管理员、普通用户，支持设置每个用户可见的项目
> ……

半小时后，git 历史上出现了两记 `breaking change` 重构：一个 52 文件 +2972/-1959 行，把执行模型改成了 GitHub Actions 式的并行 Run；一个 20 文件 +2190/-522 行，前端整体换成 GHA 风格 UI + 登录页 + 角色权限。**第一天结束时，它已经不是一个玩具，而是一个有权限体系的、对标 GitHub Actions 运行模型的 CI 了。**

这里有个我事后复盘很感慨的点：如果是人写代码，"推翻执行模型重写"这种事，第一天的 psyche 扛不住。但 Z Code 重构 3000 行跟写 300 行一样平静，我只需要决策"要不要这么改"。

## 第二天：功能大爆炸（23 个提交）

9 月 11 日是整个项目提交最多的一天，23 个提交。凌晨 3 点 21 分我发了一条消息，一口气提了四个需求：

> 增加运行服务器与 Agent 脚本
> job 可以指定依赖的 job，从而形成一个图
> 需要将 job 由依赖关系组成的图显示出来，并且可以点击后切换下面的 job 详情及 steps 日志，使用 GitHub 风格
> 每个任务的配置放到单独的 Git 仓库中，并且可以查看历史，也支持克隆到本地

这一天落地的东西，随便拎一个出来都是正经 CI 的核心功能：

- **DAG 依赖调度**：`needs: [a, b]` 声明依赖，失败级联跳过，页面上渲染成可点击的依赖图；
- **配置即 Git 仓库**：任务的 YAML 存成独立 Git 仓库，Web 表单保存即 commit，能看历史、能回滚、能 `git clone`——这个设计我很得意，比 Jenkins 把配置埋进数据库优雅多了；
- **Git SCM 集成**：工作流声明 `scm:` 块，构建前自动检出源码；
- **Jenkins 风格首页 Dashboard**：任务卡片、状态图标、收藏置顶；
- **Webhook 触发 + 企业微信机器人通知**。

最有意思的是 DAG 图的视觉打磨。我拿着 Jenkins Blue Ocean 的截图逐条提意见：

> 学习这张图片中的任务图风格，开始和结束以及大部分结点在一条线上，并行的任务按照顺序排在并行结点下方。结点之间使用圆角直线连接。

> ok 的进入线与退出线的后半段变成了直线，需要改成圆角肘形

一个上午连修五轮，最终的效果是：主线结点在一条水平线上、并行分支垂下来再汇聚、圆角肘形连线、成功打勾失败打叉。这就是 AI 编程的正确用法之一——**审美和"像不像那个产品"的判断我来给，SVG 连线怎么画让它去迭代**。

> 📷 截图位：Run 页面的 Blue Ocean 风格 DAG 依赖图

## 第 3 到 8 天：每天一个主题

之后的一周，节奏变成"每天一个主题"，每天睡前或起床后看一眼哪里不顺眼，丢给 Z Code：

**9 月 13 日（企业化）**：从失败步骤重试、复制任务、i18next 中英双语、**LDAP 域账号登录**。到这一步，拿去公司内网部署已经没有障碍了。

**9 月 14 日（管理体验）**：暗色主题、Projects 页面、真实的队列页（显示 Agent 派发等待状态）、Agent 配置界面（并发 / 标签 / 环境变量）。

**9 月 15 日（数据模型大迁移）**：我发了一条带 checkbox 的长消息：

> - [ ] 运行不是全局的，应该跟着任务走，且 ID 只在任务内自增，不是全局自增
>   - [ ] 现在地址是 http://127.0.0.1:5000/runs/5，应该为 http://127.0.0.1:5000/runs/dag-demo/5
> - [ ] 配置需要放到任务单独子目录中进行版本控制
> - [ ] 日志也需要放到任务单独子目录按运行 ID 进行存放
> - [ ] 简单说就是不再先类别后任务，而是先任务后类别

Z Code 完成了"任务优先"目录布局的整体迁移：每个任务独立目录、独立仓库、任务内自增编号、`/runs/{任务}/{编号}` 式 URL。这种牵一发动全身的改造，AI 做起来反而干净。

**9 月 16 日（对标审计日）**：这天的起点是一条灵魂拷问：

> 现在全盘检查一下，Jenkins 该有的功能都实现了吗？还差哪些关键与必需的？

Z Code 做了全盘审计，列缺失清单，我排了个优先级，然后这一天产出了两个里程碑提交：一个是**全页面实时化改造**——架构改为每个页面独立 SignalR 连接 + 心跳看门狗（15s ping、指数退避、连挂 5 次自动刷新页面）+ 删掉所有刷新按钮，26 文件 +2567/-1725；另一个是**六项功能补齐**——cron 定时触发、用户 API Token、SCM webhook HMAC-SHA256 验签、Job/Step 超时 kill 进程树、`if: always()` + Step 重试、越权过滤修复，最后一句提交信息写着"**104 个测试全绿**"。

**9 月 17 日（生态集成 + 决战 Unity，下面单独讲）**。

**9 月 18 日（收官打磨）**：凌晨围绕着 xterm.js 终端的"最后一公里"连修七个提交——隐藏自绘滚动条、拦截滚轮、焦点管理、Ctrl+C 复制（还专门兼容了某个 Chrome 复制扩展劫持选区的行为）。晚上以 LDAP 管理界面（配置存库热生效 + 连接测试诊断 + 组映射）收尾，也就是今天的 HEAD。

每日提交分布：13 / 23 / 0（休息）/ 5 / 6 / 7 / 10 / 17 / 9。是的，9 月 12 日一个提交都没有——写 Side project 别硬撑，休息一天效率更高。

## 决战 9·17：Unity 6 打包 Windows 跑通

前面八天，Infinity CI 已经是个"完整的软件"了，但对我而言它还缺终极验证：**它能不能真的把 Unity 6 的包打出来？**

我特意设计了解耦的集成方式：**CI 里没有一行 Unity 专属代码**。Unity 的构建知识（怎么调命令行、输出到哪）全部留在游戏仓库自己的脚本里，CI 只负责调度。9 月 17 日早上 8 点，在 Unity 工程里我用了三条消息让 Z Code 写完整个打包链路：

> Client/Assets/Editor/Builder.cs 直接在其中增加一个 MenuItem 打包 Windows 的方法

> CI\platform\windows\build.py 调用这个 Builder.BuildWindows 方法

> CI\platform\windows\prepare.py 打印所有环境变量

11 分钟，三个文件写完。Unity 侧是一个标准的命令行构建入口：

```csharp
public static class Builder
{
    [MenuItem("Tools/Build Windows")]
    public static void BuildWindows()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled).Select(s => s.path).ToArray();
        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine("Builds/Windows", "UnityCIGame.exe"),
            target = BuildTarget.StandaloneWindows64,
        };
        BuildPipeline.BuildPlayer(options);
    }
}
```

Python 侧的 `build.py` 定位 Unity 6（6000.0.23f1）的安装路径，拉起命令行：

```python
args = [
    str(unity_exe),
    "-batchmode", "-quit", "-nographics",
    "-projectPath", str(CLIENT_DIR),
    "-executeMethod", "Builder.BuildWindows",
    "-logFile", "-",          # 日志走 stdout
]
```

然后 CI 侧，任务配置就只需要这么几行 YAML：

```yaml
name: UnityCIGame
project: Default
jobs:
  build:
    runs_on: agent        # 分发到装了 Unity 的 Windows 打包机
    steps:
      - name: prepare
        command: python CI/platform/windows/prepare.py
      - name: build
        command: python CI/platform/windows/build.py
      - name: deploy
        command: python CI/platform/windows/deploy.py
```

当然，真实世界没有这么顺利。第一次把 Unity 任务真正跑起来就撞了墙：Agent 默认把源码检出到自己的 workspace，但 Unity 工程根本不在 Git 源里，脚本路径全错。我把需求丢给 Z Code：任务要能直接绑定机器上的本地目录。功能上线后第一次实测仍有路径问题，我直接把报错日志贴回去：

> python: can't open file '...\agent-data\workspaces\21-build\CI\platform\windows\prepare.py': No such file or directory
> 路径不对，用的应该是 C:\Users\NETWORM\Work\Projects\20260917-UnityCIGame\CI\platform\windows\prepare.py

半小时内两轮修复，“任务本地目录”功能成型：任务可以直接指向本地路径，Agent 就地执行。这个功能反过来让所有 CI 产品都头疼的“Unity 大工程如何进 CI”变成了伪命题——**工程不动，CI 来就你**。

早上 9 点 01 分，正式产物 `Builds/Windows/UnityCIGame.exe`（约 95 MB）落地——**从 Builder.cs 的第一行代码，到 CI 产出 Windows 包，全程约 1 小时**。紧接着我又提了一条：

> build.py 增加实时日志输出，每一行日志都要 flush

于是 Unity 的海量编译日志通过 `-logFile -` 流式输出，经 Agent 的 gRPC 逐行回传，在 Web 端的 xterm.js 终端里实时滚动、带颜色、带时间戳；Run 结束的瞬间，企业微信里已经收到带状态颜色的构建卡片。9 点 06 分收尾提交，此后这条流水线每天都在跑——第二天的 shader 编译日志还证明它凌晨还在干活。

> 📷 截图位：Unity 打包任务的 Run 详情页——DAG 图 + xterm.js 实时日志

回头看这个集成哲学：Infinity CI 不认识 Unity，未来要接 Godot、Unreal，也只是游戏仓库里换几行脚本的事。CI 引擎专注做好调度、日志、分发、通知这些它该做的事。

## 用 Z Code 写整个软件的九条体会

9 天下来，对"怎么用 AI 写一个完整软件"这件事，我有一些具体的体感：

**1. 第一条消息决定坡度。** 我没有一上来就说"给我写个 CI"，而是先问技术选型，再补充架构级的要求（每个网页独立实时连接）。AI 给出的选型（.NET 10 + React 19 + gRPC + SignalR）我基本照单全收，事后证明全是对的。

**2. 小步提交是最好的护栏。** 90 个提交几乎全是 Conventional Commits 规范的原子提交，这是 Z Code 自带的习惯。每次出问题，我直接把 URL 和日志贴回去（"http://127.0.0.1:5000/runs/3 报错"），它自己定位自己修。git 历史就是调试时的地图。

**3. 用真实产品的截图和链接驱动 UI。** "UI 风格改为这个链接"、"学习这张图片中的任务图风格"——比任何文字描述都精准。第二轮开始，AI 交付的还原度高得惊人。

**4. 放心让 AI 推倒重来。** 第一天上午两次 breaking 重构（合计改 72 个文件），第五天整体数据布局迁移。在 AI 编程里，"重写"的成本曲线被拉平了，架构决策不该再为沉没成本妥协。

**5. 灵魂拷问要人来做。** "Jenkins 该有的功能都实现了吗？"——这一条消息引发的全盘审计比我自己想需求高效十倍。**人负责提出好问题、排优先级，AI 负责穷举和实现。**

**6. 密集反馈循环是质量的关键。** 9 月 18 日凌晨修 xterm 体验，我的消息是这样的节奏："还是存在滚动条" → "依然有" → "已经强制刷新了，你可以自己强制刷新测试一下"。经常几分钟就是一轮反馈。AI 不怕你挑剔，怕的是你不验证。

**7. 一款工具全栈通吃。** 这个项目横跨 C#、TypeScript/React、Python、Protobuf、YAML、Dockerfile、LDAP、SQL——全部出自 Z Code 一款工具。不需要"前端 AI、后端 AI"各来一个。

**8. 测试从第一天就跟着走。** Core 和 Server 都有测试项目，到 9 月 16 日已经有 104 个测试。每次大重构后"测试全绿"是敢单击提交的底气。

**9. 人类的时间花在刀刃上。** 9 天里我真正投入的，是每天几十条消息的产品决策和验收反馈。Z Code 把"实现"这件事的成本降到了"说清楚要什么"。

## 结语

最终交付物：

- **Infinity CI**：9 天、90 个提交、22,212 行净增代码，类 Jenkins / GitHub Actions 的自托管 CI，单 EXE 或 Docker 部署，开源在 [github.com/networm/InfinityCI](https://github.com/networm/InfinityCI)
- **一条真实在用的流水线**：Git 事件 / 手动 / cron 触发 → Server 调度 → Windows 打包机上的 Agent 拉起 Unity 6 命令行打包 → 实时彩色日志回传 → 企业微信收通知

回到开头的问题：个人开发者到底值不值得自己写一个 CI？放在以前，我的答案是不值得。但现在，一个合理的技术选型 + 一款足够强的 AI 编程工具 + 每天下班后零散的反馈时间，9 天就能得到一个完全长在自己需求上的软件——不用伺候插件，不用迁就云服务，打包机插上网线就能加入集群。

这是我用 Z Code 写的第一个完整软件，大概率不是最后一个。
