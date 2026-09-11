using System.Security.Cryptography;
using InfinityCI.Core;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowRuntimeState(string WorkflowName, bool Enabled, string? WebhookToken, string? NotifyWebhookUrl);

/// <summary>
/// Runtime toggles per workflow (enabled, incoming-webhook token, WeCom notify
/// URL), persisted outside the config Git repo since they are operational
/// state rather than declarative configuration.
/// </summary>
public class WorkflowControlService(IServiceScopeFactory scopeFactory, ILogger<WorkflowControlService> logger)
{
    /// <summary>State for a workflow; defaults to enabled with no tokens.</summary>
    public async Task<WorkflowRuntimeState> GetAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.WorkflowStates.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowName == workflowName, ct);
        if (record is null)
            return new WorkflowRuntimeState(workflowName, Enabled: true, WebhookToken: null, NotifyWebhookUrl: null);
        return new WorkflowRuntimeState(record.WorkflowName, record.Enabled, record.WebhookToken, record.NotifyWebhookUrl);
    }

    /// <summary>Workflow must be enabled for triggering.</summary>
    public async Task<bool> IsEnabledAsync(string workflowName, CancellationToken ct = default) =>
        (await GetAsync(workflowName, ct)).Enabled;

    public async Task SetEnabledAsync(string workflowName, bool enabled)
    {
        await UpsertAsync(workflowName, state => state.Enabled = enabled);
        logger.LogInformation("Workflow {Workflow} is now {State}", workflowName, enabled ? "enabled" : "disabled");
    }

    /// <summary>Generates a fresh webhook token, replacing any previous one.</summary>
    public async Task<string> IssueWebhookTokenAsync(string workflowName)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await UpsertAsync(workflowName, state => state.WebhookToken = token);
        return token;
    }

    public async Task RevokeWebhookTokenAsync(string workflowName) =>
        await UpsertAsync(workflowName, state => state.WebhookToken = null);

    /// <summary>Finds the workflow a webhook token belongs to; null for unknown tokens.</summary>
    public async Task<string?> FindByWebhookTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates
            .Where(w => w.WebhookToken == token)
            .Select(w => w.WorkflowName)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetNotifyWebhookUrlAsync(string workflowName, string? url)
    {
        await UpsertAsync(workflowName, state => state.NotifyWebhookUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim());
    }

    /// <summary>Enabled map across all workflows (missing rows default to enabled).</summary>
    public async Task<Dictionary<string, bool>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates.ToDictionaryAsync(w => w.WorkflowName, w => w.Enabled, ct);
    }

    /// <summary>All notify URLs for workflows that have one configured.</summary>
    public virtual async Task<Dictionary<string, string>> GetNotifyUrlsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates
            .Where(w => w.NotifyWebhookUrl != null)
            .Select(w => new { w.WorkflowName, w.NotifyWebhookUrl })
            .ToDictionaryAsync(x => x.WorkflowName, x => x.NotifyWebhookUrl!, ct);
    }

    private async Task UpsertAsync(string workflowName, Action<WorkflowState> mutate)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.WorkflowStates.FirstOrDefaultAsync(w => w.WorkflowName == workflowName);
        if (record is null)
        {
            record = new WorkflowState { WorkflowName = workflowName, Enabled = true };
            db.WorkflowStates.Add(record);
        }
        mutate(record);
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}
