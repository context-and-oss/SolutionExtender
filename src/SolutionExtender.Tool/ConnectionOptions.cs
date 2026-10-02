using Azure.Identity;
using DataverseConnection;

namespace SolutionExtender.Tool;

/// <summary>Maps CLI authentication selection without replacing library-managed interactive caching.</summary>
internal static class ConnectionOptions
{
    /// <summary>Builds connection options and rejects overrides that bypass library caching.</summary>
    /// <param name="options">The validated command-line options.</param>
    /// <param name="environment">The Dataverse environment URL.</param>
    /// <returns>Connection options with interactive credential options left unset.</returns>
    public static DataverseOptions Create(IReadOnlyDictionary<string, string?> options, string environment)
    {
        var credential = (options.GetValueOrDefault("auth") ?? "interactive") switch
        {
            "azcli" => DataverseCredentialType.AzureCliCredential,
            "devicecode" => DataverseCredentialType.DeviceCodeCredential,
            "interactive" or "browser" => DataverseCredentialType.InteractiveBrowserCredential,
            _ => throw new ArgumentException("--auth must be azcli, devicecode, or interactive.", nameof(options)),
        };
        if (options.ContainsKey("client-id"))
        {
            throw new ArgumentException("--client-id is not supported. DataverseConnection manages the interactive credential and its environment-specific cache.", nameof(options));
        }

        options.TryGetValue("tenant-id", out var tenant);
        if (tenant is not null && credential != DataverseCredentialType.AzureCliCredential)
        {
            throw new ArgumentException("--tenant-id is only supported with --auth azcli. Browser/device-code authentication uses DataverseConnection's credential and cache defaults.", nameof(options));
        }

        // Supplying interactive options bypasses DataverseConnection's persistent credential wrapper,
        // environment-specific cache, authentication record and persistence fallback. Leave them null.
        return new DataverseOptions
        {
            DataverseUrl = environment,
            CredentialType = credential,
            AzureCliCredentialOptions = tenant is null ? null : new AzureCliCredentialOptions { TenantId = tenant },
        };
    }
}
