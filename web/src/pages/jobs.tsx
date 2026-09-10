import { TaskDashboard } from "@/components/task-dashboard";

/**
 * 任务页与首页共用 Jenkins 风格的任务仪表盘；任务配置管理与历史入口
 * 在每个任务的详情页（点击任务名进入）。
 */
export function JobsPage() {
  return <TaskDashboard />;
}
