import { useCallback, useEffect, useState } from "react";
import { Copy, Webhook } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import type { WorkflowRuntimeState } from "@/lib/types";

/** Admin automation controls on the task detail page: enable toggle,
/// incoming webhook token, WeCom notify URL. */
export function AutomationCard({ name, onMessage }: { name: string; onMessage: (message: string) => void }) {
  const { t } = useTranslation();
  const [state, setState] = useState<WorkflowRuntimeState | null>(null);
  const [webhookInfo, setWebhookInfo] = useState<{ token: string; url: string; curl: string } | null>(null);
  const [notifyUrl, setNotifyUrl] = useState("");
  const [saving, setSaving] = useState(false);

  const reload = useCallback(async () => {
    try {
      const runtime = await api.workflowState(name);
      setState(runtime);
      setNotifyUrl(runtime.notifyWebhookUrl ?? "");
      setWebhookInfo(
        runtime.webhookToken
          ? {
              token: runtime.webhookToken,
              url: `${location.origin}/api/webhooks/${runtime.webhookToken}`,
              curl: `curl -X POST ${location.origin}/api/webhooks/${runtime.webhookToken} -H "Content-Type: application/json" -d '{"params":{}}'`,
            }
          : null,
      );
    } catch (e) {
      onMessage(e instanceof Error ? e.message : String(e));
    }
  }, [name]);

  useEffect(() => {
    void reload();
  }, [reload]);

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
        <div className="mb-2 text-xs font-medium text-fg-muted">{t("automation.webhookTitle")}</div>
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
                setWebhookInfo({
                  token: created.token,
                  url: created.url,
                  curl: created.curl,
                });
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

      {/* WeCom notify */}
      <div className="rounded-md border border-line-muted p-3">
        <div className="mb-2 text-xs font-medium text-fg-muted">{t("automation.wecomTitle")}</div>
        <div className="flex flex-wrap items-center gap-2">
          <input
            value={notifyUrl}
            onChange={(e) => setNotifyUrl(e.target.value)}
            placeholder="https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=..."
            className="w-96 max-w-full rounded-md border border-line px-2.5 py-1.5 font-mono text-xs outline-none focus:border-link"
          />
          <button
            type="button"
            disabled={saving}
            onClick={async () => {
              setSaving(true);
              try {
                await api.setNotifyWebhook(name, notifyUrl.trim() || null);
                onMessage(notifyUrl.trim() ? t("automation.notifySaved") : t("automation.notifyCleared"));
              } catch (e) {
                onMessage(e instanceof Error ? e.message : String(e));
              } finally {
                setSaving(false);
              }
            }}
            className="rounded-md bg-success-btn px-3 py-1.5 text-xs font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            {t("common.save")}
          </button>
          {notifyUrl && (
            <button
              type="button"
              onClick={async () => {
                try {
                  await api.setNotifyWebhook(name, null);
                  setNotifyUrl("");
                } catch (e) {
                  onMessage(e instanceof Error ? e.message : String(e));
                }
              }}
              className="rounded-md border border-line px-3 py-1.5 text-xs hover:bg-canvas-subtle"
            >
              {t("automation.clear")}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
