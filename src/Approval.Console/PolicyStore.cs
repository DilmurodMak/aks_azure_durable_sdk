using Approval.Contracts;

namespace Approval.Console;

public sealed record ActivePolicy(string Id, int Version);
public sealed record PolicyRegistry(ApprovalPolicy[] Policies, ActivePolicy[] Active);

/// <summary>The chain chosen for one business step, and which rule supplied it (for audit).</summary>
public sealed record PolicyMatch(ApprovalPolicy Policy, string WorkflowCode, PolicyStage[] Stages, string Source)
{
    public ResolvedApprovalPolicy Resolve(string client, string? account, Func<string, string> actorForRole) =>
        new(Policy.Id, client, account, WorkflowCode, Policy.Version,
            Stages.Select(stage => new ResolvedPolicyStage(stage.Name, stage.TimeoutMinutes,
                stage.Roles.Select(role => new ResolvedRole(role, actorForRole(role))).ToArray())).ToArray(),
            Source);
}

/// <summary>No active policy set supplies a chain for the requested client, account, and business step.</summary>
public sealed class NoMatchingPolicyException()
    : InvalidOperationException("No active approval policy has a default chain or an override for this client, " +
        "account, and business step. Add a default chain or a step override in step 2 (Policies).");

public interface IPolicyStore
{
    Task<PolicyRegistry> ListAsync(CancellationToken cancellation);
    Task<ApprovalPolicy> SaveAsync(SavePolicyRequest request, CancellationToken cancellation);
    Task<ApprovalPolicy> ActivateAsync(string id, int version, CancellationToken cancellation);
    Task<PolicyMatch> ResolveAsync(string client, string? account, string workflow, CancellationToken cancellation);
}

public sealed class PolicyStore(string path) : IPolicyStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PolicyRegistry> ListAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try { return await ReadAsync(cancellation); }
        finally { gate.Release(); }
    }

    public async Task<ApprovalPolicy> SaveAsync(SavePolicyRequest request, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            PolicyRegistry registry = await ReadAsync(cancellation);
            ApprovalPolicy? latest = registry.Policies.Where(item => item.Id == request.Id).MaxBy(item => item.Version);
            if ((latest?.Version ?? 0) != request.BaseVersion)
                throw new InvalidOperationException("Policy changed since it was loaded. Reload before saving.");
            if (latest is not null && (latest.ClientId != request.ClientId || latest.AccountId != request.AccountId))
                throw new ArgumentException("Existing policy scope cannot change. Create a different policy ID.");
            ApprovalPolicy policy = new(request.Id, request.ClientId, request.AccountId, request.BaseVersion + 1,
                request.DefaultStages, [.. (request.Overrides ?? []).OrderBy(item => item.WorkflowCode, StringComparer.Ordinal)],
                DateTimeOffset.UtcNow);
            policy.Validate();
            if (latest is not null && SameContent(latest, policy))
                return latest;
            PolicyRegistry updated = registry with { Policies = [.. registry.Policies, policy] };
            await WriteAsync(SelectActive(updated, policy), cancellation);
            return policy;
        }
        finally { gate.Release(); }
    }

    private static bool SameStages(PolicyStage[]? left, PolicyStage[]? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Length == right.Length && left.Zip(right).All(pair =>
                pair.First.Name == pair.Second.Name &&
                pair.First.TimeoutMinutes == pair.Second.TimeoutMinutes &&
                pair.First.Roles.SequenceEqual(pair.Second.Roles));

    private static bool SameContent(ApprovalPolicy current, ApprovalPolicy candidate) =>
        SameStages(current.DefaultStages, candidate.DefaultStages) &&
        current.Overrides.Length == candidate.Overrides.Length &&
        current.Overrides.Zip(candidate.Overrides).All(pair =>
            pair.First.WorkflowCode == pair.Second.WorkflowCode && SameStages(pair.First.Stages, pair.Second.Stages));

    public async Task<ApprovalPolicy> ActivateAsync(string id, int version, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            PolicyRegistry registry = await ReadAsync(cancellation);
            ApprovalPolicy policy = registry.Policies.SingleOrDefault(item => item.Id == id && item.Version == version)
                ?? throw new KeyNotFoundException("Policy version was not found.");
            await WriteAsync(SelectActive(registry, policy), cancellation);
            return policy;
        }
        finally { gate.Release(); }
    }

    private static PolicyRegistry SelectActive(PolicyRegistry registry, ApprovalPolicy policy)
    {
        ActivePolicy[] retained = registry.Active.Where(active =>
        {
            ApprovalPolicy current = registry.Policies.Single(item => item.Id == active.Id && item.Version == active.Version);
            return current.ClientId != policy.ClientId || current.AccountId != policy.AccountId;
        }).ToArray();
        return registry with { Active = [.. retained, new(policy.Id, policy.Version)] };
    }

    /// <summary>
    /// Narrower owner first: the account's set, then the client's, then agency-default. Inside one owner a
    /// step override beats that owner's default chain. No match is an explicit error, never a guessed chain.
    /// </summary>
    public async Task<PolicyMatch> ResolveAsync(
        string client, string? account, string workflow, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(client, "Client");
        ApprovalPolicy.ValidateKey(workflow, "Business step");
        if (account is not null) ApprovalPolicy.ValidateKey(account, "Account");
        PolicyRegistry registry = await ListAsync(cancellation);
        ApprovalPolicy[] active = registry.Active.Select(reference => registry.Policies.Single(item =>
            item.Id == reference.Id && item.Version == reference.Version)).ToArray();
        (string Level, string Owner, string? OwnerAccount)[] owners = account is null
            ? [("client", client, null), ("agency", "agency-default", null)]
            : [("account", client, account), ("client", client, null), ("agency", "agency-default", null)];
        foreach ((string level, string owner, string? ownerAccount) in owners)
        {
            ApprovalPolicy[] sets = active.Where(item => item.ClientId == owner && item.AccountId == ownerAccount).ToArray();
            foreach (ApprovalPolicy set in sets)
            {
                StepOverride? step = set.Overrides.FirstOrDefault(item => item.WorkflowCode == workflow);
                if (step is not null) return new(set, workflow, step.Stages, $"{level} override");
            }

            foreach (ApprovalPolicy set in sets)
            {
                if (set.DefaultStages is not null) return new(set, workflow, set.DefaultStages, $"{level} default");
            }
        }

        throw new NoMatchingPolicyException();
    }

    private Task<PolicyRegistry> ReadAsync(CancellationToken cancellation) =>
        RegistryFile.ReadAsync(path, () => new PolicyRegistry([], []), cancellation);

    private Task WriteAsync(PolicyRegistry registry, CancellationToken cancellation) =>
        RegistryFile.WriteAsync(path, registry, cancellation);
}
