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
        if (command.Name == "start")
        {
            ApprovalRequest request = command.Request
                ?? throw new ArgumentException("Start requires an approval request.");
            request.Validate();
            if (await client.GetInstanceAsync(command.InstanceId, cancellation) is not null)
            {
                throw new InvalidOperationException($"Instance {command.InstanceId} already exists. Use a fresh ID; do not overwrite its history.");
            }

            StartOrchestrationOptions options = new StartOrchestrationOptions(command.InstanceId)
                .WithDedupeStatuses(Enum.GetValues<OrchestrationRuntimeStatus>());
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                ApprovalNames.Orchestration, request, options, cancellation);
            return new { instanceId, orchestration = ApprovalNames.Orchestration, request };
        }

        OrchestrationMetadata metadata = await client.GetInstanceAsync(
            command.InstanceId, getInputsAndOutputs: true, cancellation)
            ?? throw new InvalidOperationException($"Instance {command.InstanceId} was not found.");
        if (metadata.Name != ApprovalNames.Orchestration)
        {
            throw new InvalidOperationException($"Instance {command.InstanceId} is not a {ApprovalNames.Orchestration} workflow.");
        }

        string? customStatus = metadata.ReadCustomStatusAs<string>();
        if (command.Name == "decide")
        {
            if (metadata.RuntimeStatus != OrchestrationRuntimeStatus.Running ||
                customStatus != ApprovalNames.WaitingStatus)
            {
                throw new InvalidOperationException(
                    $"Instance {command.InstanceId} is not waiting for a decision ({metadata.RuntimeStatus}, {customStatus}).");
            }

            Decision decision = command.Decision
                ?? throw new ArgumentException("Decide requires an explicit decision.");
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

        return new
        {
            instanceId = metadata.InstanceId,
            runtimeStatus = metadata.RuntimeStatus.ToString(),
            customStatus,
            result = metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed
                ? metadata.ReadOutputAs<ApprovalResult>()
                : null,
            failure = metadata.FailureDetails?.ErrorMessage,
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            history
        };
    }
}
