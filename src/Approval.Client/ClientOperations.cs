using System.Text.Json;
using System.Text.Json.Serialization;
using Approval.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace Approval.Client;

public static class ClientOperations
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<object> ExecuteAsync(
        DurableTaskClient client, ClientCommand command, CancellationToken cancellation)
    {
        if (command.Name is not ("status" or "decide"))
            throw new ArgumentException("Only decide and status are supported. Start through the policy-driven workflow endpoint.");

        OrchestrationMetadata metadata = await client.GetInstanceAsync(
            command.InstanceId, getInputsAndOutputs: true, cancellation)
            ?? throw new InvalidOperationException($"Instance {command.InstanceId} was not found.");
        if (metadata.Name != ApprovalNames.StepOrchestration && metadata.Name != ApprovalNames.ChainOrchestration)
        {
            throw new InvalidOperationException($"Instance {command.InstanceId} is not a {ApprovalNames.StepOrchestration} workflow.");
        }

        ApprovalProgress? customStatus = metadata.ReadCustomStatusAs<ApprovalProgress>();
        if (command.Name == "decide")
        {
            if (metadata.Name != ApprovalNames.StepOrchestration ||
                metadata.RuntimeStatus != OrchestrationRuntimeStatus.Running ||
                customStatus?.State != "Waiting")
            {
                throw new InvalidOperationException(
                    $"Instance {command.InstanceId} is not waiting for a decision ({metadata.RuntimeStatus}, {customStatus}).");
            }

            Decision decision = command.Decision
                ?? throw new ArgumentException("Decide requires an explicit decision.");
            ApprovalRequest input = metadata.ReadInputAs<ApprovalRequest>()
                ?? throw new InvalidDataException("Approval input is missing.");
            ApprovalStepContext step = input.Step
                ?? throw new InvalidOperationException("Reviewer is not part of a policy-driven workflow.");
            OrchestrationMetadata parent = await client.GetInstanceAsync(
                step.ParentInstanceId, true, cancellation)
                ?? throw new InvalidOperationException("Parent workflow was not found.");
            if (parent.Name != ApprovalNames.ChainOrchestration ||
                parent.RuntimeStatus != OrchestrationRuntimeStatus.Running ||
                parent.ReadCustomStatusAs<ApprovalProgress>()?.StageNumber != step.StageNumber)
                throw new InvalidOperationException("This approval group is not active in its parent workflow.");
            await client.RaiseEventAsync(command.InstanceId, ApprovalNames.DecisionEvent, decision, cancellation);
            return new
            {
                instanceId = command.InstanceId,
                eventName = ApprovalNames.DecisionEvent,
                decision,
                status = "DecisionSentNotYetApplied"
            };
        }

        JsonElement[]? history = null;
        if (command.IncludeHistory)
        {
            var events = await client.GetOrchestrationHistoryAsync(command.InstanceId, cancellation);
            history = events.Select(item => JsonSerializer.SerializeToElement(item, item.GetType(), JsonOptions)).ToArray();
        }

        object? output = null;
        if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
        {
            if (metadata.Name == ApprovalNames.ChainOrchestration)
                output = metadata.ReadOutputAs<ApprovalChainResult>();
            else
                output = metadata.ReadOutputAs<ApprovalResult>();
        }

        return new
        {
            instanceId = metadata.InstanceId,
            runtimeStatus = metadata.RuntimeStatus.ToString(),
            customStatus,
            result = output,
            failure = metadata.FailureDetails?.ErrorMessage,
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            history
        };
    }
}
