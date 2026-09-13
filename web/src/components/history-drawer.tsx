import { useCallback, useEffect, useState } from "react";
import { Undo2, X } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";

interface WorkflowCommit {
  sha: string;
  message: string;
  author: string;
  when: string;
}

export function HistoryDrawer({ name, isAdmin, onClose }: { name: string; isAdmin: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const [commits, setCommits] = useState<WorkflowCommit[] | null>(null);
  const [preview, setPreview] = useState<{ sha: string; yaml: string } | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      setCommits(await api.jobHistory(name));
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }, [name]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return (
    <div className="fixed inset-0 z-40 flex justify-end bg-black/30" onClick={onClose}>
      <div
        className="h-full w-[480px] overflow-auto bg-canvas p-5 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-base font-semibold">{t("history.title", { name })}</h2>
          <button type="button" onClick={onClose} className="text-fg-muted hover:text-fg">
            <X size={18} />
          </button>
        </div>

        {message && (
          <div className="mb-3 rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
        )}

        {commits === null ? (
          <div className="text-sm text-fg-muted">{t("common.loading")}</div>
        ) : commits.length === 0 ? (
          <div className="text-sm text-fg-muted">{t("history.empty")}</div>
        ) : (
          <div className="space-y-2">
            {commits.map((commit) => (
              <div key={commit.sha} className="rounded-md border border-line p-3">
                <div className="flex items-center justify-between gap-2">
                  <div className="min-w-0">
                    <div className="truncate text-sm font-medium">{commit.message}</div>
                    <div className="text-xs text-fg-muted">
                      {commit.author} · {formatDateTime(commit.when)} ·{" "}
                      <code className="font-mono">{commit.sha.slice(0, 7)}</code>
                    </div>
                  </div>
                </div>
                <div className="mt-2 flex gap-2">
                  <button
                    type="button"
                    onClick={async () => {
                      try {
                        const blob = await api.jobBlob(name, commit.sha);
                        setPreview({ sha: commit.sha, yaml: blob.yaml });
                      } catch (e) {
                        setMessage(e instanceof Error ? e.message : String(e));
                      }
                    }}
                    className="rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
                  >
                    {t("history.viewVersion")}
                  </button>
                  {isAdmin && (
                    <button
                      type="button"
                      onClick={async () => {
                        if (!window.confirm(t("history.confirmRestore", { name, sha: commit.sha.slice(0, 7) }))) return;
                        try {
                          await api.restoreJob(name, commit.sha);
                          await reload();
                          setMessage(null);
                          setPreview(null);
                        } catch (e) {
                          setMessage(e instanceof Error ? e.message : String(e));
                        }
                      }}
                      className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-link hover:bg-link-subtle"
                    >
                      <Undo2 size={12} />
                      {t("history.restoreVersion")}
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}

        {preview && (
          <div className="mt-4">
            <div className="mb-1 flex items-center justify-between">
              <span className="text-xs font-medium text-fg-muted">{t("history.previewTitle", { sha: preview.sha.slice(0, 7) })}</span>
              <button type="button" onClick={() => setPreview(null)} className="text-xs text-link hover:underline">
                {t("history.closePreview")}
              </button>
            </div>
            <pre className="max-h-[50vh] overflow-auto rounded-md bg-[#0d1117] p-3 font-mono text-xs text-[#c9d1d9]">
              {preview.yaml}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
}
