using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class WorkflowParamsTests
{
    [Fact]
    public void Parse_ShortAndLongForms()
    {
        var workflow = WorkflowYaml.Parse("""
            name: param-demo
            params:
              ENV_NAME: production
              VERSION:
                default: "1.0"
                required: true
                description: build version
              DEBUG:
                default: "false"
            jobs:
              a:
                steps:
                  - command: echo hi
            """);

        Assert.Equal(3, workflow.Params.Count);

        var env = workflow.Params.Single(p => p.Name == "ENV_NAME");
        Assert.Equal("production", env.Default);
        Assert.False(env.Required);

        var version = workflow.Params.Single(p => p.Name == "VERSION");
        Assert.Equal("1.0", version.Default);
        Assert.True(version.Required);
        Assert.Equal("build version", version.Description);

        var debug = workflow.Params.Single(p => p.Name == "DEBUG");
        Assert.Equal("false", debug.Default);
    }

    [Fact]
    public void Parse_InvalidParamName_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("""
            name: bad
            params:
              HAS-SPACE$: x
            jobs:
              a:
                steps:
                  - command: echo
            """));
    }
}
