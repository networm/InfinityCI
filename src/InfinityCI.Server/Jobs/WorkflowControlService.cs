using System.Security.Cryptography;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server.Realtime;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Jobs;

public sealed record WorkflowRuntimeState(string WorkflowName, bool Enabled, string? WebhookToken, string? NotifyWebhookUrl, bool HasWebhookSecret, string? WebhookBranches, string? WebhookEvents);

/// <summary>
/// Runtime toggles per workflow (enabled, incoming-webhook token, WeCom notify
/// URL), persisted outside the config Git repo since they are operational
/// state rather than declarative configuration.
/// </summary>
public class WorkflowControlService(IServiceScopeFactory scopeFactory, WorkflowStore workflowStore, ChangeEvents events, ILogger<WorkflowControlService> logger)
{
    /// <summary>State for a workflow; defaults to enabled with no tokens.</summary>
    public async Task<WorkflowRuntimeState> GetAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.WorkflowStates.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowName == workflowName, ct);
        if (record is null)
            return new WorkflowRuntimeState(workflowName, Enabled: true, WebhookToken: null, NotifyWebhookUrl: null, HasWebhookSecret: false, WebhookBranches: null, WebhookEvents: null);
        return new WorkflowRuntimeState(record.WorkflowName, record.Enabled, record.WebhookToken, record.NotifyWebhookUrl,
            HasWebhookSecret: !string.IsNullOrEmpty(record.WebhookSecret), WebhookBranches: record.WebhookBranches, WebhookEvents: record.WebhookEvents);
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

    /// <summary>Sets the webhook HMAC secret, branch filter and/or enabled event
    /// kinds. A null field keeps the current value; an empty string clears it. The
    /// secret is write-only through the API (state exposes only HasWebhookSecret).</summary>
    public async Task SetWebhookConfigAsync(string workflowName, string? secret, string? branches, string? events = null)
    {
        await UpsertAsync(workflowName, state =>
        {
            if (secret is not null)
                state.WebhookSecret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
            if (branches is not null)
                state.WebhookBranches = string.IsNullOrWhiteSpace(branches) ? null : branches.Trim();
            if (events is not null)
                state.WebhookEvents = string.IsNullOrWhiteSpace(events) ? null : events.Trim();
        });
    }

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

    /// <summary>Raw webhook signing secret for the push endpoint (never exposed via the API).</summary>
    public async Task<string?> GetWebhookSecretAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates.AsNoTracking()
            .Where(w => w.WorkflowName == workflowName)
            .Select(w => w.WebhookSecret)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Branch filter patterns for the push endpoint (comma-separated wildcards).</summary>
    public async Task<string?> GetWebhookBranchesAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates.AsNoTracking()
            .Where(w => w.WorkflowName == workflowName)
            .Select(w => w.WebhookBranches)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Enabled webhook event kinds ("push", "pr"); null = push only.</summary>
    public async Task<string?> GetWebhookEventsAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates.AsNoTracking()
            .Where(w => w.WorkflowName == workflowName)
            .Select(w => w.WebhookEvents)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>All notification channels configured for one workflow. Falls back to
    /// the legacy single WeCom URL column when the channels JSON is absent.</summary>
    public virtual async Task<List<NotifyChannel>> GetNotifyChannelsAsync(string workflowName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.WorkflowStates.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowName == workflowName, ct);
        if (record is null)
            return [];
        if (!string.IsNullOrEmpty(record.NotifyChannelsJson))
            return JsonSerializer.Deserialize<List<NotifyChannel>>(record.NotifyChannelsJson) ?? [];
        return string.IsNullOrEmpty(record.NotifyWebhookUrl)
            ? []
            : [new NotifyChannel("wecom", record.NotifyWebhookUrl, "always")];
    }

    /// <summary>Replaces the workflow's notification channels; an empty list clears them.</summary>
    public async Task SetNotifyChannelsAsync(string workflowName, IReadOnlyList<NotifyChannel> channels)
    {
        var json = channels.Count == 0 ? null : JsonSerializer.Serialize(channels);
        await UpsertAsync(workflowName, state => state.NotifyChannelsJson = json);
    }

    /// <summary>Enabled map across all workflows (missing rows default to enabled).</summary>
    public async Task<Dictionary<string, bool>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        return await db.WorkflowStates.ToDictionaryAsync(w => w.WorkflowName, w => w.Enabled, ct);
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
        // Runtime toggles are part of the job list rows; push a change signal
        // (project routes the event to the right per-project group).
        var project = workflowStore.TryGet(workflowName)?.Project ?? "";
        await events.PublishWorkflowChangedAsync(workflowName, WorkflowChangeKind.Updated, project);
    }
}
