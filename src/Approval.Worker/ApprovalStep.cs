using System.Diagnostics;
using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace Approval.Worker;

public sealed class ApprovalStep : TaskOrchestrator<ApprovalRequest, ApprovalResult>
{
    public override async Task<ApprovalResult> RunAsync(TaskOrchestrationContext context, ApprovalRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        await context.CallActivityAsync<string>(nameof(RecordSubmission), input);
        ApprovalProgress progress = new("Waiting", input.Step.Stage, input.Step.StageNumber, input.Step.StageCount);
        context.SetCustomStatus(progress);
        ILogger logger = context.CreateReplaySafeLogger<ApprovalStep>();

        using CancellationTokenSource cancellation = new();
        DateTime deadline = context.CurrentUtcDateTime.AddMinutes(input.TimeoutMinutes);
        Task timer = context.CreateTimer(deadline, cancellation.Token);
        Task cancelTask = context.WaitForExternalEvent<string>(ApprovalNames.CancelEvent, cancellation.Token);

        ApprovalResult result;
        try
        {
            while (true)
            {
                Task<Decision> decisionTask = context.WaitForExternalEvent<Decision>(
                    ApprovalNames.DecisionEvent, cancellation.Token);
                Task first = await Task.WhenAny(timer, decisionTask, cancelTask);
                if (first == timer)
                {
                    await timer;
                    result = new ApprovalResult(ApprovalStatus.Timeout, input.Version, DecidedAt: Now(context));
                    break;
                }

                if (first == cancelTask)
                {
                    await cancelTask;
                    result = new ApprovalResult(ApprovalStatus.Canceled, input.Version, DecidedAt: Now(context));
                    break;
                }

                Decision decision = await decisionTask;
                string? refusal = Refusal(input, decision);
                if (refusal is not null)
                {
                    logger.LogWarning(
                        "Instance {InstanceId} refused decision: {Reason}; status {Status}; waiting until {Deadline}",
                        context.InstanceId, refusal, progress.Display, deadline);
                    continue;
                }

                result = new ApprovalResult(ApprovalOutcome.ToStatus(decision.Outcome)!, input.Version,
                    input.Approver, Now(context), decision.Comment?.Trim());
                break;
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await timer;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                logger.LogInformation("Instance {InstanceId} approval timer canceled", context.InstanceId);
            }
        }

        await context.CallActivityAsync<string>(nameof(RecordDecision), new RecordDecisionRequest(input, result));
        context.SetCustomStatus(progress with { State = result.Status, Completed = 1 });
        logger.LogInformation("Instance {InstanceId} finished with {Status}, version {Version}",
            context.InstanceId, result.Status, result.Version);
        return result;
    }

    private static DateTimeOffset Now(TaskOrchestrationContext context) =>
        new(DateTime.SpecifyKind(context.CurrentUtcDateTime, DateTimeKind.Utc));

    /// <summary>Why this decision does not count, or null when it is acceptable. A refused decision changes nothing.</summary>
    private static string? Refusal(ApprovalRequest request, Decision? decision)
    {
        if (decision is null || string.IsNullOrWhiteSpace(decision.Approver))
        {
            return "RefusedMissingApprover";
        }

        if (string.Equals(decision.Approver, request.Requester, StringComparison.OrdinalIgnoreCase))
        {
            return "RefusedSelfApproval";
        }

        if (!string.Equals(decision.Approver, request.Approver, StringComparison.OrdinalIgnoreCase))
        {
            return "RefusedWrongApprover";
        }

        if (decision.Version != request.Version)
        {
            return "RefusedStaleVersion";
        }

        if (ApprovalOutcome.ToStatus(decision.Outcome) is null)
        {
            return "RefusedInvalidOutcome";
        }

        return ApprovalRules.CommentProblem(decision.Outcome, decision.Comment) is null ? null : "RefusedComment";
    }
}

public sealed class RecordSubmission(ILogger<RecordSubmission> logger)
    : TaskActivity<ApprovalRequest, string>
{
    public override Task<string> RunAsync(TaskActivityContext context, ApprovalRequest input)
    {
        input.Validate();
        using Activity? span = ApprovalTelemetry.StartSpan("approval.submitted", input.Tags("policy-chain"),
            ("role", input.Step.Role), ("approver", input.Approver));
        logger.LogInformation(
            "RecordSubmission instance={InstanceId} plan={PlanId} version={Version} correlation={CorrelationId}",
            context.InstanceId, input.PlanId, input.Version, input.Step.CorrelationId);
        return Task.FromResult(input.PlanId);
    }
}

/// <summary>
/// The place a reviewer step's final record (who, what, when, why) leaves the workflow. For the spike it is a
/// structured log line and a span. A real system writes it to an audit store outside the scheduler, because the
/// scheduler's own history is purged.
/// </summary>
public sealed class RecordDecision(ILogger<RecordDecision> logger)
    : TaskActivity<RecordDecisionRequest, string>
{
    public override Task<string> RunAsync(TaskActivityContext context, RecordDecisionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Request.Validate();
        ApprovalResult result = input.Result ?? throw new ArgumentException("A decision record needs a result.");
        if (!ApprovalStatus.IsKnown(result.Status))
        {
            throw new ArgumentException("A decision record needs a known status.");
        }

        ApprovalRequest request = input.Request;
        using Activity? span = ApprovalTelemetry.StartSpan("approval.decision.recorded", request.Tags("policy-chain"),
            ("role", request.Step.Role), ("approver", request.Approver), ("status", result.Status),
            ("decidedBy", result.DecidedBy ?? string.Empty));
        logger.LogInformation(
            "Decision recorded instance={InstanceId} correlation={CorrelationId} client={ClientId} account={AccountId} " +
            "plan={PlanId} version={Version} stage={Stage} role={Role} approver={Approver} status={Status} " +
            "decidedBy={DecidedBy} decidedAt={DecidedAt} comment={Comment}",
            context.InstanceId, request.Step.CorrelationId, request.Step.ClientId, request.Step.AccountId,
            request.PlanId, request.Version, request.Step.Stage, request.Step.Role, request.Approver, result.Status,
            result.DecidedBy, result.DecidedAt, result.Comment);
        return Task.FromResult(result.Status);
    }
}

public sealed class ApplyDecision(ILogger<ApplyDecision> logger)
    : TaskActivity<ApplyDecisionRequest, string>
{
    public override async Task<string> RunAsync(TaskActivityContext context, ApplyDecisionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Receipt);
        ApprovalRules.ValidateDelay(input.DelaySeconds);
        logger.LogInformation("ApplyDecision attempt instance={InstanceId} receipt={Receipt} delaySeconds={DelaySeconds}",
            context.InstanceId, input.Receipt, input.DelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(input.DelaySeconds));
        return input.Receipt;
    }
}
