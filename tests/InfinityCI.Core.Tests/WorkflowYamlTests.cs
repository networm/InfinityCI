using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class WorkflowYamlTests
{
    private const string ValidYaml = """
        name: demo
        project: MyProject
        jobs:
          build:
            runs_on: local
            env:
              FOO: bar
            steps:
              - name: Compile
                command: dotnet build
          test:
            runs_on: agent:docker
            steps:
              - command: echo testing
                continue_on_error: true
                env:
                  BAZ: qux
        """;

    [Fact]
    public void Parse_MultiJobWorkflow_ReturnsParallelJobs()
    {
        var workflow = WorkflowYaml.Parse(ValidYaml);

        Assert.Equal("demo", workflow.Name);
        Assert.Equal("MyProject", workflow.Project);
        Assert.Equal(2, workflow.Jobs.Count);

        var build = workflow.Jobs["build"];
        Assert.Equal("local", build.RunsOn);
        Assert.False(build.RunsOnAgent);
        Assert.Equal("bar", build.Environment["FOO"]);
        Assert.Equal("Compile", build.Steps[0].Name);

        var test = workflow.Jobs["test"];
        Assert.True(test.RunsOnAgent);
        Assert.Equal("docker", test.RequiredAgentLabel);
        Assert.Equal("step 1", test.Steps[0].Name);
        Assert.True(test.Steps[0].ContinueOnError);
        Assert.Equal("qux", test.Steps[0].Environment["BAZ"]);
    }

    [Fact]
    public void Parse_LegacySingleJobForm_WrapsIntoDefaultJob()
    {
        var yaml = "name: legacy\nsteps:\n  - command: echo hi";
        var workflow = WorkflowYaml.Parse(yaml);

        Assert.Equal("Default", workflow.Project);
        var job = Assert.Single(workflow.Jobs.Values);
        Assert.Equal("default", workflow.Jobs.Keys.Single());
        Assert.Single(job.Steps);
        Assert.Equal("local", job.RunsOn);
    }

    [Fact]
    public void Parse_MissingName_Throws()
    {
        var yaml = "jobs:\n  a:\n    steps:\n      - command: echo";
        var ex = Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse(yaml));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NoJobs_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("name: empty"));
    }

    [Fact]
    public void Parse_JobWithoutSteps_Throws()
    {
        var yaml = "name: broken\njobs:\n  a:\n    runs_on: local";
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse(yaml));
    }

    [Fact]
    public void Parse_StepMissingCommand_Throws()
    {
        var yaml = "name: broken\njobs:\n  a:\n    steps:\n      - name: no command";
        var ex = Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse(yaml));
        Assert.Contains("command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MalformedYaml_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("name: [unclosed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_Throws(string yaml)
    {
        Assert.Throws<ArgumentException>(() => WorkflowYaml.Parse(yaml));
    }
}
