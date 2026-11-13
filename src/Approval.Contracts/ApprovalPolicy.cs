namespace Approval.Contracts;

public sealed record PolicyStage(string Name, string[] Roles, double TimeoutMinutes = 15);

public sealed record ApprovalPolicy(
    string Id,
    string ClientId,
    string? AccountId,
    string WorkflowCode,
    int Version,
    PolicyStage[] Stages,
    DateTimeOffset SavedAt)
{
    public void Validate()
    {
        ValidateKey(Id, nameof(Id));
        ValidateKey(ClientId, nameof(ClientId));
        if (AccountId is not null) ValidateKey(AccountId, nameof(AccountId));
        ValidateKey(WorkflowCode, nameof(WorkflowCode));
        if (Version < 1 || Stages is null || Stages.Length is < 1 or > 10)
            throw new ArgumentException("A policy needs a positive version and 1-10 stages.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (PolicyStage stage in Stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            ValidateKey(stage.Name, "Stage name");
            if (!names.Add(stage.Name) || stage.Roles is null || stage.Roles.Length is < 1 or > 10 ||
                stage.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stage.Roles.Length)
                throw new ArgumentException("Stage names must be unique; each stage needs 1-10 unique roles.");
            if (!double.IsFinite(stage.TimeoutMinutes) || stage.TimeoutMinutes is <= 0 or > 1440)
                throw new ArgumentException("Stage timeout must be greater than zero and at most 1440 minutes.");
            foreach (string role in stage.Roles)
            {
                ValidateKey(role, "Role");
            }
        }
    }

    public ResolvedApprovalPolicy Resolve(Func<string, string> actorForRole)
    {
        Validate();
        return new(Id, ClientId, AccountId, WorkflowCode, Version,
            Stages.Select(stage => new ResolvedPolicyStage(stage.Name, stage.TimeoutMinutes,
                stage.Roles.Select(role => new ResolvedRole(role, actorForRole(role))).ToArray())).ToArray());
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
public sealed record ResolvedApprovalPolicy(
    string Id, string ClientId, string? AccountId, string WorkflowCode, int Version, ResolvedPolicyStage[] Stages);

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
