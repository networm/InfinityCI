using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class JobYamlTests
{
    private const string ValidYaml = """
        name: hello-build
        description: A demo job
        env:
          FOO: bar
          COUNT: 3
        steps:
          - name: Print version
            command: dotnet --version
          - command: echo done
            continue_on_error: true
            env:
              BAZ: qux
        """;

    [Fact]
    public void Parse_ValidYaml_ReturnsDefinition()
    {
        var job = JobYaml.Parse(ValidYaml);

        Assert.Equal("hello-build", job.Name);
        Assert.Equal("A demo job", job.Description);
        Assert.Equal("bar", job.Environment["FOO"]);
        Assert.Equal("3", job.Environment["COUNT"]);
        Assert.Equal(2, job.Steps.Count);

        Assert.Equal("Print version", job.Steps[0].Name);
        Assert.Equal("dotnet --version", job.Steps[0].Command);
        Assert.False(job.Steps[0].ContinueOnError);

        // Unnamed step gets a default name.
        Assert.Equal("step 2", job.Steps[1].Name);
        Assert.True(job.Steps[1].ContinueOnError);
        Assert.Equal("qux", job.Steps[1].Environment["BAZ"]);
    }

    [Fact]
    public void Parse_MissingName_Throws()
    {
        var yaml = "steps:\n  - command: echo hi";
        var ex = Assert.Throws<JobYamlException>(() => JobYaml.Parse(yaml));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptySteps_Throws()
    {
        var yaml = "name: empty\nsteps: []";
        Assert.Throws<JobYamlException>(() => JobYaml.Parse(yaml));
    }

    [Fact]
    public void Parse_StepMissingCommand_Throws()
    {
        var yaml = "name: broken\nsteps:\n  - name: no command here";
        var ex = Assert.Throws<JobYamlException>(() => JobYaml.Parse(yaml));
        Assert.Contains("command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MalformedYaml_Throws()
    {
        var yaml = "name: [unclosed";
        Assert.Throws<JobYamlException>(() => JobYaml.Parse(yaml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_Throws(string yaml)
    {
        Assert.Throws<ArgumentException>(() => JobYaml.Parse(yaml));
    }
}
