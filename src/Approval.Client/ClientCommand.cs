using System.Globalization;
using Approval.Contracts;

namespace Approval.Client;

public sealed record ClientCommand(
    string Name,
    string InstanceId,
    ApprovalRequest? Request = null,
    Decision? Decision = null,
    bool IncludeHistory = false)
{
    public const string Usage = """
        start  --instance ID --plan PLAN --version N --requester ID --approver ID [--timeout-minutes 15]
        decide --instance ID --approver ID --version N --outcome Approve|Reject
        status --instance ID [--history]
        Only the exact outcome Approve approves. Other non-empty outcomes are rejected by the workflow.
        """;

    public static ClientCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("start" or "decide" or "status"))
        {
            throw new ArgumentException("Expected start, decide, or status.");
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            string value;
            if (option == "--history")
            {
                value = "true";
            }
            else if (!option.StartsWith("--", StringComparison.Ordinal) ||
                     index + 1 >= args.Length ||
                     args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }
            else
            {
                value = args[++index];
            }

            if (!values.TryAdd(option, value))
            {
                throw new ArgumentException($"Duplicate option: {option}.");
            }
        }

        string[] allowed = args[0] switch
        {
            "start" => ["--instance", "--plan", "--version", "--requester", "--approver", "--timeout-minutes"],
            "decide" => ["--instance", "--approver", "--version", "--outcome"],
            _ => ["--instance", "--history"]
        };
        foreach (string key in values.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new ArgumentException($"Unknown option for {args[0]}: {key}.");
            }
        }

        string instanceId = Required(values, "--instance");
        if (instanceId.Length > 100 ||
            instanceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Instance ID must contain 1-100 ASCII letters, digits, or hyphens.");
        }

        if (args[0] == "status")
        {
            return new ClientCommand("status", instanceId, IncludeHistory: values.ContainsKey("--history"));
        }

        if (!int.TryParse(Required(values, "--version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            version < 1)
        {
            throw new ArgumentException("--version must be a positive integer.");
        }

        string approver = Required(values, "--approver");
        if (args[0] == "decide")
        {
            return new ClientCommand("decide", instanceId,
                Decision: new Decision(approver, Required(values, "--outcome"), version));
        }

        string timeoutValue = values.GetValueOrDefault("--timeout-minutes", "15");
        if (!double.TryParse(timeoutValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double timeoutMinutes))
        {
            throw new ArgumentException("--timeout-minutes must be a number.");
        }

        ApprovalRequest request = new(
            Required(values, "--plan"), version, Required(values, "--requester"), approver, timeoutMinutes);
        request.Validate();
        return new ClientCommand("start", instanceId, request);
    }

    private static string Required(Dictionary<string, string> values, string option)
    {
        if (!values.TryGetValue(option, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A non-empty {option} is required.");
        }

        return value;
    }
}
