using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace InfinityCI.Core;

public sealed class WorkflowYamlException : Exception
{
    public WorkflowYamlException(string message) : base(message) { }
    public WorkflowYamlException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Parses workflow YAML (GitHub Actions-like) into <see cref="Workflow"/>.
/// The legacy single-job form (`steps:` at the top level) is wrapped into one job named "default".</summary>
public static class WorkflowYaml
{
    public static Workflow Parse(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        WorkflowYamlDto? dto;
        try
        {
            dto = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<WorkflowYamlDto?>(yaml);
        }
        catch (YamlException e)
        {
            throw new WorkflowYamlException(Msg.T($"Invalid YAML: {e.Message}", $"无效的 YAML：{e.Message}"), e);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            throw new WorkflowYamlException(Msg.T("Workflow 'name' is required.", "缺少 name 字段。"));

        Dictionary<string, WorkflowJob> jobs;
        if (dto.Jobs is { Count: > 0 })
        {
            jobs = new Dictionary<string, WorkflowJob>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, jobDto) in dto.Jobs)
            {
                if (jobDto is null)
                    throw new WorkflowYamlException(Msg.T($"Job '{key}' is empty.", $"任务「{key}」为空。"));
                jobs[key] = ToJob(jobDto, $"Job '{key}'", $"任务「{key}」");
            }
            ValidateNeeds(jobs);
        }
        else if (dto.Steps is { Count: > 0 })
        {
            // Legacy single-job form.
            jobs = new Dictionary<string, WorkflowJob>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = ToJob(new JobDto
                {
                    RunsOn = dto.RunsOn,
                    Env = dto.Env,
                    Steps = dto.Steps,
                }, "Workflow", "任务"),
            };
        }
        else
        {
            throw new WorkflowYamlException(Msg.T(
                $"Workflow '{dto.Name}' must define 'jobs' (or legacy top-level 'steps').",
                $"任务「{dto.Name}」必须定义 jobs（或旧版顶层 steps）。"));
        }

        var paramList = ParseParams(dto.Params);

        var schedules = ParseSchedules(dto.Schedule);

        return new Workflow
        {
            Name = dto.Name.Trim(),
            Project = string.IsNullOrWhiteSpace(dto.Project) ? "Default" : dto.Project.Trim(),
            Params = paramList,
            Schedules = schedules,
            Scm = dto.Scm is null ? null : new ScmConfig
            {
                Url = dto.Scm.Url?.Trim() ?? "",
                Branch = string.IsNullOrWhiteSpace(dto.Scm.Branch) ? null : dto.Scm.Branch.Trim(),
                Ref = string.IsNullOrWhiteSpace(dto.Scm.Ref) ? null : dto.Scm.Ref.Trim(),
                Credentials = string.IsNullOrWhiteSpace(dto.Scm.Credentials) ? null : dto.Scm.Credentials.Trim(),
                CommitStatus = dto.Scm.CommitStatus == true,
            },
            Jobs = jobs,
        };
    }

    /// <summary>`schedule:` accepts one cron string or a list of them; each is validated eagerly.</summary>
    private static IReadOnlyList<string> ParseSchedules(object? raw)
    {
        var items = raw switch
        {
            null => [],
            string one => [one],
            List<object> many => many.Select(v => v?.ToString() ?? "").ToList(),
            _ => throw new WorkflowYamlException(Msg.T(
                "'schedule' must be a cron string or a list of cron strings.",
                "schedule 必须是 cron 字符串或字符串列表。")),
        };
        foreach (var item in items)
        {
            try
            {
                CronSchedule.Parse(item);
            }
            catch (CronFormatException ex)
            {
                throw new WorkflowYamlException(Msg.T(ex.Message, ex.Message), ex);
            }
        }
        return items;
    }

    private static WorkflowJob ToJob(JobDto dto, string contextEn, string contextZh)
    {
        if (dto.Steps is null || dto.Steps.Count == 0)
            throw new WorkflowYamlException(Msg.T(
                $"{contextEn} must define at least one step.",
                $"{contextZh} 至少需要定义一个步骤。"));

        if (dto.If is { } condition && WorkflowJob.NormalizeIf(condition) is not ("always" or ""))
            throw new WorkflowYamlException(Msg.T(
                $"{contextEn} has unsupported 'if: {condition}' (only 'always()' is supported).",
                $"{contextZh} 使用了不支持的 if: {condition}（仅支持 always()）。"));

        var steps = new List<JobStep>(dto.Steps.Count);
        for (var i = 0; i < dto.Steps.Count; i++)
        {
            var s = dto.Steps[i];
            if (s is null || string.IsNullOrWhiteSpace(s.Command))
                throw new WorkflowYamlException(Msg.T(
                    $"{contextEn} step {i + 1} is missing 'command'.",
                    $"{contextZh} 的步骤 {i + 1} 缺少 'command'。"));
            if (s.Retry < 0)
                throw new WorkflowYamlException(Msg.T(
                    $"{contextEn} step {i + 1} has a negative 'retry'.",
                    $"{contextZh} 的步骤 {i + 1} 的 retry 不能为负数。"));
            steps.Add(new JobStep
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"step {i + 1}" : s.Name.Trim(),
                Command = s.Command.Trim(),
                Shell = string.IsNullOrWhiteSpace(s.Shell) ? null : s.Shell.Trim(),
                ContinueOnError = s.ContinueOnError,
                Environment = s.Env ?? new Dictionary<string, string>(),
                Retry = s.Retry,
                Timeout = ParseTimeout(s.TimeoutMinutes, s.TimeoutSeconds, $"{contextEn} step {i + 1}", $"{contextZh} 的步骤 {i + 1}"),
            });
        }

        return new WorkflowJob
        {
            RunsOn = string.IsNullOrWhiteSpace(dto.RunsOn) ? "local" : dto.RunsOn.Trim(),
            Needs = dto.Needs ?? [],
            Environment = dto.Env ?? new Dictionary<string, string>(),
            Steps = steps,
            If = string.IsNullOrWhiteSpace(dto.If) ? null : dto.If.Trim(),
            Timeout = ParseTimeout(dto.TimeoutMinutes, dto.TimeoutSeconds, contextEn, contextZh),
        };
    }

    private static TimeSpan? ParseTimeout(int? minutes, int? seconds, string contextEn, string contextZh)
    {
        if ((minutes.HasValue && minutes.Value < 0) || (seconds.HasValue && seconds.Value < 0))
            throw new WorkflowYamlException(Msg.T(
                $"{contextEn} has a negative 'timeout_minutes'/'timeout_seconds'.",
                $"{contextZh} 的 timeout_minutes/timeout_seconds 不能为负数。"));
        var totalSeconds = (minutes ?? 0) * 60 + (seconds ?? 0);
        return totalSeconds > 0 ? TimeSpan.FromSeconds(totalSeconds) : null;
    }

    /// <summary>Needs must reference existing jobs and must not form cycles.</summary>
    private static void ValidateNeeds(Dictionary<string, WorkflowJob> jobs)
    {
        foreach (var (key, job) in jobs)
        {
            foreach (var need in job.Needs)
            {
                if (need.Equals(key, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowYamlException(Msg.T($"Job '{key}' cannot depend on itself.", $"任务「{key}」不能依赖自身。"));
                if (!jobs.ContainsKey(need))
                    throw new WorkflowYamlException(Msg.T($"Job '{key}' needs unknown job '{need}'.", $"任务「{key}」依赖了不存在的任务「{need}」。"));
            }
        }

        const int Unvisited = 0, Visiting = 1, Done = 2;
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in jobs.Keys)
        {
            Visit(key);
        }

        void Visit(string key)
        {
            switch (state.GetValueOrDefault(key))
            {
                case Visiting:
                    throw new WorkflowYamlException(Msg.T(
                        $"Job dependency cycle detected involving '{key}'.",
                        $"检测到任务依赖循环，涉及「{key}」。"));
                case Done:
                    return;
            }
            state[key] = Visiting;
            foreach (var need in jobs[key].Needs)
                Visit(need);
            state[key] = Done;
        }
    }

    /// <summary>Params support "NAME: default" and the full object form; names must be valid env-var names.</summary>
    private static List<WorkflowParam> ParseParams(Dictionary<string, object>? raw)
    {
        var result = new List<WorkflowParam>();
        if (raw is null)
            return result;

        foreach (var (name, value) in raw)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                throw new WorkflowYamlException(Msg.T(
                    $"Parameter name '{name}' is invalid; use letters, digits and underscores only.",
                    $"参数名「{name}」无效，只能使用字母、数字和下划线。"));

            WorkflowParam param;
            if (value is string defaultValue)
            {
                param = new WorkflowParam { Name = name, Default = defaultValue };
            }
            else if (value is Dictionary<object, object> spec)
            {
                string? Get(object key) => spec.TryGetValue(key, out var v) ? v?.ToString() : null;
                param = new WorkflowParam
                {
                    Name = name,
                    Default = Get("default") ?? "",
                    Required = string.Equals(Get("required"), "true", StringComparison.OrdinalIgnoreCase),
                    Description = Get("description"),
                };
            }
            else
            {
                param = new WorkflowParam { Name = name, Default = value?.ToString() ?? "" };
            }
            result.Add(param);
        }
        return result;
    }

    private sealed class ScmDto
    {
        public string? Url { get; set; }
        public string? Branch { get; set; }
        public string? Ref { get; set; }
        public string? Credentials { get; set; }
        public bool? CommitStatus { get; set; }
    }

    private sealed class WorkflowYamlDto
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
        public Dictionary<string, object>? Params { get; set; }
        public object? Schedule { get; set; }
        public ScmDto? Scm { get; set; }
        public Dictionary<string, JobDto>? Jobs { get; set; }
        public string? RunsOn { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public List<StepDto>? Steps { get; set; }
    }

    private sealed class JobDto
    {
        public string? RunsOn { get; set; }
        public List<string>? Needs { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? If { get; set; }
        public int? TimeoutMinutes { get; set; }
        public int? TimeoutSeconds { get; set; }
        public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Command { get; set; }
        public string? Shell { get; set; }
        public bool ContinueOnError { get; set; }
        public int Retry { get; set; }
        public int? TimeoutMinutes { get; set; }
        public int? TimeoutSeconds { get; set; }
        public Dictionary<string, string>? Env { get; set; }
    }
}
