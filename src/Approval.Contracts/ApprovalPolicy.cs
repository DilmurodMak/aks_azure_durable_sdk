namespace Approval.Contracts;

public sealed record PolicyStage(string Name, string[] Roles, double TimeoutMinutes = 15);

/// <summary>A chain used only for one business step. It replaces the default chain for that step.</summary>
public sealed record StepOverride(string WorkflowCode, PolicyStage[] Stages);

/// <summary>
/// One versioned approval policy set for a client or a single account: an optional default chain that
/// applies to every business step, plus per-step overrides. Roles only; people come from role assignments.
/// </summary>
public sealed record ApprovalPolicy(
    string Id,
    string ClientId,
    string? AccountId,
    int Version,
    PolicyStage[]? DefaultStages,
    StepOverride[] Overrides,
    DateTimeOffset SavedAt)
{
    public const int MaxOverrides = 50;

    public void Validate()
    {
        ValidateKey(Id, nameof(Id));
        ValidateKey(ClientId, nameof(ClientId));
        if (AccountId is not null) ValidateKey(AccountId, nameof(AccountId));
        if (Version < 1) throw new ArgumentException("A policy needs a positive version.");
        if (Overrides is null || Overrides.Length > MaxOverrides)
            throw new ArgumentException($"Overrides must be a list of at most {MaxOverrides} business steps.");
        if (DefaultStages is null && Overrides.Length == 0)
            throw new ArgumentException("A policy needs a default chain or at least one step override.");
        if (DefaultStages is not null) ValidateStages(DefaultStages, "Default chain");
        HashSet<string> codes = new(StringComparer.Ordinal);
        foreach (StepOverride item in Overrides)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateKey(item.WorkflowCode, "Business step");
            if (!codes.Add(item.WorkflowCode))
                throw new ArgumentException("Each business step can have only one override.");
            ValidateStages(item.Stages, $"Override for {item.WorkflowCode}");
        }
    }

    /// <summary>The override for the step if there is one, otherwise the default chain, otherwise nothing.</summary>
    public (PolicyStage[] Stages, bool IsOverride)? ChainFor(string workflowCode)
    {
        StepOverride? match = Overrides.FirstOrDefault(item => item.WorkflowCode == workflowCode);
        if (match is not null) return (match.Stages, true);
        return DefaultStages is null ? null : (DefaultStages, false);
    }

    private static void ValidateStages(PolicyStage[]? stages, string name)
    {
        if (stages is null || stages.Length is < 1 or > 10)
            throw new ArgumentException($"{name} needs 1-10 stages.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (PolicyStage stage in stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            ValidateKey(stage.Name, "Stage name");
            if (!names.Add(stage.Name) || stage.Roles is null || stage.Roles.Length is < 1 or > 10 ||
                stage.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stage.Roles.Length)
                throw new ArgumentException($"{name}: stage names must be unique; each stage needs 1-10 unique roles.");
            if (!double.IsFinite(stage.TimeoutMinutes) || stage.TimeoutMinutes is <= 0 or > 1440)
                throw new ArgumentException($"{name}: stage timeout must be greater than zero and at most 1440 minutes.");
            foreach (string role in stage.Roles)
            {
                ValidateKey(role, "Role");
            }
        }
    }

    public static void ValidateKey(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException($"{name} must contain 1-80 ASCII letters, digits, or hyphens.");
    }
}

public sealed record ResolvedRole(string Role, string Approver);
public sealed record ResolvedPolicyStage(string Name, double TimeoutMinutes, ResolvedRole[] Approvals);

/// <summary>
/// The chain pinned into a workflow at start. <paramref name="Source"/> records which rule supplied it,
/// for example "client override" or "agency default".
/// </summary>
public sealed record ResolvedApprovalPolicy(
    string Id, string ClientId, string? AccountId, string WorkflowCode, int Version, ResolvedPolicyStage[] Stages,
    string Source);

public sealed record ApprovalChainRequest(
    string PlanId, int Version, string Requester, ResolvedApprovalPolicy Policy, int ApplyDecisionDelaySeconds = 0);

public sealed record ApprovalStepContext(
    string ParentInstanceId, string ClientId, string? AccountId, string WorkflowCode,
    string PolicyId, int PolicyVersion, string Stage, int StageNumber, int StageCount, string Role);

public sealed record ApprovalProgress(
    string State, string Stage, int StageNumber, int StageCount, int Completed = 0, int Total = 1)
{
    public string Display => $"{State}: {Stage} ({StageNumber}/{StageCount})";
}

public sealed record ApprovalStepResult(
    string InstanceId, string Stage, string Role, string Approver, string Status);

public sealed record ApprovalChainResult(
    string Status, int Version, string PolicyId, int PolicyVersion,
    ApprovalStepResult[] Steps, string? Receipt);

public static class ApprovalTags
{
    public static Dictionary<string, string> Create(
        string client, string workflow, string plan, int version, string source,
        string stage, string? policyId = null, int? policyVersion = null, string? parent = null)
    {
        Dictionary<string, string> tags = new()
        {
            ["durabletask.displayName"] = $"{plan} v{version} - {stage}",
            ["client"] = client,
            ["workflow"] = workflow,
            ["plan"] = plan,
            ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = source,
            ["stage"] = stage
        };
        if (policyId is not null) tags["policy"] = policyId;
        if (policyVersion is not null) tags["policyVersion"] = policyVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (parent is not null) tags["parent"] = parent;
        return tags;
    }
}
