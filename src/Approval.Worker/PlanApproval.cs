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

        using CancellationTokenSource cancellation = new();
        Task<Decision> decisionTask = context.WaitForExternalEvent<Decision>(
            ApprovalNames.DecisionEvent, cancellation.Token);
        Task timer = context.CreateTimer(
            context.CurrentUtcDateTime.AddMinutes(input.TimeoutMinutes), cancellation.Token);

        ApprovalResult result;
        if (await Task.WhenAny(decisionTask, timer) == timer)
        {
            await timer;
            cancellation.Cancel();
            result = new ApprovalResult("Timeout", input.Version, null);
        }
        else
        {
            cancellation.Cancel();
            Decision decision = await decisionTask;
            string status = EvaluateDecision(input, decision);
            string? receipt = status == "Approved"
                ? await context.CallActivityAsync<string>(
                    nameof(ApplyDecision), $"{input.PlanId}:v{input.Version}:approved")
                : null;
            result = new ApprovalResult(status, input.Version, receipt);
        }

        context.SetCustomStatus(result.Status);
        context.CreateReplaySafeLogger<PlanApproval>()
            .LogInformation("Instance {InstanceId} finished with {Status}, version {Version}, receipt {Receipt}",
                context.InstanceId, result.Status, result.Version, result.Receipt);
        return result;
    }

    public static string EvaluateDecision(ApprovalRequest request, Decision? decision)
    {
        if (decision is null || string.IsNullOrWhiteSpace(decision.Approver))
        {
            return "Rejected";
        }

        if (string.Equals(decision.Approver, request.Requester, StringComparison.OrdinalIgnoreCase))
        {
            return "RejectedSelfApproval";
        }

        if (!string.Equals(decision.Approver, request.Approver, StringComparison.OrdinalIgnoreCase))
        {
            return "RejectedWrongApprover";
        }

        if (decision.Version != request.Version)
        {
            return "RejectedStaleVersion";
        }

        return decision.Outcome == "Approve" ? "Approved" : "Rejected";
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

public sealed class ApplyDecision(ILogger<ApplyDecision> logger) : TaskActivity<string, string>
{
    public override Task<string> RunAsync(TaskActivityContext context, string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        logger.LogInformation("ApplyDecision instance={InstanceId} receipt={Receipt}", context.InstanceId, input);
        return Task.FromResult(input);
    }
}
