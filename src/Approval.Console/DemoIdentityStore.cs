using Approval.Contracts;

namespace Approval.Console;

/// <summary>Simulates a role assignment in User Administration; it is not an authenticated user.</summary>
public sealed record DemoRoleAssignment(string ClientId, string? AccountId, string Role, string Actor);

/// <summary>Demo-only assignments persisted independently of approval policies.</summary>
public sealed record DemoIdentityRegistry(DemoRoleAssignment[] Assignments);

public interface IDemoIdentityStore
{
    Task<DemoIdentityRegistry> ListAsync(CancellationToken cancellation);
    Task<DemoIdentityRegistry> SaveAsync(SaveDemoIdentitiesRequest request, CancellationToken cancellation);
    Task<Dictionary<string, string>> ResolveAsync(
        string client, string? account, IEnumerable<string> roles, CancellationToken cancellation);
}

public sealed class DemoIdentityStore(string path) : IDemoIdentityStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DemoIdentityRegistry> ListAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try { return await ReadAsync(cancellation); }
        finally { gate.Release(); }
    }

    public async Task<DemoIdentityRegistry> SaveAsync(SaveDemoIdentitiesRequest request, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(request.ClientId, "Client");
        if (request.AccountId is not null) ApprovalPolicy.ValidateKey(request.AccountId, "Account");
        if (request.Assignments is null || request.Assignments.Count > 100 ||
            request.Assignments.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Assignments.Count)
            throw new ArgumentException("Provide at most 100 unique demo roles.");
        foreach (var assignment in request.Assignments)
        {
            ApprovalPolicy.ValidateKey(assignment.Key, "Role");
            if (string.IsNullOrWhiteSpace(assignment.Value) || assignment.Value.Length > 100)
                throw new ArgumentException("Each demo actor must contain 1-100 characters.");
        }

        await gate.WaitAsync(cancellation);
        try
        {
            DemoIdentityRegistry registry = await ReadAsync(cancellation);
            DemoRoleAssignment[] retained = registry.Assignments.Where(item =>
                item.ClientId != request.ClientId || item.AccountId != request.AccountId).ToArray();
            DemoIdentityRegistry updated = new([.. retained, .. request.Assignments.Select(item =>
                new DemoRoleAssignment(request.ClientId, request.AccountId, item.Key, item.Value.Trim()))]);
            await RegistryFile.WriteAsync(path, updated, cancellation);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task<Dictionary<string, string>> ResolveAsync(
        string client, string? account, IEnumerable<string> roles, CancellationToken cancellation)
    {
        DemoIdentityRegistry registry = await ListAsync(cancellation);
        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        foreach (string role in roles.Distinct(StringComparer.Ordinal))
        {
            DemoRoleAssignment? assignment = registry.Assignments.SingleOrDefault(item =>
                item.ClientId == client && item.AccountId == account && item.Role == role);
            if (assignment is null && account is not null)
                assignment = registry.Assignments.SingleOrDefault(item =>
                    item.ClientId == client && item.AccountId is null && item.Role == role);
            assignment ??= registry.Assignments.SingleOrDefault(item =>
                item.ClientId == "agency-default" && item.AccountId is null && item.Role == role);
            resolved[role] = assignment?.Actor ?? throw new MissingDemoIdentityException(role);
        }
        return resolved;
    }

    private Task<DemoIdentityRegistry> ReadAsync(CancellationToken cancellation) =>
        RegistryFile.ReadAsync(path, () => new DemoIdentityRegistry([]), cancellation);
}

public sealed class MissingDemoIdentityException(string role) : Exception("Missing demo identity assignment.")
{
    public string Role { get; } = role;
}
