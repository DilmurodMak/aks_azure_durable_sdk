using System.Text.Json;

namespace Approval.Contracts;

public static class ApprovalJson
{
    public static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };
}
