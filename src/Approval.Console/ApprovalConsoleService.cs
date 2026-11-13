using Approval.Client;
using Approval.Contracts;
using Microsoft.DurableTask.Client;

namespace Approval.Console;

public interface IApprovalConsoleService
{
    Task<IReadOnlyList<WaitingApprovalResponse>> ListAsync(CancellationToken cancellation);
    Task<object> DecideAsync(string id, DecisionForm decision, CancellationToken cancellation);
    Task<object> StatusAsync(string id, bool history, CancellationToken cancellation);
}

public sealed class ApprovalConsoleService(DurableTaskClient client) : IApprovalConsoleService
{
    public async Task<IReadOnlyList<WaitingApprovalResponse>> ListAsync(CancellationToken cancellation)
    {
        List<WaitingApprovalResponse> waiting = [];
        OrchestrationQuery query = new(Statuses: [OrchestrationRuntimeStatus.Running], FetchInputsAndOutputs: true);
        await foreach (OrchestrationMetadata metadata in client.GetAllInstancesAsync(query).WithCancellation(cancellation))
        {
            if (metadata.Name != ApprovalNames.StepOrchestration ||
                metadata.ReadCustomStatusAs<ApprovalProgress>()?.State != "Waiting")
            {
                continue;
            }

            ApprovalRequest request = metadata.ReadInputAs<ApprovalRequest>()
                ?? throw new InvalidOperationException("Waiting approval has missing input.");
            waiting.Add(new(metadata.InstanceId, request.PlanId, request.Version, request.Requester,
                request.Approver, metadata.CreatedAt, metadata.ReadCustomStatusAs<ApprovalProgress>(),
                request.Step?.ParentInstanceId, request.Step?.PolicyVersion, request.Step?.Role));
        }

        return waiting;
    }

    public Task<object> DecideAsync(string id, DecisionForm decision, CancellationToken cancellation)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.ActingAs);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Outcome);
        return ClientOperations.ExecuteAsync(client,
            new ClientCommand("decide", id, Decision: new Decision(decision.ActingAs, decision.Outcome, decision.Version)),
            cancellation);
    }

    public async Task<object> StatusAsync(string id, bool history, CancellationToken cancellation)
    {
        ValidateId(id);
        if (await client.GetInstanceAsync(id, cancellation) is null)
        {
            throw new KeyNotFoundException("Approval was not found.");
        }

        return await ClientOperations.ExecuteAsync(client,
            new ClientCommand("status", id, IncludeHistory: history), cancellation);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Instance ID must contain 1-100 ASCII letters, digits, or hyphens.");
        }
    }
}
