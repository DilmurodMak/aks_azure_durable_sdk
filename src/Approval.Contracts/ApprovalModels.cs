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
        ApprovalRules.ValidateCorrelationId(Step.CorrelationId);
        if (Step.StageCount is < 1 or > 10 || Step.StageNumber < 1 || Step.StageNumber > Step.StageCount)
        {
            throw new ArgumentException("Step stage number must be within its 1-10 stages.");
        }
    }

    /// <summary>The business tags shared by this step's instance, its spans, and its decision records.</summary>
    public Dictionary<string, string> Tags(string source) => ApprovalTags.Create(
        Step.ClientId, Step.AccountId, Step.WorkflowCode, PlanId, Version, Requester, Step.CorrelationId, source,
        Step.Stage, Step.PolicyId, Step.PolicyVersion, Step.ParentInstanceId);
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

    public const int MaxCommentLength = 1000;

    public static void ValidateBudget(decimal amount)
    {
        if (amount < 0 || amount > 999999999999.99m || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("USD amounts must be between 0 and 999999999999.99 with at most two decimal places.");
    }

    /// <summary>Why a decision's comment is unacceptable, or null. Rejecting or returning needs a reason.</summary>
    public static string? CommentProblem(string? outcome, string? comment)
    {
        if (comment is { Length: > MaxCommentLength })
            return $"A comment can be at most {MaxCommentLength} characters.";
        if (outcome is ApprovalOutcome.Reject or ApprovalOutcome.Return && string.IsNullOrWhiteSpace(comment))
            return "A comment is required to reject or return a plan.";
        return null;
    }

    public static void ValidateCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 80 ||
            correlationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Correlation ID must contain 1-80 ASCII letters, digits, hyphens, or underscores.");
    }

    /// <summary>
    /// Separation of duties for a resolved chain: the requester may not review their own plan, and one person
    /// may not hold reviews in two different stages. Checked at start, when people are resolved.
    /// </summary>
    public static void ValidateSeparationOfDuties(string requester, IEnumerable<ResolvedPolicyStage> stages)
    {
        Dictionary<string, string> firstStage = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedPolicyStage stage in stages)
        {
            foreach (ResolvedRole role in stage.Approvals)
            {
                if (string.Equals(role.Approver, requester, StringComparison.OrdinalIgnoreCase))
                    throw new SeparationOfDutiesException(
                        $"The requester {requester} holds the required role {role.Role} (stage {stage.Name}). " +
                        "Assign a different user to that role or start the plan as someone else.");
                if (!firstStage.TryAdd(role.Approver, stage.Name) && firstStage[role.Approver] != stage.Name)
                    throw new SeparationOfDutiesException(
                        $"{role.Approver} would review in both stage {firstStage[role.Approver]} and stage {stage.Name}. " +
                        "Assign different users so no one reviews twice.");
            }
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

/// <summary>A reviewer's decision. A comment is required to reject or return and recommended for every decision.</summary>
public sealed record Decision(string Approver, string Outcome, int Version, string? Comment = null);

/// <summary>
/// One reviewer step's outcome: who decided, when, and why. Timeout and Canceled steps have no decider.
/// </summary>
public sealed record ApprovalResult(
    string Status, int Version, string? DecidedBy = null, DateTimeOffset? DecidedAt = null, string? Comment = null);

public sealed record ApplyDecisionRequest(string Receipt, int DelaySeconds = 0);

/// <summary>The audit-worthy facts of one finished reviewer step, handed to the recording activity.</summary>
public sealed record RecordDecisionRequest(ApprovalRequest Request, ApprovalResult Result);

/// <summary>What a reviewer can choose, and the step status each choice produces.</summary>
public static class ApprovalOutcome
{
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string Return = "Return";

    public static string? ToStatus(string? outcome) => outcome switch
    {
        Approve => ApprovalStatus.Approved,
        Reject => ApprovalStatus.Rejected,
        Return => ApprovalStatus.Returned,
        _ => null,
    };
}

public static class ApprovalStatus
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Returned = "Returned";
    public const string Timeout = "Timeout";
    public const string Canceled = "Canceled";

    /// <summary>A reviewer step that ends the whole chain: the plan goes back, or nobody answered in time.</summary>
    public static bool EndsChain(string status) => status is Rejected or Returned or Timeout;

    public static bool IsKnown(string status) => status is Approved or Rejected or Returned or Timeout or Canceled;
}

/// <summary>The start was refused because a person would review their own plan or review twice.</summary>
public sealed class SeparationOfDutiesException(string message) : InvalidOperationException(message);

public static class ApprovalNames
{
    public const string StepOrchestration = "ApprovalStep";
    public const string ChainOrchestration = "ApprovalChain";
    public const string DecisionEvent = "Decision";
    public const string CancelEvent = "Cancel";

    public static string StepInstanceId(string parent, int stageNumber, int reviewerNumber) =>
        FormattableString.Invariant($"{parent}-s{stageNumber}-r{reviewerNumber}");
}
