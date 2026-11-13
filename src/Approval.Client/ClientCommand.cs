using System.Globalization;
using Approval.Contracts;

namespace Approval.Client;

public sealed record ClientCommand(
    string Name,
    string InstanceId,
    Decision? Decision = null,
    bool IncludeHistory = false)
{
    public const string Usage = """
        decide --instance ID --approver ID --version N --outcome Approve|Reject
        status --instance ID [--history]
        Start all workflows through POST /api/workflows or the Local Console UI using a saved policy.
        Only exact Approve or Reject completes. Invalid decisions are refused and the workflow keeps waiting.
        """;

    public static ClientCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("decide" or "status"))
        {
            throw new ArgumentException("Expected decide or status. Start workflows through the console using a saved policy.");
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
        return new ClientCommand("decide", instanceId,
            Decision: new Decision(approver, Required(values, "--outcome"), version));
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
