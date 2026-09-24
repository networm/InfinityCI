import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'Infinity CI',
  tagline: '类 Jenkins / GitHub Actions 的自托管持续集成服务器',
  favicon: 'img/favicon.ico',

  url: 'https://infinityci.github.io',
  baseUrl: '/',

  organizationName: 'networm',
  projectName: 'InfinityCI',

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'zh',
    locales: ['zh', 'en'],
    localeConfigs: {
      zh: {label: '中文', htmlLang: 'zh-Hans'},
      en: {label: 'English', htmlLang: 'en'},
    },
  },

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
        },
        blog: {
          showReadingTime: true,
          feedOptions: {
            type: ['rss', 'atom'],
            xslt: true,
          },
          onInlineTags: 'warn',
          onInlineAuthors: 'warn',
          onUntruncatedBlogPosts: 'warn',
        },
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themes: ['@easyops-cn/docusaurus-search-local'],

  themeConfig: {
    image: 'img/social-card.jpg',
    colorMode: {
      defaultMode: 'dark',
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'Infinity CI',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docs',
          position: 'left',
          label: '文档',
        },
        {
          to: '/blog',
          label: '博客',
          position: 'left',
        },
        {
          type: 'localeDropdown',
          position: 'right',
        },
        {
          href: 'https://github.com/networm/InfinityCI',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: '文档',
          items: [
            {
              label: '快速开始',
              to: '/docs/quickstart',
            },
            {
              label: '工作流语法',
              to: '/docs/workflow',
            },
            {
              label: 'Agent 部署',
              to: '/docs/agents',
            },
          ],
        },
        {
          title: '更多',
          items: [
            {label: '博客', to: '/blog'},
            {
              label: 'GitHub',
              href: 'https://github.com/networm/InfinityCI',
            },
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Infinity CI. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['yaml', 'python', 'csharp', 'bash', 'docker'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
