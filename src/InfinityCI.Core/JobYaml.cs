using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace InfinityCI.Core;

/// <summary>Parses job YAML (GitHub Actions-like syntax) into <see cref="JobDefinition"/>.</summary>
public static class JobYaml
{
    public static JobDefinition Parse(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        JobYamlDto? dto;
        try
        {
            dto = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<JobYamlDto?>(yaml);
        }
        catch (YamlException e)
        {
            throw new JobYamlException($"Invalid YAML: {e.Message}", e);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            throw new JobYamlException("Job 'name' is required.");
        if (dto.Steps is null || dto.Steps.Count == 0)
            throw new JobYamlException($"Job '{dto.Name}' must define at least one step.");

        var steps = new List<JobStep>(dto.Steps.Count);
        for (var i = 0; i < dto.Steps.Count; i++)
        {
            var s = dto.Steps[i];
            if (s is null || string.IsNullOrWhiteSpace(s.Command))
                throw new JobYamlException($"Step {i + 1} of job '{dto.Name}' is missing 'command'.");

            steps.Add(new JobStep
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"step {i + 1}" : s.Name.Trim(),
                Command = s.Command.Trim(),
                Shell = string.IsNullOrWhiteSpace(s.Shell) ? null : s.Shell.Trim(),
                ContinueOnError = s.ContinueOnError,
                Environment = s.Env ?? new Dictionary<string, string>(),
            });
        }

        return new JobDefinition
        {
            Name = dto.Name.Trim(),
            Description = dto.Description,
            Environment = dto.Env ?? new Dictionary<string, string>(),
            Steps = steps,
            RunsOn = string.IsNullOrWhiteSpace(dto.RunsOn) ? null : dto.RunsOn.Trim(),
        };
    }

    private sealed class JobYamlDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? RunsOn { get; set; }
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
