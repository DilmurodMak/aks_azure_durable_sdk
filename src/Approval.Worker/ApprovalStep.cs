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

        ApprovalResult result;
        try
        {
            while (true)
            {
                Task<Decision> decisionTask = context.WaitForExternalEvent<Decision>(
                    ApprovalNames.DecisionEvent, cancellation.Token);
                if (await Task.WhenAny(timer, decisionTask) == timer)
                {
                    await timer;
                    result = new ApprovalResult("Timeout", input.Version);
                    break;
                }

                Decision decision = await decisionTask;
                string status = EvaluateDecision(input, decision);
                if (status is not ("Approved" or "Rejected"))
                {
                    logger.LogWarning(
                        "Instance {InstanceId} refused decision: {Reason}; status {Status}; waiting until {Deadline}",
                        context.InstanceId, status, progress.Display, deadline);
                    continue;
                }

                result = new ApprovalResult(status, input.Version);
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

        context.SetCustomStatus(progress with { State = result.Status, Completed = 1 });
        logger.LogInformation("Instance {InstanceId} finished with {Status}, version {Version}",
            context.InstanceId, result.Status, result.Version);
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
        ApprovalRules.ValidateDelay(input.DelaySeconds);
        logger.LogInformation("ApplyDecision attempt instance={InstanceId} receipt={Receipt} delaySeconds={DelaySeconds}",
            context.InstanceId, input.Receipt, input.DelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(input.DelaySeconds));
        return input.Receipt;
    }
}
