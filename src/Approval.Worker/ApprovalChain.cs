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

        for (int stageIndex = 0; stageIndex < policy.Stages.Length; stageIndex++)
        {
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
                    policy.Stages.Length, role.Role);
                ApprovalRequest request = new(input.PlanId, input.Version, input.Requester, role.Approver,
                    stage.TimeoutMinutes, step);
                TaskOptions options = new(retry: null, tags: ApprovalTags.Create(
                    policy.ClientId, policy.WorkflowCode, input.PlanId, input.Version, "policy-chain",
                    stage.Name, policy.Id, policy.Version, context.InstanceId));
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
            }

            // Observe faults only after every child in this stage has settled.
            ApprovalResult[] results = await Task.WhenAll(children.Select(child => child.Task));
            for (int roleIndex = 0; roleIndex < children.Count; roleIndex++)
            {
                ApprovalResult result = results[roleIndex];
                if (result.Status is not ("Approved" or "Rejected" or "Timeout"))
                {
                    throw new InvalidOperationException("An approval step returned an unknown status.");
                }

                var child = children[roleIndex];
                steps.Add(new(child.InstanceId, stage.Name, child.Role.Role, child.Role.Approver, result.Status));
            }

            context.SetCustomStatus(progress with { Completed = completed });
        }

        string status = steps.Any(step => step.Status == "Rejected") ? "Rejected"
            : steps.Any(step => step.Status == "Timeout") ? "Timeout" : "Approved";
        string? receipt = status == "Approved"
            ? await context.CallActivityAsync<string>(nameof(ApplyDecision),
                new ApplyDecisionRequest($"{input.PlanId}:v{input.Version}:approved", input.ApplyDecisionDelaySeconds))
            : null;
        context.SetCustomStatus(new ApprovalProgress(status, policy.Stages[^1].Name, policy.Stages.Length,
            policy.Stages.Length, completed, total));
        return new(status, input.Version, policy.Id, policy.Version, steps.ToArray(), receipt);
    }

    private static void Validate(ApprovalChainRequest input, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ApprovalRules.ValidatePlan(input.PlanId, input.Version, input.Requester);
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
    }
}
