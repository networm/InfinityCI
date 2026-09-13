import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { LanguageToggle } from "@/components/language-toggle";
import { ThemeToggle } from "@/components/theme-toggle";
import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";

export function LoginPage() {
  const { refresh } = useMe();
  const { t } = useTranslation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [ldapEnabled, setLdapEnabled] = useState(false);

  useEffect(() => {
    api
      .authConfig()
      .then((config) => setLdapEnabled(config.ldapEnabled))
      .catch(() => setLdapEnabled(false));
  }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.login(username, password);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : t("login.failed"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-canvas-subtle">
      <div className="w-full max-w-[320px] rounded-lg border border-line bg-canvas p-6 shadow-sm">
        <div className="mb-2 flex justify-end gap-1.5">
          <ThemeToggle />
          <LanguageToggle />
        </div>
        <h1 className="mb-1 text-center text-2xl font-semibold tracking-tight">Infinity CI</h1>
        <p className="mb-5 text-center text-sm text-fg-muted">{t("login.prompt")}</p>
        {ldapEnabled && (
          <p className="mb-4 rounded-md border border-link-line bg-link-subtle px-3 py-2 text-center text-xs text-link-fg">
            {t("login.ldapHint")}
          </p>
        )}
        <form onSubmit={submit} className="space-y-4">
          <label className="block text-sm">
            <span className="mb-1 block font-medium">{t("login.username")}</span>
            <input
              autoFocus
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full rounded-md border border-line px-3 py-1.5 text-sm outline-none focus:border-link focus:ring-2 focus:ring-link/30"
            />
          </label>
          <label className="block text-sm">
            <span className="mb-1 block font-medium">{t("login.password")}</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-md border border-line px-3 py-1.5 text-sm outline-none focus:border-link focus:ring-2 focus:ring-link/30"
            />
          </label>
          {error && (
            <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{error}</div>
          )}
          <button
            type="submit"
            disabled={busy || !username || !password}
            className="w-full rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            {busy ? t("login.submitting") : t("login.submit")}
          </button>
        </form>
      </div>
    </div>
  );
}
