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
            throw new WorkflowYamlException($"Invalid YAML: {e.Message}", e);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            throw new WorkflowYamlException($"Workflow 'name' is required.");

        Dictionary<string, WorkflowJob> jobs;
        if (dto.Jobs is { Count: > 0 })
        {
            jobs = new Dictionary<string, WorkflowJob>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, jobDto) in dto.Jobs)
            {
                if (jobDto is null)
                    throw new WorkflowYamlException($"Job '{key}' is empty.");
                jobs[key] = ToJob(jobDto, $"Job '{key}'");
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
                }, "Workflow"),
            };
        }
        else
        {
            throw new WorkflowYamlException($"Workflow '{dto.Name}' must define 'jobs' (or legacy top-level 'steps').");
        }

        return new Workflow
        {
            Name = dto.Name.Trim(),
            Project = string.IsNullOrWhiteSpace(dto.Project) ? "Default" : dto.Project.Trim(),
            Jobs = jobs,
        };
    }

    private static WorkflowJob ToJob(JobDto dto, string context)
    {
        if (dto.Steps is null || dto.Steps.Count == 0)
            throw new WorkflowYamlException($"{context} must define at least one step.");

        var steps = new List<JobStep>(dto.Steps.Count);
        for (var i = 0; i < dto.Steps.Count; i++)
        {
            var s = dto.Steps[i];
            if (s is null || string.IsNullOrWhiteSpace(s.Command))
                throw new WorkflowYamlException($"{context} step {i + 1} is missing 'command'.");
            steps.Add(new JobStep
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"step {i + 1}" : s.Name.Trim(),
                Command = s.Command.Trim(),
                Shell = string.IsNullOrWhiteSpace(s.Shell) ? null : s.Shell.Trim(),
                ContinueOnError = s.ContinueOnError,
                Environment = s.Env ?? new Dictionary<string, string>(),
            });
        }

        return new WorkflowJob
        {
            RunsOn = string.IsNullOrWhiteSpace(dto.RunsOn) ? "local" : dto.RunsOn.Trim(),
            Needs = dto.Needs ?? [],
            Environment = dto.Env ?? new Dictionary<string, string>(),
            Steps = steps,
        };
    }

    /// <summary>Needs must reference existing jobs and must not form cycles.</summary>
    private static void ValidateNeeds(Dictionary<string, WorkflowJob> jobs)
    {
        foreach (var (key, job) in jobs)
        {
            foreach (var need in job.Needs)
            {
                if (need.Equals(key, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowYamlException($"Job '{key}' cannot depend on itself.");
                if (!jobs.ContainsKey(need))
                    throw new WorkflowYamlException($"Job '{key}' needs unknown job '{need}'.");
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
                    throw new WorkflowYamlException($"Job dependency cycle detected involving '{key}'.");
                case Done:
                    return;
            }
            state[key] = Visiting;
            foreach (var need in jobs[key].Needs)
                Visit(need);
            state[key] = Done;
        }
    }

    private sealed class WorkflowYamlDto
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
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
        public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Command { get; set; }
        public string? Shell { get; set; }
        public bool ContinueOnError { get; set; }
        public Dictionary<string, string>? Env { get; set; }
    }
}
