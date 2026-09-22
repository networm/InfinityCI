# Infinity CI 官网

基于 [Docusaurus 3](https://docusaurus.io) 的官方网站：产品 Landing + 中文文档 + 博客，中英双语（`zh` 默认，`en` 位于 `/en/` 路径）。

## 本地开发

```cmd
npm install          # 首次
npm start            # 开发服务器（热更新，中文）
npm start -- --locale en   # 开发英文版
```

## 构建与预览

```cmd
npm run build        # 产出 build/（zh + en 双语静态站点）
npm run serve        # 本地预览构建产物
```

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `docs/` | 中文文档（11 篇：简介/快速开始/工作流语法/触发/Agent/权限/通知/配置仓库/部署/Unity 实战/FAQ） |
| `blog/` | 博客（Markdown + front matter） |
| `src/pages/index.tsx` | 产品首页（Hero / 特性 / 快速开始 / Unity 案例 / CTA） |
| `i18n/en/` | 英文翻译：`code.json`（界面文案）、`docusaurus-plugin-content-docs/current/`（文档）、`docusaurus-theme-classic/`（导航/页脚） |
| `sidebars.ts` | 文档侧边栏分组 |

## 翻译工作流

中文为源语言。界面文案改动后运行 `npm run write-translations -- --locale en` 刷新翻译骨架（已填的翻译会保留），然后补齐 `i18n/en/` 下对应英文。博客暂不翻译（英文站自动回退显示中文）。

## 部署（GitHub Pages 示例）

`docusaurus.config.ts` 已配置 `url: https://networm.github.io`、`baseUrl: '/InfinityCI/'`、`organizationName: networm`、`projectName: InfinityCI`。用官方 GitHub Action（`actions/deploy-pages`）在 `website/` 下执行 `npm run build` 并发布 `build/` 即可；自有域名部署时把 `url` 改为域名、`baseUrl` 改为 `/`。
