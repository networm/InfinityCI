---
title: "Recipe: Unity 6 Windows Builds"
description: Automating Unity 6 Windows packaging with Infinity CI
sidebar_position: 10
---

# Recipe: Unity 6 Windows Builds

This is the scenario Infinity CI was designed around: getting a real Unity 6 Windows packaging pipeline running. Below is a complete, battle-tested configuration.

## Philosophy: zero Unity code in the CI

Infinity CI **contains not a single line of Unity-specific code**. The build knowledge (how to invoke the command line, where output goes) lives entirely in the game repo's own scripts; the CI only orchestrates, dispatches, logs, and notifies. Benefits:

- Switch CI engines and the scripts don't change; switch game engines and the CI doesn't change;
- Scripts can be run locally by hand while debugging — CI is just a different trigger.

## Directory layout

Keep a `CI/` directory in the game repo (or local project):

```text
MyUnityGame/
├── CI/
│   └── platform/
│       └── windows/
│           ├── prepare.py   # environment self-check
│           ├── build.py     # invokes the Unity command line
│           └── deploy.py    # archive / notify
└── Client/                  # Unity 6 project
    └── Assets/Editor/Builder.cs
```

## Step 1: the Unity-side build entry

`Client/Assets/Editor/Builder.cs` — the `-executeMethod` entry point:

```csharp
public static class Builder
{
    [MenuItem("Tools/Build Windows")]   // also triggerable from the editor
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

Key points: the scene list comes from the scenes enabled in Build Settings (nothing hardcoded); the target is `StandaloneWindows64`.

## Step 2: the build script

Core of `CI/platform/windows/build.py` (Python 3, no third-party dependencies):

```python
args = [
    str(unity_exe),
    "-batchmode", "-quit", "-nographics",
    "-projectPath", str(CLIENT_DIR),
    "-executeMethod", "Builder.BuildWindows",
    "-logFile", "-",          # logs go to stdout
]
```

Three battle-tested details are baked into the script:

1. **Locating Unity.exe**: read the `UNITY_PATH` environment variable first; otherwise parse the version from `ProjectSettings/ProjectVersion.txt` (e.g. `6000.0.23f1`) and resolve the Unity Hub default install path;
2. **Real-time logs**: `subprocess.Popen` reads stdout line by line with `flush`, streaming Unity's massive compile log to the CI web terminal live (not dumped at the end);
3. **Artifact double-check**: exit code 0 doesn't mean success — the script also verifies `Builds/Windows/*.exe` actually exists, preventing false greens.

## Step 3: the CI workflow

On the Infinity CI side, a few lines of YAML are all it takes. Chain the three steps with `needs` and the run page renders them as a DAG — when prepare fails, the downstream jobs are skipped automatically:

```yaml
name: UnityCIGame2
project: Default
jobs:
  prepare:
    runs_on: agent        # environment self-check on the Unity build machine
    steps:
      - name: prepare
        command: python CI/platform/windows/prepare.py
  build:
    needs: [prepare]      # starts only after prepare succeeds
    runs_on: local
    steps:
      - name: build
        command: python CI/platform/windows/build.py
  deploy:
    needs: [build]
    runs_on: local
    steps:
      - name: deploy
        command: python CI/platform/windows/deploy.py
```

Bind the task's **local directory** to the project path (e.g. `C:\Users\me\Work\Projects\MyUnityGame` — this applies to both local execution and agents) and everything runs in place — no need to push gigabytes of Unity project into Git.

## What it looks like

- prepare prints every environment variable in the web terminal — confirming `UNITY_PATH` injection at a glance;
- build streams Unity's compile log line by line, with timestamps and ANSI color, errors visible immediately;
- on completion the artifact path and a WeCom notification go out together, and the commit status is written back to the source repo.

## Common pitfalls

| Symptom | Cause & fix |
| --- | --- |
| Agent can't find the scripts | Task isn't bound to a local directory / path mismatch; use the prepare step to print env vars and locate the issue |
| Unity exits 0 but no build | Missing scenes, licensing, etc.; the script's artifact check catches this |
| Chinese log garbling | Infinity CI decodes with the console code page (GBK); make sure custom scripts don't force UTF-8 re-encoding |
| New Input System errors after build | Add `InputSystem_Actions` to PlayerSettings → preloaded assets |
