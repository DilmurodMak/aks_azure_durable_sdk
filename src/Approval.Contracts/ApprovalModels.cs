namespace Approval.Contracts;

/// <summary>One reviewer's wait for one decision inside a policy chain.</summary>
public sealed record ApprovalRequest(
    string PlanId,
    int Version,
    string Requester,
    string Approver,
    double TimeoutMinutes,
    ApprovalStepContext Step)
{
    public void Validate()
    {
        ApprovalRules.ValidatePlan(PlanId, Version, Requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(Approver);
        ApprovalRules.ValidateTimeout(TimeoutMinutes);
        ArgumentNullException.ThrowIfNull(Step);
        ApprovalRules.ValidateParentId(Step.ParentInstanceId);
        ApprovalRules.ValidatePolicyKeys(Step.PolicyId, Step.ClientId, Step.AccountId, Step.WorkflowCode, Step.PolicyVersion);
        ApprovalPolicy.ValidateKey(Step.Stage, "Stage name");
        ApprovalPolicy.ValidateKey(Step.Role, "Role");
        if (Step.StageCount is < 1 or > 10 || Step.StageNumber < 1 || Step.StageNumber > Step.StageCount)
        {
            throw new ArgumentException("Step stage number must be within its 1-10 stages.");
        }
    }
}

/// <summary>Validation shared by the console (before scheduling) and the workers (before acting).</summary>
public static class ApprovalRules
{
    public static void ValidatePlan(string planId, int version, string requester)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);
        if (planId.Length > 80 || planId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("PlanId must contain 1-80 ASCII letters, digits, or hyphens.");
        }

        if (version < 1)
        {
            throw new ArgumentException("Version must be positive.");
        }
    }

    public static void ValidateTimeout(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes <= 0 || minutes > 1440)
        {
            throw new ArgumentException("TimeoutMinutes must be greater than zero and at most 1440.");
        }
    }

    public static void ValidateDelay(int seconds)
    {
        if (seconds is < 0 or > 60)
        {
            throw new ArgumentException("ApplyDecisionDelaySeconds must be between zero and 60.");
        }
    }

    public static void ValidateParentId(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (instanceId.Length > 80)
        {
            throw new ArgumentException("Parent instance IDs must be at most 80 characters.");
        }
    }

    public static void ValidatePolicyKeys(string policy, string client, string? account, string workflow, int version)
    {
        ApprovalPolicy.ValidateKey(policy, "Policy ID");
        ApprovalPolicy.ValidateKey(client, "Client ID");
        if (account is not null) ApprovalPolicy.ValidateKey(account, "Account ID");
        ApprovalPolicy.ValidateKey(workflow, "Workflow code");
        if (version < 1) throw new ArgumentException("Policy version must be positive.");
    }
}

public sealed record Decision(string Approver, string Outcome, int Version);

public sealed record ApprovalResult(string Status, int Version);

public sealed record ApplyDecisionRequest(string Receipt, int DelaySeconds = 0);

public static class ApprovalNames
{
    public const string StepOrchestration = "ApprovalStep";
    public const string ChainOrchestration = "ApprovalChain";
    public const string DecisionEvent = "Decision";

    public static string StepInstanceId(string parent, int stageNumber, int reviewerNumber) =>
        FormattableString.Invariant($"{parent}-s{stageNumber}-r{reviewerNumber}");
}
