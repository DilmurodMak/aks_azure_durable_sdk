namespace Approval.Contracts;

public sealed record SchedulerConnection(string Endpoint, string TaskHub, string Authentication)
{
    public string ConnectionString => $"Endpoint={Endpoint};TaskHub={TaskHub};Authentication={Authentication}";

    public static SchedulerConnection FromEnvironment() => Create(Environment.GetEnvironmentVariable);

    public static SchedulerConnection Create(Func<string, string?> environment)
    {
        string endpoint = environment("ENDPOINT")
            ?? throw new ArgumentException("Set ENDPOINT explicitly; there is no emulator fallback.");
        string taskHub = environment("TASKHUB")
            ?? throw new ArgumentException("Set TASKHUB explicitly.");
        string authentication = environment("SCHEDULER_AUTH")
            ?? throw new ArgumentException("Set SCHEDULER_AUTH to AzureCli, WorkloadIdentity, or None.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? address) ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            address.AbsolutePath != "/" ||
            endpoint.Contains(';'))
        {
            throw new ArgumentException("ENDPOINT must be a Scheduler origin URL, not a connection string.");
        }

        if (string.IsNullOrWhiteSpace(taskHub) ||
            taskHub.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("TASKHUB must contain only ASCII letters, digits, hyphens, or underscores.");
        }

        if (authentication == "None")
        {
            if (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp)
            {
                throw new ArgumentException("Authentication=None is allowed only for a loopback HTTP emulator.");
            }
        }
        else
        {
            if (authentication is not "AzureCli" and not "WorkloadIdentity" || address.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Azure Scheduler requires HTTPS and AzureCli or WorkloadIdentity authentication.");
            }

            if (authentication == "WorkloadIdentity" &&
                (!Guid.TryParse(environment("AZURE_CLIENT_ID"), out _) ||
                 !Guid.TryParse(environment("AZURE_TENANT_ID"), out _) ||
                 string.IsNullOrWhiteSpace(environment("AZURE_FEDERATED_TOKEN_FILE"))))
            {
                throw new ArgumentException("WorkloadIdentity requires AZURE_CLIENT_ID, AZURE_TENANT_ID, and AZURE_FEDERATED_TOKEN_FILE.");
            }
        }

        return new SchedulerConnection(address.GetLeftPart(UriPartial.Authority), taskHub, authentication);
    }
}
