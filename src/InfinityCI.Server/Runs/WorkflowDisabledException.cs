using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

/// <summary>Trigger rejected because an admin disabled the workflow.</summary>
public sealed class WorkflowDisabledException(string workflowName)
    : InvalidOperationException(Msg.T($"Workflow '{workflowName}' is disabled.", $"任务「{workflowName}」已禁用。"));
