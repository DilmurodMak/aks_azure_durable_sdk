using Approval.Client;
using Approval.Contracts;
using Microsoft.DurableTask.Client;

namespace Approval.Console;

public interface IApprovalConsoleService
{
    Task<object> DecideAsync(string id, DecisionForm decision, CancellationToken cancellation);
    Task<object> StatusAsync(string id, bool history, CancellationToken cancellation);
}

public sealed class ApprovalConsoleService(DurableTaskClient client) : IApprovalConsoleService
{
    public Task<object> DecideAsync(string id, DecisionForm decision, CancellationToken cancellation)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.ActingAs);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Outcome);
        return ClientOperations.ExecuteAsync(client,
            new ClientCommand("decide", id, Decision: new Decision(decision.ActingAs, decision.Outcome, decision.Version)),
            cancellation);
    }

    public Task<object> StatusAsync(string id, bool history, CancellationToken cancellation)
    {
        ValidateId(id);
        return ClientOperations.ExecuteAsync(client,
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
