import type {ReactNode} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import Translate, {translate} from '@docusaurus/Translate';

import styles from './index.module.css';

function TerminalCard({title, children}: {title: string; children: ReactNode}) {
  return (
    <div className={styles.termCard}>
      <div className={styles.termBar}>
        <span className={styles.termDot} style={{background: '#ff5f56'}} />
        <span className={styles.termDot} style={{background: '#ffbd2e'}} />
        <span className={styles.termDot} style={{background: '#27c93f'}} />
        <span className={styles.termTitle}>{title}</span>
      </div>
      <pre className={styles.termBody}>{children}</pre>
    </div>
  );
}

function Hero() {
  const {siteConfig} = useDocusaurusContext();
  return (
    <header className={clsx('hero', styles.heroBanner)}>
      <div className="container">
        <div className={styles.heroInner}>
          <div className={styles.heroText}>
            <span className={styles.heroTag}>⚡ Open Source · Self-Hosted</span>
            <Heading as="h1" className={styles.heroTitle}>
              {siteConfig.title}
            </Heading>
            <p className={styles.heroSubtitle}>
              <Translate id="hero.subtitle">
                类 Jenkins / GitHub Actions 的自托管持续集成服务器
              </Translate>
            </p>
            <p className={styles.heroDesc}>
              <Translate id="hero.desc">
                单二进制 + SQLite 即可起步，YAML 定义工作流，Agent 拉取式分发任务到任意打包机，
                全页面实时刷新，零手动刷新按钮。把 GitHub Actions 的体验搬进你自己的机器。
              </Translate>
            </p>
            <div className={styles.heroButtons}>
              <Link
                className="button button--primary button--lg"
                to="/docs/quickstart">
                <Translate id="hero.cta.start">快速开始</Translate>
              </Link>
              <Link
                className="button button--secondary button--lg"
                href="https://github.com/networm/InfinityCI">
                GitHub
              </Link>
            </div>
            <div className={styles.heroStats}>
              <span>
                <b>.NET 10</b>Server / Agent
              </span>
              <span>
                <b>React 19</b>Web UI
              </span>
              <span>
                <b>gRPC</b>
                <Translate id="hero.stats.grpc">分布式 Agent</Translate>
              </span>
              <span>
                <b>xterm.js</b>
                <Translate id="hero.stats.log">实时日志</Translate>
              </span>
            </div>
          </div>
          <TerminalCard title="Infinity CI · runs/UnityCIGame2/1">
            <span className={styles.tGray}>$ infinity run UnityCIGame2</span>
            {'\n'}
            <span className={styles.tGray}>─ DAG ─────────────────────────────</span>
            {'\n'}
            <span className={styles.tBlue}>  ● start → prepare → build → deploy → ● </span>
            {'\n\n'}
            <span className={styles.tGreen}>09:01:02</span>
            {'  '}
            <span className={styles.tGreen}>✔ prepare</span>
            {'  python CI/platform/windows/prepare.py\n'}
            <span className={styles.tGreen}>09:01:08</span>
            {'  '}
            <span className={styles.tYellow}>▶ build  </span>
            {'  python CI/platform/windows/build.py\n'}
            <span className={styles.tGray}>         Unity 6000.0.23f1 -batchmode -nographics …</span>
            {'\n'}
            <span className={styles.tGreen}>09:14:55</span>
            {'  '}
            <span className={styles.tGreen}>✔ build  </span>
            {'  StandaloneWindows64\n'}
            <span className={styles.tGreen}>09:15:03</span>
            {'  '}
            <span className={styles.tGreen}>✔ deploy </span>
            {'  Builds/Windows/UnityCIGame.exe (95 MB)\n\n'}
            <span className={styles.tGreen}>✔ Run succeeded · </span>
            <span className={styles.tGreen}>
              <Translate id="hero.term.notify">通知已推送企业微信</Translate>
            </span>
          </TerminalCard>
        </div>
      </div>
    </header>
  );
}

const features = [
  {
    icon: '🔀',
    node: <Translate id="feature.dag.title">并行 DAG 调度</Translate>,
    desc: <Translate id="feature.dag.desc">
      needs 声明依赖形成有向无环图，多 Job 并行执行，依赖失败下游自动跳过，页面渲染 Blue Ocean 风格依赖图。
    </Translate>,
  },
  {
    icon: '📡',
    title: '',
    node: <Translate id="feature.agent.title">分布式 Agent</Translate>,
    desc: <Translate id="feature.agent.desc">
      Agent 主动外连 Master（gRPC 双向流），拉取式领任务、心跳租约、断线重连、孤儿任务重排队——打包机无需开放入站端口。
    </Translate>,
  },
  {
    icon: '⚡',
    title: '',
    node: <Translate id="feature.realtime.title">全页面实时</Translate>,
    desc: <Translate id="feature.realtime.desc">
      每个页面独立 SignalR 连接，数据变化实时上屏；心跳看门狗指数回退重连，整个系统没有手动刷新按钮。
    </Translate>,
  },
  {
    icon: '🖥️',
    title: '',
    node: <Translate id="feature.log.title">xterm.js 彩色日志</Translate>,
    desc: <Translate id="feature.log.desc">
      逐行时间戳、JSONL 断点续传、ANSI 全彩渲染、按 Step 下载日志，超时自动 kill 整个进程树。
    </Translate>,
  },
  {
    icon: '🗂️',
    title: '',
    node: <Translate id="feature.config.title">配置即 Git 仓库</Translate>,
    desc: <Translate id="feature.config.desc">
      每个任务的 YAML 配置是独立 Git 仓库：Web 表单保存即提交，天然拥有历史、回滚与 git clone。
    </Translate>,
  },
  {
    icon: '🔔',
    title: '',
    node: <Translate id="feature.trigger.title">多方式触发</Translate>,
    desc: <Translate id="feature.trigger.desc">
      Web 手动、Git push webhook（HMAC 验签 + 分支通配）、PR/MR 触发、cron 定时，commit 状态自动回写 GitHub/GitLab。
    </Translate>,
  },
  {
    icon: '💬',
    title: '',
    node: <Translate id="feature.notify.title">五渠道通知</Translate>,
    desc: <Translate id="feature.notify.desc">
      企业微信、钉钉、Slack、通用 Webhook、邮件 SMTP，按事件过滤，成功失败不同颜色的消息卡片。
    </Translate>,
  },
  {
    icon: '🔐',
    title: '',
    node: <Translate id="feature.auth.title">企业级权限</Translate>,
    desc: <Translate id="feature.auth.desc">
      超管/管理员/普通用户三级 RBAC、按项目可见性过滤、个人 API Token、LDAP 域账号登录与组映射。
    </Translate>,
  },
];

function Features() {
  return (
    <section className={clsx(styles.section, styles.sectionAlt)}>
      <div className="container">
        <Heading as="h2" className={styles.sectionTitle}>
          <Translate id="features.title">为自动化构建而生</Translate>
        </Heading>
        <p className={styles.sectionDesc}>
          <Translate id="features.desc">
            从个人开发者的打包脚本到企业内网的持续集成，一个软件全覆盖
          </Translate>
        </p>
        <div className={styles.featuresGrid}>
          {features.map((f) => (
            <div key={f.icon} className={styles.featureCard}>
              <div className={styles.featureIcon}>{f.icon}</div>
              <h3>{f.node}</h3>
              <p>{f.desc}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function QuickStart() {
  return (
    <section className={styles.section}>
      <div className="container">
        <Heading as="h2" className={styles.sectionTitle}>
          <Translate id="quickstart.title">三分钟上手</Translate>
        </Heading>
        <p className={styles.sectionDesc}>
          <Translate id="quickstart.desc">
            无需数据库、无需安装 Jenkins 插件，两个命令跑起来
          </Translate>
        </p>
        <div className={styles.codeDuo}>
          <TerminalCard title="cmd.exe">
            <span className={styles.tGray}>:: 启动 Server（Web :5000）</span>
            {'\nscripts\\start-server.cmd\n\n'}
            <span className={styles.tGray}>:: 在打包机上启动 Agent</span>
            {'\nscripts\\start-agent.cmd \\\n  --Agent:EnrollToken='}
            <span className={styles.tYellow}>&lt;令牌&gt;</span>
            {' \\\n  --Agent:AgentName=build-1'}
            <span className={styles.codeHint}>
              {'\n\n# 默认账号 admin / admin · 首次启动自动创建'}
            </span>
          </TerminalCard>
          <TerminalCard title="workflow.yml">
            <span className={styles.tBlue}>name</span>: UnityCIGame2
            {'\n'}
            <span className={styles.tBlue}>jobs</span>:
            {'\n  prepare:\n    '}
            <span className={styles.tBlue}>runs_on</span>: agent
            {'\n    '}
            <span className={styles.tBlue}>steps</span>:
            {'\n      - '}
            <span className={styles.tBlue}>name</span>: prepare
            {'\n        '}
            <span className={styles.tBlue}>command</span>: python CI/platform/windows/prepare.py
            {'\n  build:\n    '}
            <span className={styles.tBlue}>needs</span>: [prepare]
            {'\n    '}
            <span className={styles.tBlue}>steps</span>:
            {'\n      - '}
            <span className={styles.tBlue}>name</span>: build
            {'\n        '}
            <span className={styles.tBlue}>command</span>: python CI/platform/windows/build.py
            {'\n  deploy:\n    '}
            <span className={styles.tBlue}>needs</span>: [build]
            {'\n    '}
            <span className={styles.tBlue}>steps</span>:
            {'\n      - '}
            <span className={styles.tBlue}>name</span>: deploy
            {'\n        '}
            <span className={styles.tBlue}>command</span>: python CI/platform/windows/deploy.py
          </TerminalCard>
        </div>
      </div>
    </section>
  );
}

function UnitySection() {
  return (
    <section className={clsx(styles.section, styles.sectionAlt)}>
      <div className="container">
        <Heading as="h2" className={styles.sectionTitle}>
          <Translate id="unity.title">为 Unity 打包而生</Translate>
        </Heading>
        <p className={styles.sectionDesc}>
          <Translate id="unity.desc">
            CI 里没有一行 Unity 专属代码——构建知识留在游戏仓库，引擎只负责调度
          </Translate>
        </p>
        <div className={styles.unityFlow}>
          <div className={styles.unityStep}>
            <code>prepare.py</code>
            <p><Translate id="unity.step1">Agent 自检：打印构建环境变量，确认 UNITY_PATH 与工作目录</Translate></p>
          </div>
          <div className={styles.unityStep}>
            <code>build.py</code>
            <p><Translate id="unity.step2">调起 Unity 6 命令行（-batchmode -executeMethod），日志逐行实时回传 Web 终端</Translate></p>
          </div>
          <div className={styles.unityStep}>
            <code>deploy.py</code>
            <p><Translate id="unity.step3">归档产物、推送企业微信通知，构建结果回写 commit status</Translate></p>
          </div>
        </div>
        <p className={styles.unityNote}>
          <Translate id="unity.note">
            工程不动，CI 来就你：任务可直接绑定本地 Unity 工程目录，无需把几个 GB 的工程塞进 Git。
            从第一行脚本到产出 Windows 包，实测 1 小时跑通。
          </Translate>
        </p>
      </div>
    </section>
  );
}

function Cta() {
  return (
    <section className={styles.cta}>
      <div className="container">
        <Heading as="h2">
          <Translate id="cta.title">把打包交给 Infinity CI</Translate>
        </Heading>
        <p>
          <Translate id="cta.desc">
            单 EXE 或 Docker 部署，SQLite 单文件存储，备份就是拷一个目录
          </Translate>
        </p>
        <div className={styles.heroButtons} style={{justifyContent: 'center'}}>
          <Link className="button button--primary button--lg" to="/docs/quickstart">
            <Translate id="hero.cta.start">快速开始</Translate>
          </Link>
          <Link
            className="button button--secondary button--lg"
            href="https://github.com/networm/InfinityCI">
            <Translate id="cta.github">GitHub 仓库</Translate>
          </Link>
        </div>
      </div>
    </section>
  );
}

export default function Home(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout
      title={translate({id: 'home.title', message: 'Infinity CI — 自托管持续集成服务器'})}
      description={siteConfig.tagline}>
      <Hero />
      <main>
        <Features />
        <QuickStart />
        <UnitySection />
        <Cta />
      </main>
    </Layout>
  );
}
