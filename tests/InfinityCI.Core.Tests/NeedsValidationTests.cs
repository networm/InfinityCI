using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class NeedsValidationTests
{
    [Fact]
    public void Parse_ValidNeeds_Parsed()
    {
        var workflow = WorkflowYaml.Parse("""
            name: dag
            jobs:
              build:
                steps:
                  - command: echo build
              test:
                needs: [build]
                steps:
                  - command: echo test
              deploy:
                needs: [build, test]
                steps:
                  - command: echo deploy
            """);

        Assert.Empty(workflow.Jobs["build"].Needs);
        Assert.Equal(["build"], workflow.Jobs["test"].Needs);
        Assert.Equal(2, workflow.Jobs["deploy"].Needs.Count);
    }

    [Fact]
    public void Parse_UnknownNeed_Throws()
    {
        var ex = Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("""
            name: bad
            jobs:
              a:
                needs: [nope]
                steps:
                  - command: echo
            """));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Parse_SelfNeed_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("""
            name: bad
            jobs:
              a:
                needs: [a]
                steps:
                  - command: echo
            """));
    }

    [Fact]
    public void Parse_DirectCycle_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("""
            name: bad
            jobs:
              a:
                needs: [b]
                steps:
                  - command: echo
              b:
                needs: [a]
                steps:
                  - command: echo
            """));
    }

    [Fact]
    public void Parse_IndirectCycle_Throws()
    {
        Assert.Throws<WorkflowYamlException>(() => WorkflowYaml.Parse("""
            name: bad
            jobs:
              a:
                needs: [c]
                steps:
                  - command: echo
              b:
                needs: [a]
                steps:
                  - command: echo
              c:
                needs: [b]
                steps:
                  - command: echo
            """));
    }
}
