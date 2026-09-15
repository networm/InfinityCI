import { useEffect, useState } from "react";
import { useNavigate, useParams, useSearch } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { dump as yamlDump, load as yamlLoad } from "js-yaml";
import { useTranslation } from "react-i18next";
import i18next from "i18next";

import { api } from "@/lib/api";
import type { EditorJob, EditorParam, EditorScm, EditorStep, EditorWorkflow } from "@/lib/types";

function modelToYaml(workflow: EditorWorkflow): string {
  const doc: Record<string, unknown> = {
    name: workflow.name.trim(),
    project: workflow.project.trim() || "Default",
  };
  if (workflow.params.length > 0) {
    const params: Record<string, unknown> = {};
    for (const param of workflow.params) {
      const paramName = param.name.trim();
      if (!paramName) continue;
      const spec: Record<string, unknown> = { default: param.default };
      if (param.required) spec.required = true;
      if (param.description.trim()) spec.description = param.description.trim();
      params[paramName] = spec;
    }
    if (Object.keys(params).length > 0) doc.params = params;
  }
  if (workflow.scm && workflow.scm.url.trim()) {
    const scm: Record<string, unknown> = { url: workflow.scm.url.trim() };
    if (workflow.scm.branch.trim()) scm.branch = workflow.scm.branch.trim();
    if (workflow.scm.ref.trim()) scm.ref = workflow.scm.ref.trim();
    if (workflow.scm.credentials.trim()) scm.credentials = workflow.scm.credentials.trim();
    doc.scm = scm;
  }
  doc.jobs = {} as Record<string, unknown>;
  const jobs = doc.jobs as Record<string, unknown>;
  for (const job of workflow.jobs) {
    const needs = job.needs.map((n) => n.trim()).filter(Boolean);
    const jobDoc: Record<string, unknown> = {
      runs_on: job.runsOn || "local",
      ...(needs.length > 0 ? { needs } : {}),
      steps: job.steps.map((step) => {
        const stepDoc: Record<string, unknown> = { name: step.name.trim() || "step", command: step.command };
        if (step.shell.trim()) stepDoc.shell = step.shell.trim();
        if (step.continueOnError) stepDoc.continue_on_error = true;
        return stepDoc;
      }),
    };
    jobs[job.key.trim()] = jobDoc;
  }
  return yamlDump(doc, { lineWidth: 120, noRefs: true });
}

function yamlToModel(text: string): EditorWorkflow {
  const doc = yamlLoad(text) as {
    name?: string;
    project?: string;
    params?: Record<string, unknown>;
    jobs?: Record<string, { runs_on?: string; needs?: unknown; steps?: { name?: string; command?: string; shell?: string; continue_on_error?: boolean }[] }>;
  };
  if (!doc || typeof doc !== "object") throw new Error(i18next.t("editor.invalidYaml"));
  if (!doc.name) throw new Error(i18next.t("editor.missingName"));
  const params: EditorParam[] = Object.entries(doc.params ?? {}).map(([name, value]) => {
    if (value !== null && typeof value === "object") {
      const spec = value as Record<string, unknown>;
      return {
        name,
        default: spec.default == null ? "" : String(spec.default),
        required: String(spec.required).toLowerCase() === "true",
        description: spec.description == null ? "" : String(spec.description),
      };
    }
    return { name, default: value == null ? "" : String(value), required: false, description: "" };
  });
  const jobs: EditorJob[] = Object.entries(doc.jobs ?? {}).map(([key, job]) => ({
    key,
    runsOn: job?.runs_on ?? "local",
    needs: needsToList(job?.needs),
    steps: (job?.steps ?? []).map((step) => ({
      name: step?.name ?? "",
      command: step?.command ?? "",
      shell: step?.shell ?? "",
      continueOnError: step?.continue_on_error ?? false,
    })),
  }));
  const scmDoc = (doc as { scm?: { url?: string; branch?: string; ref?: string; credentials?: string } }).scm;
  const scm: EditorScm | null = scmDoc
    ? { url: scmDoc.url ?? "", branch: scmDoc.branch ?? "", ref: scmDoc.ref ?? "", credentials: scmDoc.credentials ?? "" }
    : null;
  return { name: doc.name, project: doc.project ?? "Default", scm, params, jobs };
}

function emptyStep(): EditorStep {
  return { name: "", command: "echo hello", shell: "", continueOnError: false };
}

// `needs` accepts both the scalar form (`needs: build`) and the list form.
function needsToList(needs: unknown): string[] {
  if (typeof needs === "string") return needs.trim() ? [needs.trim()] : [];
  if (Array.isArray(needs)) return needs.map((n) => (n == null ? "" : String(n))).filter((n) => n.length > 0);
  return [];
}

// The form model covers name/project/params/scm/jobs; anything else (job/step
// env) would be dropped by a form round-trip, so copies of such workflows must
// stay in YAML mode where the text is preserved verbatim.
function formRepresentable(text: string): boolean {
  let doc: Record<string, unknown>;
  try {
    doc = (yamlLoad(text) ?? {}) as Record<string, unknown>;
  } catch {
    return false;
  }
  const hasEnv = (value: unknown) => Boolean(value && typeof value === "object" && "env" in (value as object));
  if (hasEnv(doc)) return false;
  const jobs = Object.values((doc.jobs as Record<string, unknown>) ?? {});
  return jobs.every((job) => {
    if (!job || typeof job !== "object" || hasEnv(job)) return false;
    const record = job as Record<string, unknown>;
    return ((record.steps as unknown[] | undefined) ?? []).every((step) => !hasEnv(step));
  });
}

export function JobEditorPage() {
  const { t } = useTranslation();
  // strict: false — /jobs/new has no $name param; reading a sibling route's
  // params with `from` throws on non-matching routes.
  const params = useParams({ strict: false }) as { name?: string };
  const editName = params.name;
  const isEdit = Boolean(editName);
  const search = useSearch({ strict: false }) as { from?: string };
  const copyFrom = isEdit ? undefined : search.from;
  const navigate = useNavigate();

  const [workflow, setWorkflow] = useState<EditorWorkflow>({
    name: "",
    project: "Default",
    scm: null,
    params: [],
    jobs: [{ key: "build", runsOn: "local", needs: [], steps: [emptyStep()] }],
  });
  const [mode, setMode] = useState<"form" | "yaml">("form");
  const [yamlText, setYamlText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(isEdit || Boolean(copyFrom));
  const [saving, setSaving] = useState(false);
  const [projectOptions, setProjectOptions] = useState<string[]>(["Default"]);

  useEffect(() => {
    api
      .projects()
      .then((projects) => setProjectOptions(projects.map((p) => p.name)))
      .catch(() => {});
  }, []);

  useEffect(() => {
    const source = isEdit ? editName : copyFrom;
    if (!source) return;
    api
      .jobRaw(source)
      .then((raw) => {
        setYamlText(raw.yaml);
        try {
          setWorkflow(yamlToModel(raw.yaml));
          if (!isEdit && !formRepresentable(raw.yaml)) setMode("yaml");
        } catch (e) {
          setMode("yaml"); // workflow not representable in the form — edit as YAML
        }
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false));
  }, [editName, isEdit, copyFrom]);

  const switchToYaml = () => {
    setYamlText(modelToYaml(workflow));
    setMode("yaml");
  };

  const switchToForm = () => {
    try {
      setWorkflow(yamlToModel(yamlText));
      setMode("form");
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const text = mode === "yaml" ? yamlText : modelToYaml(workflow);
      if (isEdit && editName) {
        await api.updateJob(editName, text);
      } else {
        await api.createJob(text);
      }
      navigate({ to: "/jobs" });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="text-sm text-fg-muted">{t("common.loading")}</div>;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{isEdit ? t("editor.editTitle", { name: editName }) : t("editor.newTitle")}</h1>
        <div className="flex items-center gap-2">
          <div className="flex overflow-hidden rounded-md border border-line text-xs">
            <button
              type="button"
              onClick={() => mode === "yaml" && switchToForm()}
              className={"px-3 py-1.5 " + (mode === "form" ? "bg-chip font-medium" : "bg-canvas hover:bg-canvas-subtle")}
            >
              {t("editor.form")}
            </button>
            <button
              type="button"
              onClick={() => mode === "form" && switchToYaml()}
              className={"px-3 py-1.5 " + (mode === "yaml" ? "bg-chip font-medium" : "bg-canvas hover:bg-canvas-subtle")}
            >
              YAML
            </button>
          </div>
          <button
            type="button"
            onClick={() => void save()}
            disabled={saving}
            className="rounded-md bg-success-btn px-3 py-1.5 text-sm font-medium text-white hover:bg-success-btn-hover disabled:opacity-50"
          >
            {saving ? t("common.saving") : t("common.save")}
          </button>
        </div>
      </div>

      {copyFrom && (
        <div className="rounded-md border border-link-line bg-link-subtle px-3 py-2 text-sm text-link-fg">
          {t("editor.copiedBanner", { name: copyFrom })}
        </div>
      )}

      {error && (
        <div className="rounded-md border border-danger-line bg-danger-subtle px-3 py-2 text-sm text-danger">{error}</div>
      )}

      {mode === "form" ? (
        <div className="space-y-4">
          <div className="grid gap-3 rounded-md border border-line bg-canvas p-4 sm:grid-cols-2">
            <label className="text-sm">
              <span className="mb-1 block font-medium">{t("editor.name")}</span>
              <input
                value={workflow.name}
                disabled={isEdit}
                onChange={(e) => setWorkflow({ ...workflow, name: e.target.value })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link disabled:bg-canvas-subtle"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block font-medium">{t("editor.project")}</span>
              <select
                value={projectOptions.includes(workflow.project) ? workflow.project : ""}
                onChange={(e) => setWorkflow({ ...workflow, project: e.target.value })}
                className="w-full rounded-md border border-line px-2.5 py-1.5 text-sm outline-none focus:border-link"
              >
                {!projectOptions.includes(workflow.project) && workflow.project && (
                  <option value="">{workflow.project}</option>
                )}
                {projectOptions.map((project) => (
                  <option key={project} value={project}>
                    {project}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <div className="rounded-md border border-line bg-canvas p-4">
            <div className="mb-2 flex items-center justify-between">
              <span className="text-sm font-medium">{t("editor.scm")}</span>
              <button
                type="button"
                onClick={() => setWorkflow({ ...workflow, scm: workflow.scm ? null : { url: "", branch: "", ref: "", credentials: "" } })}
                className="text-xs text-link hover:underline"
              >
                {workflow.scm ? t("editor.remove") : t("editor.add")}
              </button>
            </div>
            {workflow.scm && (
              <div className="grid gap-2 sm:grid-cols-4">
                <label className="text-xs sm:col-span-2">
                  <span className="mb-1 block font-medium text-fg-muted">{t("editor.scmUrl")}</span>
                  <input
                    value={workflow.scm.url}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, url: e.target.value } })}
                    placeholder={t("editor.scmUrlPlaceholder")}
                    className="w-full rounded-md border border-line px-2 py-1 font-mono text-xs outline-none focus:border-link"
                  />
                </label>
                <label className="text-xs">
                  <span className="mb-1 block font-medium text-fg-muted">{t("editor.scmBranch")}</span>
                  <input
                    value={workflow.scm.branch}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, branch: e.target.value } })}
                    className="w-full rounded-md border border-line px-2 py-1 text-xs outline-none focus:border-link"
                  />
                </label>
                <label className="text-xs">
                  <span className="mb-1 block font-medium text-fg-muted">{t("editor.scmRef")}</span>
                  <input
                    value={workflow.scm.ref}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, ref: e.target.value } })}
                    className="w-full rounded-md border border-line px-2 py-1 text-xs outline-none focus:border-link"
                  />
                </label>
                <label className="text-xs sm:col-span-4">
                  <span className="mb-1 block font-medium text-fg-muted">{t("editor.scmCredentials")}</span>
                  <input
                    value={workflow.scm.credentials}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, credentials: e.target.value } })}
                    className="w-full rounded-md border border-line px-2 py-1 text-xs outline-none focus:border-link"
                  />
                </label>
              </div>
            )}
          </div>

          <div className="rounded-md border border-line bg-canvas p-4">
            <div className="mb-2 flex items-center justify-between">
              <div>
                <span className="text-sm font-medium">{t("editor.paramsTitle")}</span>
                <p className="mt-0.5 text-xs text-fg-muted">{t("editor.paramsHint")}</p>
              </div>
              <button
                type="button"
                onClick={() =>
                  setWorkflow({
                    ...workflow,
                    params: [...workflow.params, { name: "", default: "", required: false, description: "" }],
                  })
                }
                className="flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs hover:bg-hover"
              >
                <Plus size={12} />
                {t("editor.addParam")}
              </button>
            </div>
            {workflow.params.length === 0 ? (
              <div className="text-xs text-fg-muted">{t("editor.noParams")}</div>
            ) : (
              <div className="space-y-1.5">
                <div className="flex gap-2 text-xs text-fg-muted">
                  <span className="w-40">{t("editor.paramName")}</span>
                  <span className="w-40">{t("editor.paramDefault")}</span>
                  <span className="w-16 text-center">{t("editor.paramRequired")}</span>
                  <span className="flex-1">{t("editor.paramDescription")}</span>
                  <span className="w-5" />
                </div>
                {workflow.params.map((param, paramIndex) => (
                  <div key={paramIndex} className="flex items-center gap-2">
                    <input
                      value={param.name}
                      placeholder="VERSION"
                      onChange={(e) => {
                        const params = [...workflow.params];
                        params[paramIndex] = { ...param, name: e.target.value };
                        setWorkflow({ ...workflow, params });
                      }}
                      className="w-40 rounded-md border border-line px-2 py-1 font-mono text-xs outline-none focus:border-link"
                    />
                    <input
                      value={param.default}
                      onChange={(e) => {
                        const params = [...workflow.params];
                        params[paramIndex] = { ...param, default: e.target.value };
                        setWorkflow({ ...workflow, params });
                      }}
                      className="w-40 rounded-md border border-line px-2 py-1 text-xs outline-none focus:border-link"
                    />
                    <div className="w-16 text-center">
                      <input
                        type="checkbox"
                        checked={param.required}
                        onChange={(e) => {
                          const params = [...workflow.params];
                          params[paramIndex] = { ...param, required: e.target.checked };
                          setWorkflow({ ...workflow, params });
                        }}
                      />
                    </div>
                    <input
                      value={param.description}
                      onChange={(e) => {
                        const params = [...workflow.params];
                        params[paramIndex] = { ...param, description: e.target.value };
                        setWorkflow({ ...workflow, params });
                      }}
                      className="min-w-0 flex-1 rounded-md border border-line px-2 py-1 text-xs outline-none focus:border-link"
                    />
                    <button
                      type="button"
                      onClick={() => setWorkflow({ ...workflow, params: workflow.params.filter((_, i) => i !== paramIndex) })}
                      className="w-5 shrink-0 text-danger hover:opacity-70"
                    >
                      <Trash2 size={13} />
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {workflow.jobs.map((job, jobIndex) => (
            <div key={jobIndex} className="rounded-md border border-line bg-canvas p-4">
              <div className="mb-3 flex items-center gap-2">
                <label className="text-sm">
                  <span className="mr-2 text-xs font-medium text-fg-muted">{t("editor.jobKey")}</span>
                  <input
                    value={job.key}
                    onChange={(e) => {
                      const oldKey = job.key;
                      const newKey = e.target.value;
                      // Renaming a key rewires every `needs` that referenced the old key.
                      const jobs = workflow.jobs.map((j, i) =>
                        i === jobIndex
                          ? { ...j, key: newKey }
                          : { ...j, needs: j.needs.map((n) => (n === oldKey ? newKey : n)) },
                      );
                      setWorkflow({ ...workflow, jobs });
                    }}
                    className="rounded-md border border-line px-2 py-1 font-mono text-sm outline-none focus:border-link"
                  />
                </label>
                <label className="text-sm">
                  <span className="mr-2 text-xs font-medium text-fg-muted">{t("editor.runsOn")}</span>
                  <select
                    value={job.runsOn.startsWith("agent") ? "agent" : "local"}
                    onChange={(e) => {
                      const jobs = [...workflow.jobs];
                      jobs[jobIndex] = { ...job, runsOn: e.target.value };
                      setWorkflow({ ...workflow, jobs });
                    }}
                    className="rounded-md border border-line px-2 py-1 text-sm outline-none focus:border-link"
                  >
                    <option value="local">{t("editor.runsOnLocal")}</option>
                    <option value="agent">{t("editor.runsOnAgent")}</option>
                  </select>
                </label>
                {workflow.jobs.length > 1 && (
                  <button
                    type="button"
                    onClick={() => {
                      const removedKey = job.key;
                      const jobs = workflow.jobs
                        .filter((_, i) => i !== jobIndex)
                        .map((j) => ({ ...j, needs: j.needs.filter((n) => n !== removedKey) }));
                      setWorkflow({ ...workflow, jobs });
                    }}
                    className="ml-auto flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs text-danger hover:bg-danger-subtle"
                  >
                    <Trash2 size={12} />
                    {t("editor.deleteJob")}
                  </button>
                )}
              </div>

              <div className="mb-3 flex flex-wrap items-center gap-1.5">
                <span className="mr-1 text-xs font-medium text-fg-muted">{t("editor.needs")}</span>
                {workflow.jobs.map((other, otherIndex) => {
                  if (otherIndex === jobIndex) return null;
                  const key = other.key.trim();
                  if (!key) return null;
                  const selected = job.needs.includes(key);
                  return (
                    <button
                      key={otherIndex}
                      type="button"
                      aria-pressed={selected}
                      onClick={() => {
                        const needs = selected ? job.needs.filter((n) => n !== key) : [...job.needs, key];
                        updateJobAt(workflow, setWorkflow, jobIndex, { ...job, needs });
                      }}
                      className={
                        "rounded-full border px-2 py-0.5 font-mono text-xs " +
                        (selected
                          ? "border-link-line bg-chip font-medium"
                          : "border-line text-fg-muted hover:bg-canvas-subtle")
                      }
                    >
                      {key}
                    </button>
                  );
                })}
                {job.needs
                  .filter((n) => !workflow.jobs.some((other, i) => i !== jobIndex && other.key.trim() === n))
                  .map((stale) => (
                    <button
                      key={stale}
                      type="button"
                      title={t("editor.needsStale")}
                      onClick={() =>
                        updateJobAt(workflow, setWorkflow, jobIndex, {
                          ...job,
                          needs: job.needs.filter((n) => n !== stale),
                        })
                      }
                      className="rounded-full border border-danger-line px-2 py-0.5 font-mono text-xs text-danger line-through hover:bg-danger-subtle"
                    >
                      {stale}
                    </button>
                  ))}
              </div>

              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-fg-muted">
                    <th className="w-48 pb-1">{t("editor.stepName")}</th>
                    <th className="pb-1">{t("editor.command")}</th>
                    <th className="w-28 pb-1">{t("editor.shell")}</th>
                    <th className="w-10 pb-1">{t("editor.continue")}</th>
                    <th className="w-10 pb-1" />
                  </tr>
                </thead>
                <tbody>
                  {job.steps.map((step, stepIndex) => (
                    <tr key={stepIndex} className="border-t border-line-muted">
                      <td className="py-1.5 pr-2">
                        <input
                          value={step.name}
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, name: e.target.value };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                          className="w-full rounded border border-line px-2 py-1 outline-none focus:border-link"
                        />
                      </td>
                      <td className="py-1.5 pr-2">
                        <input
                          value={step.command}
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, command: e.target.value };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                          className="w-full rounded border border-line px-2 py-1 font-mono text-xs outline-none focus:border-link"
                        />
                      </td>
                      <td className="py-1.5 pr-2">
                        <input
                          value={step.shell}
                          placeholder={t("editor.shellDefaultPlaceholder")}
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, shell: e.target.value };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                          className="w-full rounded border border-line px-2 py-1 outline-none focus:border-link"
                        />
                      </td>
                      <td className="py-1.5 pr-2 text-center">
                        <input
                          type="checkbox"
                          checked={step.continueOnError}
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, continueOnError: e.target.checked };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                        />
                      </td>
                      <td className="py-1.5 text-right">
                        {job.steps.length > 1 && (
                          <button
                            type="button"
                            onClick={() => {
                              const steps = job.steps.filter((_, i) => i !== stepIndex);
                              updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                            }}
                            className="text-danger hover:opacity-70"
                          >
                            <Trash2 size={13} />
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <button
                type="button"
                onClick={() => {
                  const steps = [...job.steps, emptyStep()];
                  updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                }}
                className="mt-2 flex items-center gap-1 rounded-md border border-line px-2 py-1 text-xs hover:bg-canvas-subtle"
              >
                <Plus size={12} />
                {t("editor.addStep")}
              </button>
            </div>
          ))}

          <button
            type="button"
            onClick={() =>
              setWorkflow({
                ...workflow,
                jobs: [...workflow.jobs, { key: `job${workflow.jobs.length + 1}`, runsOn: "local", needs: [], steps: [emptyStep()] }],
              })
            }
            className="flex items-center gap-1.5 rounded-md border border-line bg-canvas px-3 py-1.5 text-sm hover:bg-canvas-subtle"
          >
            <Plus size={14} />
            {t("editor.addJob")}
          </button>
        </div>
      ) : (
        <textarea
          value={yamlText}
          onChange={(e) => setYamlText(e.target.value)}
          spellCheck={false}
          className="h-[60vh] w-full rounded-md border border-line bg-[#0d1117] p-4 font-mono text-xs text-[#c9d1d9] outline-none focus:border-link"
        />
      )}
    </div>
  );
}

function updateJobAt(
  workflow: EditorWorkflow,
  setWorkflow: (next: EditorWorkflow) => void,
  index: number,
  job: EditorJob,
) {
  const jobs = [...workflow.jobs];
  jobs[index] = job;
  setWorkflow({ ...workflow, jobs });
}
