using Approval.Contracts;
using Microsoft.DurableTask;

namespace Approval.Worker;

public sealed class ApprovalChain : TaskOrchestrator<ApprovalChainRequest, ApprovalChainResult>
{
    public override async Task<ApprovalChainResult> RunAsync(
        TaskOrchestrationContext context, ApprovalChainRequest input)
    {
        Validate(input, context.InstanceId);
        ResolvedApprovalPolicy policy = input.Policy;
        int total = policy.Stages.Sum(stage => stage.Approvals.Length);
        int completed = 0;
        List<ApprovalStepResult> steps = [];
        string? endedWith = null;
        int lastStage = 0;

        for (int stageIndex = 0; stageIndex < policy.Stages.Length && endedWith is null; stageIndex++)
        {
            lastStage = stageIndex + 1;
            ResolvedPolicyStage stage = policy.Stages[stageIndex];
            ApprovalProgress progress = new("Running", stage.Name, stageIndex + 1, policy.Stages.Length,
                completed, total);
            context.SetCustomStatus(progress);
            List<(string InstanceId, ResolvedRole Role, Task<ApprovalResult> Task)> children = [];
            for (int roleIndex = 0; roleIndex < stage.Approvals.Length; roleIndex++)
            {
                ResolvedRole role = stage.Approvals[roleIndex];
                string instanceId = ApprovalNames.StepInstanceId(context.InstanceId, stageIndex + 1, roleIndex + 1);
                ApprovalStepContext step = new(context.InstanceId, policy.ClientId, policy.AccountId,
                    policy.WorkflowCode, policy.Id, policy.Version, stage.Name, stageIndex + 1,
                    policy.Stages.Length, role.Role, input.CorrelationId);
                ApprovalRequest request = new(input.PlanId, input.Version, input.Requester, role.Approver,
                    stage.TimeoutMinutes, step);
                TaskOptions options = new(retry: null, tags: request.Tags("policy-chain"));
                Task<ApprovalResult> child = context.CallSubOrchestratorAsync<ApprovalResult>(
                    ApprovalNames.StepOrchestration, request, new SubOrchestrationOptions(options, instanceId));
                children.Add((instanceId, role, child));
            }

            List<Task<ApprovalResult>> pending = children.Select(child => child.Task).ToList();
            while (pending.Count > 0)
            {
                Task<ApprovalResult> finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                context.SetCustomStatus(progress with { Completed = ++completed });
                if (endedWith is null && finished.IsCompletedSuccessfully && ApprovalStatus.EndsChain(finished.Result.Status))
                {
                    // One rejection, return, or timeout ends the plan: stop the reviewers still waiting in this stage.
                    endedWith = finished.Result.Status;
                    foreach (var child in children.Where(child => !child.Task.IsCompleted))
                    {
                        context.SendEvent(child.InstanceId, ApprovalNames.CancelEvent, endedWith);
                    }
                }
            }

            // Observe faults only after every child in this stage has settled.
            ApprovalResult[] results = await Task.WhenAll(children.Select(child => child.Task));
            for (int roleIndex = 0; roleIndex < children.Count; roleIndex++)
            {
                ApprovalResult result = results[roleIndex];
                if (!ApprovalStatus.IsKnown(result.Status))
                {
                    throw new InvalidOperationException("An approval step returned an unknown status.");
                }

                var child = children[roleIndex];
                steps.Add(new(child.InstanceId, stage.Name, child.Role.Role, child.Role.Approver, result.Status,
                    result.DecidedBy, result.DecidedAt, result.Comment));
            }

            context.SetCustomStatus(progress with { Completed = completed });
        }

        string status = endedWith ?? ApprovalStatus.Approved;
        string[] notStarted = policy.Stages.Skip(lastStage).Select(stage => stage.Name).ToArray();
        string? receipt = status == ApprovalStatus.Approved
            ? await context.CallActivityAsync<string>(nameof(ApplyDecision),
                new ApplyDecisionRequest($"{input.PlanId}:v{input.Version}:approved", input.ApplyDecisionDelaySeconds))
            : null;
        context.SetCustomStatus(new ApprovalProgress(status, policy.Stages[lastStage - 1].Name, lastStage,
            policy.Stages.Length, completed, total));
        return new(status, input.Version, policy.Id, policy.Version, steps.ToArray(), notStarted, receipt);
    }

    private static void Validate(ApprovalChainRequest input, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ApprovalRules.ValidatePlan(input.PlanId, input.Version, input.Requester);
        ArgumentNullException.ThrowIfNull(input.Routing);
        ApprovalRules.ValidateBudget(input.Routing.BudgetAmount);
        ApprovalRules.ValidateCorrelationId(input.CorrelationId);
        ApprovalRules.ValidateDelay(input.ApplyDecisionDelaySeconds);
        ApprovalRules.ValidateParentId(instanceId);
        ResolvedApprovalPolicy policy = input.Policy;
        ArgumentNullException.ThrowIfNull(policy);
        ApprovalRules.ValidatePolicyKeys(policy.Id, policy.ClientId, policy.AccountId, policy.WorkflowCode, policy.Version);
        if (policy.Stages is null || policy.Stages.Length is < 1 or > 10)
        {
            throw new ArgumentException("A policy snapshot needs 1-10 stages.");
        }

        HashSet<string> stageNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedPolicyStage stage in policy.Stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            ApprovalPolicy.ValidateKey(stage.Name, "Stage name");
            if (!stageNames.Add(stage.Name) || stage.Approvals is null || stage.Approvals.Length is < 1 or > 10)
            {
                throw new ArgumentException("Stage names must be unique; each stage needs 1-10 approvals.");
            }

            ApprovalRules.ValidateTimeout(stage.TimeoutMinutes);
            HashSet<string> roles = new(StringComparer.OrdinalIgnoreCase);
            foreach (ResolvedRole role in stage.Approvals)
            {
                ArgumentNullException.ThrowIfNull(role);
                ApprovalPolicy.ValidateKey(role.Role, "Role");
                ArgumentException.ThrowIfNullOrWhiteSpace(role.Approver);
                if (!roles.Add(role.Role))
                {
                    throw new ArgumentException("Roles within a stage must be unique.");
                }
            }
        }

        ApprovalRules.ValidateSeparationOfDuties(input.Requester, policy.Stages);
    }
}
