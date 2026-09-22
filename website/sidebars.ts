import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docs: [
    'intro',
    'quickstart',
    {
      type: 'category',
      label: '核心概念',
      items: ['workflow', 'triggers', 'agents'],
    },
    {
      type: 'category',
      label: '配置与管理',
      items: ['auth', 'notifications', 'config-repo'],
    },
    {
      type: 'category',
      label: '运维与实战',
      items: ['deployment', 'unity', 'faq'],
    },
  ],
};

export default sidebars;
