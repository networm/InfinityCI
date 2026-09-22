---
title: 实战：Unity 6 打包 Windows
description: 用 Infinity CI 自动化 Unity 6 的 Windows 平台打包
sidebar_position: 10
---

# 实战：Unity 6 打包 Windows

这是 Infinity CI 的设计原点：把 Unity 6 的 Windows 打包流水线真正跑起来。本文记录一套经过实战验证的完整配置。

## 设计哲学：CI 里没有 Unity 代码

Infinity CI **不含一行 Unity 专属代码**。Unity 的构建知识（怎么调命令行、输出到哪）全部留在游戏仓库自己的脚本里，CI 只负责调度、分发、日志、通知。好处：

- 换 CI 引擎，脚本不用动；换游戏引擎，CI 不用动；
- 脚本可以在本地手动执行调试，CI 只是换了触发方式。

## 目录结构

游戏仓库（或本地工程目录）里放一个 `CI/` 目录：

```text
MyUnityGame/
├── CI/
│   └── platform/
│       └── windows/
│           ├── prepare.py   # 环境自检
│           ├── build.py     # 调 Unity 命令行打包
│           └── deploy.py    # 归档 / 通知
└── Client/                  # Unity 6 工程
    └── Assets/Editor/Builder.cs
```

## 第一步：Unity 侧构建入口

`Client/Assets/Editor/Builder.cs`——`-executeMethod` 的入口：

```csharp
public static class Builder
{
    [MenuItem("Tools/Build Windows")]   // 编辑器里也能手动触发
    public static void BuildWindows()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        Directory.CreateDirectory("Builds/Windows");

        BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine("Builds/Windows", "UnityCIGame.exe"),
            target = BuildTarget.StandaloneWindows64,
        });
    }
}
```

要点：场景列表取自 Build Settings 里勾选的场景（不硬编码）；目标平台 `StandaloneWindows64`。

## 第二步：打包脚本

`CI/platform/windows/build.py` 核心逻辑（Python 3，无第三方依赖）：

```python
args = [
    str(unity_exe),
    "-batchmode", "-quit", "-nographics",
    "-projectPath", str(CLIENT_DIR),
    "-executeMethod", "Builder.BuildWindows",
    "-logFile", "-",          # 日志输出到 stdout
]
```

三条实战经验直接写进了脚本：

1. **Unity.exe 定位**：优先读环境变量 `UNITY_PATH`；否则解析 `ProjectSettings/ProjectVersion.txt` 的版本号（如 `6000.0.23f1`），拼出 Unity Hub 默认安装路径；
2. **实时日志**：用 `subprocess.Popen` 逐行读取 stdout 并 `flush`，Unity 的海量编译日志实时流到 CI 的 Web 终端（而不是结束后一次性倒出）；
3. **产物双重校验**：退出码为 0 不代表成功——还要确认 `Builds/Windows/*.exe` 真实存在，避免"假绿"。

## 第三步：CI 工作流

Infinity CI 侧只需要几行 YAML：

```yaml
name: UnityCIGame
project: Default
jobs:
  build:
    runs_on: agent          # 派发到装了 Unity 的 Windows 打包机
    steps:
      - name: prepare
        command: python CI/platform/windows/prepare.py
      - name: build
        command: python CI/platform/windows/build.py
      - name: deploy
        command: python CI/platform/windows/deploy.py
```

任务设置里把**本地目录**绑定为打包机上的工程路径（如 `C:\Users\me\Work\Projects\MyUnityGame`），Agent 就地执行，几个 GB 的 Unity 工程无需进 Git。

## 运行效果

- prepare 在 Web 终端打印全部环境变量，一眼确认 `UNITY_PATH` 注入是否成功；
- build 阶段 Unity 编译日志逐行实时滚动，带时间戳与 ANSI 颜色，报错行直接可见；
- 结束后产物路径与企业微信通知同步发出，commit status 回写源码仓库。

## 常见坑

| 现象 | 原因与解法 |
| --- | --- |
| Agent 上找不到脚本 | 任务未绑定本地目录 / 路径大小写不符，用 prepare 步骤打印环境变量定位 |
| Unity 退出码 0 但没出包 | 缺场景、License 问题等；脚本已做产物校验兜底 |
| 日志中文乱码 | Infinity CI 已按控制台代码页（GBK）解码；自定义脚本注意不要强制 UTF-8 重编码 |
| 打包后新 Input System 报错 | 把 `InputSystem_Actions` 加入 PlayerSettings 的 preloaded assets |
