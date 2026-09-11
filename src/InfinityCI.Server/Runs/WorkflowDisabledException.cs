using InfinityCI.Core;

namespace InfinityCI.Server.Runs;

/// <summary>Trigger rejected because an admin disabled the workflow.</summary>
public sealed class WorkflowDisabledException(string workflowName)
    : InvalidOperationException($"Workflow '{workflowName}' is disabled.");
