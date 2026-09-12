import { useEffect, useState } from "react";
import { useNavigate, useParams, useSearch } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { dump as yamlDump, load as yamlLoad } from "js-yaml";

import { api } from "@/lib/api";
import type { EditorJob, EditorScm, EditorStep, EditorWorkflow } from "@/lib/types";

function modelToYaml(workflow: EditorWorkflow): string {
  const doc: Record<string, unknown> = {
    name: workflow.name.trim(),
    project: workflow.project.trim() || "Default",
  };
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
    const jobDoc: Record<string, unknown> = {
      runs_on: job.runsOn || "local",
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
    jobs?: Record<string, { runs_on?: string; steps?: { name?: string; command?: string; shell?: string; continue_on_error?: boolean }[] }>;
  };
  if (!doc || typeof doc !== "object") throw new Error("YAML 内容不是有效的对象");
  if (!doc.name) throw new Error("缺少 name 字段");
  const jobs: EditorJob[] = Object.entries(doc.jobs ?? {}).map(([key, job]) => ({
    key,
    runsOn: job?.runs_on ?? "local",
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
  return { name: doc.name, project: doc.project ?? "Default", scm, jobs };
}

function emptyStep(): EditorStep {
  return { name: "", command: "echo hello", shell: "", continueOnError: false };
}

// The form model covers name/project/scm/jobs only; anything else (workflow
// params, job/step env) would be dropped by a form round-trip, so copies of
// such workflows must stay in YAML mode where the text is preserved verbatim.
function formRepresentable(text: string): boolean {
  let doc: Record<string, unknown>;
  try {
    doc = (yamlLoad(text) ?? {}) as Record<string, unknown>;
  } catch {
    return false;
  }
  const hasEnv = (value: unknown) => Boolean(value && typeof value === "object" && "env" in (value as object));
  if ("params" in doc || hasEnv(doc)) return false;
  const jobs = Object.values((doc.jobs as Record<string, unknown>) ?? {});
  return jobs.every((job) => {
    if (!job || typeof job !== "object" || hasEnv(job)) return false;
    return ((job.steps as unknown[] | undefined) ?? []).every((step) => !hasEnv(step));
  });
}

export function JobEditorPage() {
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
    jobs: [{ key: "build", runsOn: "local", steps: [emptyStep()] }],
  });
  const [mode, setMode] = useState<"form" | "yaml">("form");
  const [yamlText, setYamlText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(isEdit || Boolean(copyFrom));
  const [saving, setSaving] = useState(false);

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

  if (loading) return <div className="text-sm text-[#57606a]">加载中…</div>;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{isEdit ? `编辑任务：${editName}` : "新建任务"}</h1>
        <div className="flex items-center gap-2">
          <div className="flex overflow-hidden rounded-md border border-[#d0d7de] text-xs">
            <button
              type="button"
              onClick={() => mode === "yaml" && switchToForm()}
              className={"px-3 py-1.5 " + (mode === "form" ? "bg-[#eaeef2] font-medium" : "bg-white hover:bg-[#f6f8fa]")}
            >
              表单
            </button>
            <button
              type="button"
              onClick={() => mode === "form" && switchToYaml()}
              className={"px-3 py-1.5 " + (mode === "yaml" ? "bg-[#eaeef2] font-medium" : "bg-white hover:bg-[#f6f8fa]")}
            >
              YAML
            </button>
          </div>
          <button
            type="button"
            onClick={() => void save()}
            disabled={saving}
            className="rounded-md bg-[#2da44e] px-3 py-1.5 text-sm font-medium text-white hover:bg-[#2c974b] disabled:opacity-50"
          >
            {saving ? "保存中…" : "保存"}
          </button>
        </div>
      </div>

      {copyFrom && (
        <div className="rounded-md border border-[#a5b8fc] bg-[#ddf4ff] px-3 py-2 text-sm text-[#0550ae]">
          已从任务「{copyFrom}」拷贝配置，请修改名称后保存。
        </div>
      )}

      {error && (
        <div className="rounded-md border border-[#ffc1bc] bg-[#ffebe9] px-3 py-2 text-sm text-[#cf222e]">{error}</div>
      )}

      {mode === "form" ? (
        <div className="space-y-4">
          <div className="grid gap-3 rounded-md border border-[#d0d7de] bg-white p-4 sm:grid-cols-2">
            <label className="text-sm">
              <span className="mb-1 block font-medium">名称</span>
              <input
                value={workflow.name}
                disabled={isEdit}
                onChange={(e) => setWorkflow({ ...workflow, name: e.target.value })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da] disabled:bg-[#f6f8fa]"
              />
            </label>
            <label className="text-sm">
              <span className="mb-1 block font-medium">项目</span>
              <input
                value={workflow.project}
                onChange={(e) => setWorkflow({ ...workflow, project: e.target.value })}
                className="w-full rounded-md border border-[#d0d7de] px-2.5 py-1.5 text-sm outline-none focus:border-[#0969da]"
              />
            </label>
          </div>

          <div className="rounded-md border border-[#d0d7de] bg-white p-4">
            <div className="mb-2 flex items-center justify-between">
              <span className="text-sm font-medium">Git 源码（SCM，可选）</span>
              <button
                type="button"
                onClick={() => setWorkflow({ ...workflow, scm: workflow.scm ? null : { url: "", branch: "", ref: "", credentials: "" } })}
                className="text-xs text-[#0969da] hover:underline"
              >
                {workflow.scm ? "移除" : "添加"}
              </button>
            </div>
            {workflow.scm && (
              <div className="grid gap-2 sm:grid-cols-4">
                <label className="text-xs sm:col-span-2">
                  <span className="mb-1 block font-medium text-[#57606a]">仓库 URL</span>
                  <input
                    value={workflow.scm.url}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, url: e.target.value } })}
                    placeholder="https://... 或本地路径"
                    className="w-full rounded-md border border-[#d0d7de] px-2 py-1 font-mono text-xs outline-none focus:border-[#0969da]"
                  />
                </label>
                <label className="text-xs">
                  <span className="mb-1 block font-medium text-[#57606a]">分支</span>
                  <input
                    value={workflow.scm.branch}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, branch: e.target.value } })}
                    className="w-full rounded-md border border-[#d0d7de] px-2 py-1 text-xs outline-none focus:border-[#0969da]"
                  />
                </label>
                <label className="text-xs">
                  <span className="mb-1 block font-medium text-[#57606a]">Ref（tag/commit）</span>
                  <input
                    value={workflow.scm.ref}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, ref: e.target.value } })}
                    className="w-full rounded-md border border-[#d0d7de] px-2 py-1 text-xs outline-none focus:border-[#0969da]"
                  />
                </label>
                <label className="text-xs sm:col-span-4">
                  <span className="mb-1 block font-medium text-[#57606a]">凭据名称（Agents 页下方「凭据管理」创建）</span>
                  <input
                    value={workflow.scm.credentials}
                    onChange={(e) => setWorkflow({ ...workflow, scm: { ...workflow.scm!, credentials: e.target.value } })}
                    className="w-full rounded-md border border-[#d0d7de] px-2 py-1 text-xs outline-none focus:border-[#0969da]"
                  />
                </label>
              </div>
            )}
          </div>

          {workflow.jobs.map((job, jobIndex) => (
            <div key={jobIndex} className="rounded-md border border-[#d0d7de] bg-white p-4">
              <div className="mb-3 flex items-center gap-2">
                <label className="text-sm">
                  <span className="mr-2 text-xs font-medium text-[#57606a]">Job Key</span>
                  <input
                    value={job.key}
                    onChange={(e) => {
                      const jobs = [...workflow.jobs];
                      jobs[jobIndex] = { ...job, key: e.target.value };
                      setWorkflow({ ...workflow, jobs });
                    }}
                    className="rounded-md border border-[#d0d7de] px-2 py-1 font-mono text-sm outline-none focus:border-[#0969da]"
                  />
                </label>
                <label className="text-sm">
                  <span className="mr-2 text-xs font-medium text-[#57606a]">运行位置</span>
                  <select
                    value={job.runsOn.startsWith("agent") ? "agent" : "local"}
                    onChange={(e) => {
                      const jobs = [...workflow.jobs];
                      jobs[jobIndex] = { ...job, runsOn: e.target.value };
                      setWorkflow({ ...workflow, jobs });
                    }}
                    className="rounded-md border border-[#d0d7de] px-2 py-1 text-sm outline-none focus:border-[#0969da]"
                  >
                    <option value="local">本地（Server）</option>
                    <option value="agent">Agent</option>
                  </select>
                </label>
                {workflow.jobs.length > 1 && (
                  <button
                    type="button"
                    onClick={() => setWorkflow({ ...workflow, jobs: workflow.jobs.filter((_, i) => i !== jobIndex) })}
                    className="ml-auto flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs text-[#cf222e] hover:bg-[#ffebe9]"
                  >
                    <Trash2 size={12} />
                    删除 Job
                  </button>
                )}
              </div>

              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-[#57606a]">
                    <th className="w-48 pb-1">步骤名</th>
                    <th className="pb-1">命令</th>
                    <th className="w-28 pb-1">Shell</th>
                    <th className="w-10 pb-1">继续</th>
                    <th className="w-10 pb-1" />
                  </tr>
                </thead>
                <tbody>
                  {job.steps.map((step, stepIndex) => (
                    <tr key={stepIndex} className="border-t border-[#eaeef2]">
                      <td className="py-1.5 pr-2">
                        <input
                          value={step.name}
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, name: e.target.value };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                          className="w-full rounded border border-[#d0d7de] px-2 py-1 outline-none focus:border-[#0969da]"
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
                          className="w-full rounded border border-[#d0d7de] px-2 py-1 font-mono text-xs outline-none focus:border-[#0969da]"
                        />
                      </td>
                      <td className="py-1.5 pr-2">
                        <input
                          value={step.shell}
                          placeholder="默认"
                          onChange={(e) => {
                            const steps = [...job.steps];
                            steps[stepIndex] = { ...step, shell: e.target.value };
                            updateJobAt(workflow, setWorkflow, jobIndex, { ...job, steps });
                          }}
                          className="w-full rounded border border-[#d0d7de] px-2 py-1 outline-none focus:border-[#0969da]"
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
                            className="text-[#cf222e] hover:opacity-70"
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
                className="mt-2 flex items-center gap-1 rounded-md border border-[#d0d7de] px-2 py-1 text-xs hover:bg-[#f6f8fa]"
              >
                <Plus size={12} />
                添加步骤
              </button>
            </div>
          ))}

          <button
            type="button"
            onClick={() =>
              setWorkflow({
                ...workflow,
                jobs: [...workflow.jobs, { key: `job${workflow.jobs.length + 1}`, runsOn: "local", steps: [emptyStep()] }],
              })
            }
            className="flex items-center gap-1.5 rounded-md border border-[#d0d7de] bg-white px-3 py-1.5 text-sm hover:bg-[#f6f8fa]"
          >
            <Plus size={14} />
            添加 Job（并行执行）
          </button>
        </div>
      ) : (
        <textarea
          value={yamlText}
          onChange={(e) => setYamlText(e.target.value)}
          spellCheck={false}
          className="h-[60vh] w-full rounded-md border border-[#d0d7de] bg-[#0d1117] p-4 font-mono text-xs text-[#c9d1d9] outline-none focus:border-[#0969da]"
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
