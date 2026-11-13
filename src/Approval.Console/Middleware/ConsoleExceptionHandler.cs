using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Approval.Console.Middleware;

public sealed class ConsoleExceptionHandler(ILogger<ConsoleExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellation)
    {
        (int status, string title) = exception switch
        {
            ArgumentException => (400, "Invalid approval request"),
            BadHttpRequestException => (400, "Invalid JSON request"),
            KeyNotFoundException => (404, "Approval not found"),
            MissingDemoIdentityException missing => (409, $"Assign a demo actor for role {missing.Role} in Demo identities before starting."),
            InvalidOperationException => (409, "Approval is not in the required state or already exists"),
            RpcException { StatusCode: Grpc.Core.StatusCode.AlreadyExists } => (409, "Approval already exists"),
            RpcException { StatusCode: Grpc.Core.StatusCode.NotFound } => (404, "Approval not found"),
            RpcException => (502, "Scheduler request failed; check the console logs"),
            OperationCanceledException => (504, "Scheduler request timed out; query status before retrying"),
            _ => (500, "Console request failed; check the console logs")
        };
        logger.LogError(exception, "Console request failed: {Status} {Title}", status, title);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = context.Request.Path
        }, cancellation);
        return true;
    }
}
