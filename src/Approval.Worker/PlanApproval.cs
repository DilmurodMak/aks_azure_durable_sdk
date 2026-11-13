using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace Approval.Worker;

public sealed class PlanApproval : TaskOrchestrator<ApprovalRequest, ApprovalResult>
{
    public override async Task<ApprovalResult> RunAsync(TaskOrchestrationContext context, ApprovalRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        await context.CallActivityAsync<string>(nameof(RecordSubmission), input);
        context.SetCustomStatus(ApprovalNames.WaitingStatus);
        ILogger logger = context.CreateReplaySafeLogger<PlanApproval>();

        using CancellationTokenSource cancellation = new();
        DateTime deadline = context.CurrentUtcDateTime.AddMinutes(input.TimeoutMinutes);
        Task timer = context.CreateTimer(deadline, cancellation.Token);

        ApprovalResult result;
        while (true)
        {
            Task<Decision> decisionTask = context.WaitForExternalEvent<Decision>(
                ApprovalNames.DecisionEvent, cancellation.Token);
            if (await Task.WhenAny(timer, decisionTask) == timer)
            {
                await timer;
                cancellation.Cancel();
                result = new ApprovalResult("Timeout", input.Version, null);
                break;
            }

            Decision decision = await decisionTask;
            string status = EvaluateDecision(input, decision);
            if (status is not ("Approved" or "Rejected"))
            {
                logger.LogWarning(
                    "Instance {InstanceId} refused decision: {Reason}; status {Status}; waiting until {Deadline}",
                    context.InstanceId, status, ApprovalNames.WaitingStatus, deadline);
                continue;
            }

            cancellation.Cancel();
            string? receipt = status == "Approved"
                ? await context.CallActivityAsync<string>(
                    nameof(ApplyDecision),
                    new ApplyDecisionRequest(
                        $"{input.PlanId}:v{input.Version}:approved", input.ApplyDecisionDelaySeconds))
                : null;
            result = new ApprovalResult(status, input.Version, receipt);
            break;
        }

        context.SetCustomStatus(result.Status);
        logger.LogInformation("Instance {InstanceId} finished with {Status}, version {Version}, receipt {Receipt}",
            context.InstanceId, result.Status, result.Version, result.Receipt);
        return result;
    }

    private static string EvaluateDecision(ApprovalRequest request, Decision? decision)
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

        return decision.Outcome switch
        {
            "Approve" => "Approved",
            "Reject" => "Rejected",
            _ => "RefusedInvalidOutcome",
        };
    }
}

public sealed class RecordSubmission(ILogger<RecordSubmission> logger)
    : TaskActivity<ApprovalRequest, string>
{
    public override Task<string> RunAsync(TaskActivityContext context, ApprovalRequest input)
    {
        input.Validate();
        logger.LogInformation("RecordSubmission instance={InstanceId} plan={PlanId} version={Version}",
            context.InstanceId, input.PlanId, input.Version);
        return Task.FromResult(input.PlanId);
    }
}

public sealed class ApplyDecision(ILogger<ApplyDecision> logger)
    : TaskActivity<ApplyDecisionRequest, string>
{
    public override async Task<string> RunAsync(TaskActivityContext context, ApplyDecisionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Receipt);
        if (input.DelaySeconds is < 0 or > 60)
        {
            throw new ArgumentException("DelaySeconds must be between zero and 60.");
        }

        logger.LogInformation("ApplyDecision attempt instance={InstanceId} receipt={Receipt} delaySeconds={DelaySeconds}",
            context.InstanceId, input.Receipt, input.DelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(input.DelaySeconds));
        return input.Receipt;
    }
}
