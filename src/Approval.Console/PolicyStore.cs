using Approval.Contracts;

namespace Approval.Console;

public sealed record ActivePolicy(string Id, int Version);
public sealed record PolicyRegistry(ApprovalPolicy[] Policies, ActivePolicy[] Active);

public interface IPolicyStore
{
    Task<PolicyRegistry> ListAsync(CancellationToken cancellation);
    Task<ApprovalPolicy> SaveAsync(SavePolicyRequest request, CancellationToken cancellation);
    Task<ApprovalPolicy> ActivateAsync(string id, int version, CancellationToken cancellation);
    Task<ApprovalPolicy> ResolveAsync(string client, string? account, string workflow, CancellationToken cancellation);
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
            if (latest is not null &&
                (latest.ClientId != request.ClientId || latest.AccountId != request.AccountId || latest.WorkflowCode != request.WorkflowCode))
                throw new ArgumentException("Existing policy scope cannot change. Create a different policy ID.");
            ApprovalPolicy policy = new(request.Id, request.ClientId, request.AccountId, request.WorkflowCode,
                request.BaseVersion + 1, request.Stages, DateTimeOffset.UtcNow);
            policy.Validate();
            if (latest is not null && SameContent(latest, policy))
                return latest;
            PolicyRegistry updated = registry with { Policies = [.. registry.Policies, policy] };
            await WriteAsync(SelectActive(updated, policy), cancellation);
            return policy;
        }
        finally { gate.Release(); }
    }

    private static bool SameContent(ApprovalPolicy current, ApprovalPolicy candidate) =>
        current.Stages.Length == candidate.Stages.Length &&
        current.Stages.Zip(candidate.Stages).All(pair =>
            pair.First.Name == pair.Second.Name &&
            pair.First.TimeoutMinutes == pair.Second.TimeoutMinutes &&
            pair.First.Roles.SequenceEqual(pair.Second.Roles));

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
            return current.ClientId != policy.ClientId || current.AccountId != policy.AccountId ||
                current.WorkflowCode != policy.WorkflowCode;
        }).ToArray();
        return registry with { Active = [.. retained, new(policy.Id, policy.Version)] };
    }

    public async Task<ApprovalPolicy> ResolveAsync(
        string client, string? account, string workflow, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(client, "Client");
        ApprovalPolicy.ValidateKey(workflow, "Workflow");
        if (account is not null) ApprovalPolicy.ValidateKey(account, "Account");
        PolicyRegistry registry = await ListAsync(cancellation);
        ApprovalPolicy[] active = registry.Active.Select(reference => registry.Policies.Single(item =>
            item.Id == reference.Id && item.Version == reference.Version)).ToArray();
        ApprovalPolicy? policy = active.SingleOrDefault(item =>
            item.ClientId == client && item.AccountId == account && item.WorkflowCode == workflow);
        if (policy is null && account is not null)
            policy = active.SingleOrDefault(item => item.ClientId == client && item.AccountId is null && item.WorkflowCode == workflow);
        policy ??= active.SingleOrDefault(item =>
            item.ClientId == "agency-default" && item.AccountId is null && item.WorkflowCode == workflow);
        return (policy ?? throw new InvalidOperationException(
            "No active approval policy matches this client/account/workflow.")) with
        { ClientId = client, AccountId = account };
    }

    private Task<PolicyRegistry> ReadAsync(CancellationToken cancellation) =>
        RegistryFile.ReadAsync(path, () => new PolicyRegistry([], []), cancellation);

    private Task WriteAsync(PolicyRegistry registry, CancellationToken cancellation) =>
        RegistryFile.WriteAsync(path, registry, cancellation);
}
