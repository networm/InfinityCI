import { useCallback, useEffect, useState } from "react";
import { KeyRound, Plus, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";

import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { ApiTokenInfo } from "@/lib/types";

export function TokensPage() {
  const { t } = useTranslation();
  const [tokens, setTokens] = useState<ApiTokenInfo[]>([]);
  const [name, setName] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  // The plaintext token is returned exactly once — keep it visible until dismissed.
  const [created, setCreated] = useState<{ token: string; curl: string } | null>(null);

  const reload = useCallback(async () => {
    try {
      setTokens(await api.tokens());
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const create = async () => {
    try {
      const result = await api.createToken(name.trim());
      setCreated({ token: result.token, curl: `curl -H "Authorization: Bearer ${result.token}" http://<host>:5000/api/runs` });
      setName("");
      await reload();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const remove = async (id: number) => {
    if (!window.confirm(t("tokens.confirmDelete"))) return;
    try {
      await api.deleteToken(id);
      await reload();
    } catch (e) {
      setMessage(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-xl font-semibold">
          <KeyRound size={18} />
          {t("tokens.title")}
        </h1>
        <p className="mt-0.5 text-xs text-fg-muted">{t("tokens.hint")}</p>
      </div>

      {message && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{message}</div>
      )}

      <div className="flex flex-wrap items-end gap-3 rounded-md border border-line bg-canvas p-4">
        <label className="text-sm">
          <span className="mb-1 block text-xs font-medium text-fg-muted">{t("tokens.nameLabel")}</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={t("tokens.namePlaceholder")}
            className="w-56 rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
          />
        </label>
        <button
          type="button"
          disabled={!name.trim()}
          onClick={() => void create()}
          className="flex items-center gap-1.5 rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
        >
          <Plus size={14} />
          {t("tokens.create")}
        </button>
      </div>

      {created && (
        <div className="rounded-md border border-line bg-canvas p-4">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-sm font-medium text-attention">{t("tokens.copyNow")}</span>
            <button
              type="button"
              onClick={() => void navigator.clipboard.writeText(created.token)}
              className="rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
            >
              {t("common.copy")}
            </button>
          </div>
          <code className="block break-all rounded bg-[#0d1117] p-3 font-mono text-xs text-[#c9d1d9]">{created.token}</code>
          <p className="mt-2 text-xs text-fg-muted">{t("tokens.usageExample")}</p>
          <code className="mt-1 block break-all rounded bg-[#0d1117] p-2 font-mono text-xs text-[#7d8590]">{created.curl}</code>
          <button type="button" onClick={() => setCreated(null)} className="mt-2 text-xs text-link hover:underline">
            {t("tokens.dismiss")}
          </button>
        </div>
      )}

      {tokens.length === 0 ? (
        <div className="rounded-md border border-line bg-canvas p-6 text-sm text-fg-muted">{t("tokens.empty")}</div>
      ) : (
        <div className="overflow-hidden rounded-md border border-line bg-canvas">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-line bg-canvas-subtle text-left text-xs text-fg-muted">
                <th className="px-4 py-2">{t("columns.name")}</th>
                <th className="px-4 py-2">{t("columns.createdAt")}</th>
                <th className="px-4 py-2">{t("tokens.lastUsed")}</th>
                <th className="px-4 py-2 text-right">{t("columns.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {tokens.map((token) => (
                <tr key={token.id} className="border-b border-line-muted last:border-0">
                  <td className="px-4 py-2 font-medium">{token.name}</td>
                  <td className="px-4 py-2 text-fg-muted">{formatDateTime(token.createdUtc)}</td>
                  <td className="px-4 py-2 text-fg-muted">
                    {token.lastUsedUtc ? formatDateTime(token.lastUsedUtc) : t("tokens.neverUsed")}
                  </td>
                  <td className="px-4 py-2 text-right">
                    <button
                      type="button"
                      onClick={() => void remove(token.id)}
                      className="inline-flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
                    >
                      <Trash2 size={12} />
                      {t("common.delete")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
