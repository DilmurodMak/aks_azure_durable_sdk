using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace Approval.Console;

public interface IApprovalChainService
{
    Task<IReadOnlyList<WorkflowSummaryResponse>> ListAsync(CancellationToken cancellation);
    Task<IReadOnlyList<WorkflowApprovalResponse>> StepsAsync(string id, CancellationToken cancellation);
    Task<object> StartAsync(StartChainRequest request, CancellationToken cancellation);
    Task<object> StatusAsync(string id, CancellationToken cancellation);
}

public sealed class ApprovalChainService(
    DurableTaskClient client, IPolicyStore policies, IDemoIdentityStore identities) : IApprovalChainService
{
    public async Task<IReadOnlyList<WorkflowSummaryResponse>> ListAsync(CancellationToken cancellation)
    {
        List<WorkflowSummaryResponse> workflows = [];
        await foreach (OrchestrationMetadata metadata in client.GetAllInstancesAsync(
            new OrchestrationQuery(FetchInputsAndOutputs: true)).WithCancellation(cancellation))
        {
            if (metadata.Name != ApprovalNames.ChainOrchestration) continue;
            ApprovalChainRequest input = metadata.ReadInputAs<ApprovalChainRequest>()
                ?? throw new InvalidDataException("Workflow input is missing.");
            workflows.Add(new(metadata.InstanceId, input.PlanId, input.Version, metadata.RuntimeStatus.ToString(),
                input.Policy.Version, metadata.CreatedAt));
        }
        return workflows.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<WorkflowApprovalResponse>> StepsAsync(string id, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(id, "Instance ID");
        OrchestrationMetadata parent = await client.GetInstanceAsync(id, true, cancellation)
            ?? throw new KeyNotFoundException("Workflow was not found.");
        if (parent.Name != ApprovalNames.ChainOrchestration)
            throw new ArgumentException("Instance is not an approval chain.");
        ApprovalChainRequest input = parent.ReadInputAs<ApprovalChainRequest>()
            ?? throw new InvalidDataException("Workflow input is missing.");
        ApprovalProgress? parentProgress = parent.ReadCustomStatusAs<ApprovalProgress>();
        List<WorkflowApprovalResponse> rows = [];
        bool priorStagesFinished = true;
        for (int stageIndex = 0; stageIndex < input.Policy.Stages.Length; stageIndex++)
        {
            ResolvedPolicyStage stage = input.Policy.Stages[stageIndex];
            bool stageFinished = true;
            for (int reviewerIndex = 0; reviewerIndex < stage.Approvals.Length; reviewerIndex++)
            {
                ResolvedRole reviewer = stage.Approvals[reviewerIndex];
                string childId = ApprovalNames.StepInstanceId(id, stageIndex + 1, reviewerIndex + 1);
                OrchestrationMetadata? child = await client.GetInstanceAsync(childId, true, cancellation);
                if (child is not null && child.Name != ApprovalNames.StepOrchestration)
                    throw new InvalidDataException("Unexpected orchestration at a planned approval step ID.");
                ApprovalProgress? childProgress = child?.ReadCustomStatusAs<ApprovalProgress>();
                bool terminal = child is not null && child.RuntimeStatus is
                    OrchestrationRuntimeStatus.Completed or OrchestrationRuntimeStatus.Failed or OrchestrationRuntimeStatus.Terminated;
                stageFinished &= terminal;
                bool canDecide = priorStagesFinished && parent.RuntimeStatus == OrchestrationRuntimeStatus.Running &&
                    parentProgress?.StageNumber == stageIndex + 1 &&
                    child?.RuntimeStatus == OrchestrationRuntimeStatus.Running && childProgress?.State == "Waiting";
                string state = child?.RuntimeStatus == OrchestrationRuntimeStatus.Completed
                    ? child.ReadOutputAs<ApprovalResult>()?.Status ?? throw new InvalidDataException("Approval result is missing.")
                    : child is not null ? child.RuntimeStatus == OrchestrationRuntimeStatus.Running
                        ? childProgress?.State ?? "Starting" : child.RuntimeStatus.ToString()
                    : parent.RuntimeStatus is not (OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
                        ? "NotStarted" : priorStagesFinished ? "Queued" : "Blocked";
                string? reason = canDecide || terminal ? null
                    : parent.RuntimeStatus is not (OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
                        ? $"Workflow is {parent.RuntimeStatus}; decisions are not available."
                    : !priorStagesFinished ? $"Waiting for group {stageIndex} to finish."
                    : "Waiting for the worker to start this step.";
                rows.Add(new(childId, id, input.PlanId, input.Version, input.Requester, reviewer.Approver,
                    reviewer.Role, input.Policy.Version, FormattableString.Invariant($"{stageIndex + 1}.{reviewerIndex + 1}"),
                    state, canDecide, child is not null, reason,
                    childProgress ?? new(state, stage.Name, stageIndex + 1, input.Policy.Stages.Length)));
            }
            priorStagesFinished &= stageFinished;
        }
        return rows;
    }

    public async Task<object> StartAsync(StartChainRequest request, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(request.InstanceId, "Instance ID");
        ApprovalPolicy definition = await policies.ResolveAsync(
            request.ClientId, request.AccountId, request.WorkflowCode, cancellation);
        Dictionary<string, string> assignments = await identities.ResolveAsync(
            request.ClientId, request.AccountId, definition.Stages.SelectMany(stage => stage.Roles), cancellation);
        ResolvedApprovalPolicy policy = definition.Resolve(role => assignments[role]);
        new ApprovalRequest(request.PlanId, request.Version, request.Requester,
            "validation-only", 15, request.ApplyDecisionDelaySeconds).Validate();
        if (await client.GetInstanceAsync(request.InstanceId, cancellation) is not null)
            throw new InvalidOperationException("Instance already exists. Use a fresh ID.");
        ApprovalChainRequest input = new(request.PlanId, request.Version, request.Requester, policy,
            request.ApplyDecisionDelaySeconds);
        StartOrchestrationOptions options = new StartOrchestrationOptions(request.InstanceId)
        {
            Tags = ApprovalTags.Create(request.ClientId, request.WorkflowCode, request.PlanId,
                request.Version, "console", "chain", policy.Id, policy.Version)
        }.WithDedupeStatuses(Enum.GetValues<OrchestrationRuntimeStatus>());
        await client.ScheduleNewOrchestrationInstanceAsync(
            ApprovalNames.ChainOrchestration, input, options, cancellation);
        return new { instanceId = request.InstanceId, policyId = policy.Id, policyVersion = policy.Version, snapshot = policy };
    }

    public async Task<object> StatusAsync(string id, CancellationToken cancellation)
    {
        ApprovalPolicy.ValidateKey(id, "Instance ID");
        OrchestrationMetadata metadata = await client.GetInstanceAsync(id, true, cancellation)
            ?? throw new KeyNotFoundException("Workflow was not found.");
        if (metadata.Name != ApprovalNames.ChainOrchestration)
            throw new ArgumentException("Instance is not an approval chain.");
        ApprovalChainRequest input = metadata.ReadInputAs<ApprovalChainRequest>()
            ?? throw new InvalidDataException("Workflow input is missing.");
        return new
        {
            instanceId = id,
            runtimeStatus = metadata.RuntimeStatus.ToString(),
            progress = metadata.ReadCustomStatusAs<ApprovalProgress>(),
            policyId = input.Policy.Id,
            policyVersion = input.Policy.Version,
            snapshot = input.Policy,
            result = metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed
                ? metadata.ReadOutputAs<ApprovalChainResult>() : null,
            failure = metadata.FailureDetails?.ErrorMessage
        };
    }
}
