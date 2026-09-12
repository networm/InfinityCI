import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";

import { LanguageToggle } from "@/components/language-toggle";
import { api } from "@/lib/api";
import { useMe } from "@/lib/me-context";

export function LoginPage() {
  const { refresh } = useMe();
  const { t } = useTranslation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

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
    <div className="flex min-h-screen items-center justify-center bg-[#f6f8fa]">
      <div className="w-full max-w-[320px] rounded-lg border border-[#d0d7de] bg-white p-6 shadow-sm">
        <div className="mb-2 flex justify-end">
          <LanguageToggle />
        </div>
        <h1 className="mb-1 text-center text-2xl font-semibold tracking-tight">Infinity CI</h1>
        <p className="mb-5 text-center text-sm text-[#57606a]">{t("login.prompt")}</p>
        <form onSubmit={submit} className="space-y-4">
          <label className="block text-sm">
            <span className="mb-1 block font-medium">{t("login.username")}</span>
            <input
              autoFocus
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full rounded-md border border-[#d0d7de] px-3 py-1.5 text-sm outline-none focus:border-[#0969da] focus:ring-2 focus:ring-[#0969da]/30"
            />
          </label>
          <label className="block text-sm">
            <span className="mb-1 block font-medium">{t("login.password")}</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-md border border-[#d0d7de] px-3 py-1.5 text-sm outline-none focus:border-[#0969da] focus:ring-2 focus:ring-[#0969da]/30"
            />
          </label>
          {error && (
            <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{error}</div>
          )}
          <button
            type="submit"
            disabled={busy || !username || !password}
            className="w-full rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
          >
            {busy ? t("login.submitting") : t("login.submit")}
          </button>
        </form>
      </div>
    </div>
  );
}
