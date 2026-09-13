import { useState } from "react";
import { Plus, Trash2, X } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import type { EnrolledAgent } from "@/lib/types";

/** Editable agent config: max concurrent builds, labels and agent-wide env vars. */
export function AgentConfigDialog({
  agent,
  onClose,
  onSaved,
  onMessage,
}: {
  agent: EnrolledAgent;
  onClose: () => void;
  onSaved: () => void;
  onMessage: (message: string) => void;
}) {
  const { t } = useTranslation();
  const [max, setMax] = useState(agent.maxConcurrentBuilds);
  const [labels, setLabels] = useState(agent.labels.join(", "));
  const [envRows, setEnvRows] = useState<{ key: string; value: string }[]>(
    Object.entries(agent.environment ?? {}).map(([key, value]) => ({ key, value })),
  );
  const [saving, setSaving] = useState(false);

  const save = async () => {
    setSaving(true);
    try {
      const env = Object.fromEntries(
        envRows.filter((row) => row.key.trim()).map((row) => [row.key.trim(), row.value]),
      );
      const parsedLabels = labels
        .split(",")
        .map((label) => label.trim())
        .filter(Boolean);
      await api.updateAgentConfig(agent.id, { maxConcurrentBuilds: Math.max(1, max), labels: parsedLabels, env });
      onMessage(t("agents.configSaved"));
      onSaved();
      onClose();
    } catch (e) {
      onMessage(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40" onClick={onClose}>
      <div
        className="w-[480px] rounded-lg border border-line bg-canvas p-5 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-4 flex items-center justify-between">
          <h3 className="text-base font-semibold">
            {t("agents.configTitle")} <span className="font-mono text-sm">{agent.name}</span>
          </h3>
          <button type="button" onClick={onClose} className="text-fg-muted hover:text-fg">
            <X size={18} />
          </button>
        </div>

        <div className="mb-4 flex gap-3">
          <label className="w-28 text-sm">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("agents.maxConcurrent")}</span>
            <input
              type="number"
              min={1}
              value={max}
              onChange={(e) => setMax(Number(e.target.value) || 1)}
              className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
            />
          </label>
          <label className="flex-1 text-sm">
            <span className="mb-1 block text-xs font-medium text-fg-muted">{t("agents.labelsHint")}</span>
            <input
              value={labels}
              onChange={(e) => setLabels(e.target.value)}
              placeholder="docker, linux"
              className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
            />
          </label>
        </div>

        <div className="mb-1 flex items-center justify-between">
          <span className="text-xs font-medium text-fg-muted">{t("agents.envVars")}</span>
          <button
            type="button"
            onClick={() => setEnvRows([...envRows, { key: "", value: "" }])}
            className="flex items-center gap-1 text-xs text-link hover:underline"
          >
            <Plus size={12} />
            {t("editor.add")}
          </button>
        </div>
        <p className="mb-2 text-xs text-fg-muted">{t("agents.envVarsHint")}</p>
        <div className="max-h-56 space-y-1.5 overflow-auto pr-1">
          {envRows.length === 0 && <div className="text-xs text-fg-muted">{t("agents.noEnvVars")}</div>}
          {envRows.map((row, index) => (
            <div key={index} className="flex items-center gap-1.5">
              <input
                value={row.key}
                placeholder="KEY"
                onChange={(e) => {
                  const rows = [...envRows];
                  rows[index] = { ...row, key: e.target.value };
                  setEnvRows(rows);
                }}
                className="w-2/5 rounded-md border border-line px-2 py-1 font-mono text-xs outline-none focus:border-link"
              />
              <input
                value={row.value}
                placeholder="value"
                onChange={(e) => {
                  const rows = [...envRows];
                  rows[index] = { ...row, value: e.target.value };
                  setEnvRows(rows);
                }}
                className="min-w-0 flex-1 rounded-md border border-line px-2 py-1 font-mono text-xs outline-none focus:border-link"
              />
              <button
                type="button"
                onClick={() => setEnvRows(envRows.filter((_, i) => i !== index))}
                className="shrink-0 text-danger hover:opacity-70"
              >
                <Trash2 size={13} />
              </button>
            </div>
          ))}
        </div>

        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-line px-3 py-1.5 text-sm hover:bg-canvas-subtle"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={saving}
            onClick={() => void save()}
            className="rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      </div>
    </div>
  );
}
