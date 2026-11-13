namespace Approval.Contracts;

public sealed record ApprovalRequest(
    string PlanId,
    int Version,
    string Requester,
    string Approver,
    double TimeoutMinutes,
    int ApplyDecisionDelaySeconds = 0,
    ApprovalStepContext? Step = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(Approver);
        if (PlanId.Length > 80 ||
            PlanId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("PlanId must contain 1-80 ASCII letters, digits, or hyphens.");
        }

        if (Version < 1)
        {
            throw new ArgumentException("Version must be positive.");
        }

        if (!double.IsFinite(TimeoutMinutes) || TimeoutMinutes <= 0 || TimeoutMinutes > 1440)
        {
            throw new ArgumentException("TimeoutMinutes must be greater than zero and at most 1440.");
        }

        if (ApplyDecisionDelaySeconds is < 0 or > 60)
        {
            throw new ArgumentException("ApplyDecisionDelaySeconds must be between zero and 60.");
        }
    }
}

public sealed record Decision(string Approver, string Outcome, int Version);

public sealed record ApprovalResult(string Status, int Version, string? Receipt);

public sealed record ApplyDecisionRequest(string Receipt, int DelaySeconds = 0);

public static class ApprovalNames
{
    public const string StepOrchestration = "ApprovalStep";
    public const string ChainOrchestration = "ApprovalChain";
    public const string DecisionEvent = "Decision";

    public static string StepInstanceId(string parent, int stageNumber, int reviewerNumber) =>
        FormattableString.Invariant($"{parent}-s{stageNumber}-r{reviewerNumber}");
}
