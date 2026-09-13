import { useCallback, useMemo, useState, type FormEvent } from "react";
import { X } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import type { Run, WorkflowParam } from "@/lib/types";

/** Jenkins-style "build with parameters" dialog. Renders nothing without params. */
export function useTriggerWithParams() {
  const { t } = useTranslation();
  const [workflow, setWorkflow] = useState<{ name: string; params: WorkflowParam[] } | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [onDone, setOnDone] = useState<(run: Run) => void>(() => {});

  const requestTrigger = useCallback(
    (name: string, params: WorkflowParam[], done: (run: Run) => void) => {
      if (params.length === 0) {
        // Parameterless workflow: trigger immediately.
        setBusy(true);
        void api
          .trigger(name)
          .then(done)
          .finally(() => setBusy(false));
        return;
      }
      const defaults = Object.fromEntries(params.map((p) => [p.name, p.default]));
      setValues(defaults);
      setWorkflow({ name, params });
      setOnDone(() => done);
      setError(null);
    },
    [],
  );

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!workflow) return;
    setBusy(true);
    setError(null);
    try {
      const run = await api.trigger(workflow.name, values);
      setWorkflow(null);
      onDone(run);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };

  const dialog = useMemo(() => {
    if (!workflow) return null;
    return (
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40" onClick={() => setWorkflow(null)}>
        <form
          className="w-[420px] rounded-lg border border-line bg-canvas p-5 shadow-xl"
          onClick={(e) => e.stopPropagation()}
          onSubmit={submit}
        >
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-base font-semibold">
              {t("trigger.title")} <span className="font-mono">{workflow.name}</span>
            </h3>
            <button type="button" onClick={() => setWorkflow(null)} className="text-fg-muted hover:text-fg">
              <X size={18} />
            </button>
          </div>
          {workflow.params.map((param) => (
            <label key={param.name} className="mb-3 block text-sm">
              <span className="mb-1 block font-medium">
                {param.name}
                {param.required && <span className="ml-1 text-danger">*</span>}
                {param.description && <span className="ml-2 text-xs text-fg-muted">{param.description}</span>}
              </span>
              <input
                value={values[param.name] ?? ""}
                onChange={(e) => setValues((prev) => ({ ...prev, [param.name]: e.target.value }))}
                className={
                  "w-full rounded-md border px-2.5 py-1.5 font-mono text-sm outline-none focus:border-link " +
                  (param.required && !values[param.name] ? "border-danger" : "border-line")
                }
              />
            </label>
          ))}
          {error && (
            <div className="mb-3 rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{error}</div>
          )}
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setWorkflow(null)}
              className="rounded-md border border-line px-3 py-1.5 text-sm hover:bg-canvas-subtle"
            >
              {t("common.cancel")}
            </button>
            <button
              type="submit"
              disabled={busy || workflow.params.some((p) => p.required && !values[p.name])}
              className="rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
            >
              {busy ? t("trigger.running") : t("common.run")}
            </button>
          </div>
        </form>
      </div>
    );
  }, [workflow, values, error, busy, t]);

  return { requestTrigger, dialog };
}
