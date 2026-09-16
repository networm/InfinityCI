import { useCallback, useEffect, useState } from "react";
import { Copy, Plus, Trash2, Webhook } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import type { NotifyChannel, WorkflowRuntimeState } from "@/lib/types";

const CHANNEL_TYPES = ["wecom", "dingtalk", "slack", "webhook", "email"] as const;

const TARGET_PLACEHOLDERS: Record<string, string> = {
  wecom: "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=...",
  dingtalk: "https://oapi.dingtalk.com/robot/send?access_token=...",
  slack: "https://hooks.slack.com/services/...",
  webhook: "https://your-system.example/ci-hook",
  email: "ops@example.com",
};

/** Admin automation controls on the task detail page: enable toggle,
/// incoming webhook (token + trigger events), notification channels. */
export function AutomationCard({ name, onMessage }: { name: string; onMessage: (message: string) => void }) {
  const { t } = useTranslation();
  const [state, setState] = useState<WorkflowRuntimeState | null>(null);
  const [webhookInfo, setWebhookInfo] = useState<{ token: string; url: string; curl: string } | null>(null);
  const [channels, setChannels] = useState<NotifyChannel[]>([]);
  const [savingChannels, setSavingChannels] = useState(false);

  const reload = useCallback(async () => {
    try {
      const runtime = await api.workflowState(name);
      setState(runtime);
      setWebhookInfo(
        runtime.webhookToken
          ? {
              token: runtime.webhookToken,
              url: `${location.origin}/api/webhooks/${runtime.webhookToken}`,
              curl: `curl -X POST ${location.origin}/api/webhooks/${runtime.webhookToken} -H "Content-Type: application/json" -d '{"params":{}}'`,
            }
          : null,
      );
      const response = await api.notifyChannels(name);
      setChannels(response.channels);
    } catch (e) {
      onMessage(e instanceof Error ? e.message : String(e));
    }
  }, [name]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const enabledKinds = new Set(
    (state?.webhookEvents ?? "push")
      .split(",")
      .map((k) => k.trim())
      .filter(Boolean),
  );

  const toggleEvent = async (kind: "push" | "pr") => {
    if (!state) return;
    const next = new Set(enabledKinds);
    if (next.has(kind)) next.delete(kind);
    else next.add(kind);
    try {
      await api.setWebhookConfig(name, { events: [...next].join(",") });
      await reload();
    } catch (e) {
      onMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const updateChannel = (index: number, patch: Partial<NotifyChannel>) =>
    setChannels((prev) => prev.map((c, i) => (i === index ? { ...c, ...patch } : c)));

  const saveChannels = async () => {
    setSavingChannels(true);
    try {
      const response = await api.setNotifyChannels(name, channels);
      setChannels(response.channels);
      onMessage(t("automation.notifySaved"));
    } catch (e) {
      onMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setSavingChannels(false);
    }
  };

  return (
    <div className="rounded-md border border-line bg-canvas p-4">
      <h2 className="mb-3 flex items-center gap-1.5 text-sm font-medium text-fg-muted">
        <Webhook size={13} />
        {t("automation.title")}
      </h2>

      {/* enable/disable */}
      <div className="mb-4 flex items-center gap-3">
        <span className="text-sm">{t("automation.workflowStatus")}</span>
        {state && (
          <>
            <span className={state.enabled ? "rounded bg-success-subtle px-2 py-0.5 text-xs text-success" : "rounded bg-chip px-2 py-0.5 text-xs text-fg-muted"}>
              {state.enabled ? t("common.enabled") : t("common.disabled")}
            </span>
            <button
              type="button"
              onClick={async () => {
                try {
                  await api.setWorkflowEnabled(name, !state.enabled);
                  await reload();
                } catch (e) {
                  onMessage(e instanceof Error ? e.message : String(e));
                }
              }}
              className="rounded-md border border-line px-2.5 py-1 text-xs hover:bg-hover"
            >
              {state.enabled ? t("automation.disable") : t("automation.enable")}
            </button>
          </>
        )}
      </div>

      {/* incoming webhook */}
      <div className="mb-4 rounded-md border border-line-muted p-3">
        <div className="mb-2 flex items-center justify-between">
          <span className="text-xs font-medium text-fg-muted">{t("automation.webhookTitle")}</span>
          <div className="flex items-center gap-3 text-xs">
            <span className="text-fg-muted">{t("automation.triggerEvents")}</span>
            {(["push", "pr"] as const).map((kind) => (
              <label key={kind} className="flex cursor-pointer items-center gap-1">
                <input
                  type="checkbox"
                  checked={enabledKinds.has(kind)}
                  onChange={() => void toggleEvent(kind)}
                  className="accent-[var(--link)]"
                />
                {t(`automation.event_${kind}`)}
              </label>
            ))}
          </div>
        </div>
        {webhookInfo ? (
          <div className="space-y-2">
            <div className="overflow-x-auto rounded-md bg-[#0d1117] p-2.5 font-mono text-xs text-[#c9d1d9]">
              <div className="mb-1 flex items-center justify-between">
                <span className="text-[#7d8590]">{t("automation.callExample")}</span>
                <button
                  type="button"
                  onClick={() => void navigator.clipboard.writeText(webhookInfo.curl)}
                  className="flex items-center gap-1 hover:text-white"
                >
                  <Copy size={11} />
                  {t("common.copy")}
                </button>
              </div>
              {webhookInfo.curl}
            </div>
            <button
              type="button"
              onClick={async () => {
                if (!window.confirm(t("automation.confirmRevoke"))) return;
                try {
                  await api.revokeWebhookToken(name);
                  await reload();
                } catch (e) {
                  onMessage(e instanceof Error ? e.message : String(e));
                }
              }}
              className="rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
            >
              {t("automation.revokeToken")}
            </button>
          </div>
        ) : (
          <button
            type="button"
            onClick={async () => {
              try {
                const created = await api.issueWebhookToken(name);
                setWebhookInfo({ token: created.token, url: created.url, curl: created.curl });
              } catch (e) {
                onMessage(e instanceof Error ? e.message : String(e));
              }
            }}
            className="rounded-md border border-line px-2.5 py-1.5 text-xs hover:bg-hover"
          >
            {t("automation.issueToken")}
          </button>
        )}
      </div>

      {/* notification channels */}
      <div className="rounded-md border border-line-muted p-3">
        <div className="mb-2 text-xs font-medium text-fg-muted">{t("automation.notifyTitle")}</div>
        {channels.length > 0 && (
          <div className="mb-2 space-y-2">
            {channels.map((channel, index) => (
              <div key={index} className="flex flex-wrap items-center gap-2">
                <select
                  value={channel.type}
                  onChange={(e) => updateChannel(index, { type: e.target.value })}
                  className="rounded-md border border-line px-2 py-1.5 text-xs outline-none focus:border-link"
                >
                  {CHANNEL_TYPES.map((type) => (
                    <option key={type} value={type}>
                      {t(`automation.channel_${type}`)}
                    </option>
                  ))}
                </select>
                <input
                  value={channel.target}
                  onChange={(e) => updateChannel(index, { target: e.target.value })}
                  placeholder={TARGET_PLACEHOLDERS[channel.type] ?? ""}
                  className="min-w-0 flex-1 rounded-md border border-line px-2.5 py-1.5 font-mono text-xs outline-none focus:border-link"
                />
                <select
                  value={channel.events}
                  onChange={(e) => updateChannel(index, { events: e.target.value as NotifyChannel["events"] })}
                  className="rounded-md border border-line px-2 py-1.5 text-xs outline-none focus:border-link"
                >
                  <option value="always">{t("automation.channelAlways")}</option>
                  <option value="failure">{t("automation.channelFailure")}</option>
                </select>
                <button
                  type="button"
                  onClick={() => setChannels((prev) => prev.filter((_, i) => i !== index))}
                  title={t("common.delete")}
                  className="rounded-md border border-line p-1.5 text-danger hover:bg-danger-subtle"
                >
                  <Trash2 size={12} />
                </button>
              </div>
            ))}
          </div>
        )}
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setChannels((prev) => [...prev, { type: "wecom", target: "", events: "always" }])}
            className="flex items-center gap-1 rounded-md border border-line px-2.5 py-1.5 text-xs hover:bg-hover"
          >
            <Plus size={12} />
            {t("automation.channelAdd")}
          </button>
          <button
            type="button"
            disabled={savingChannels}
            onClick={() => void saveChannels()}
            className="rounded-md bg-success-btn px-3 py-1.5 text-xs font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            {t("common.save")}
          </button>
        </div>
      </div>
    </div>
  );
}
