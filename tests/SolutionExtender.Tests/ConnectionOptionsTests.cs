using DataverseConnection;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

public sealed class ConnectionOptionsTests
{
    [Theory]
    [InlineData("interactive", DataverseCredentialType.InteractiveBrowserCredential)]
    [InlineData("browser", DataverseCredentialType.InteractiveBrowserCredential)]
    [InlineData("devicecode", DataverseCredentialType.DeviceCodeCredential)]
    [InlineData("azcli", DataverseCredentialType.AzureCliCredential)]
    public void PreservesLibraryCredentialAndCacheDefaults(string auth, DataverseCredentialType expected)
    {
        var options = ConnectionOptions.Create(new Dictionary<string, string?>(StringComparer.Ordinal) { ["auth"] = auth }, "https://example.crm.dynamics.com");
        Assert.Equal(expected, options.CredentialType);
        Assert.Equal("https://example.crm.dynamics.com", options.DataverseUrl);
        Assert.Null(options.TokenCredential);
        Assert.Null(options.DeviceCodeCredentialOptions);
        Assert.Null(options.InteractiveBrowserCredentialOptions);
        Assert.Null(options.AzureCliCredentialOptions);
    }

    [Fact]
    public void DefaultsToLibraryManagedBrowserAuthentication()
    {
        var options = ConnectionOptions.Create(new Dictionary<string, string?>(StringComparer.Ordinal), "https://example.crm.dynamics.com");
        Assert.Equal(DataverseCredentialType.InteractiveBrowserCredential, options.CredentialType);
        Assert.Null(options.InteractiveBrowserCredentialOptions);
    }

    [Fact]
    public void AzureCliTenantDoesNotConfigureInteractiveCredentials()
    {
        var arguments = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["auth"] = "azcli",
            ["tenant-id"] = "example-tenant",
        };
        var options = ConnectionOptions.Create(arguments, "https://example.crm.dynamics.com");
        Assert.Equal("example-tenant", options.AzureCliCredentialOptions!.TenantId);
        Assert.Null(options.DeviceCodeCredentialOptions);
        Assert.Null(options.InteractiveBrowserCredentialOptions);
    }

    [Theory]
    [InlineData("interactive", "tenant-id")]
    [InlineData("devicecode", "tenant-id")]
    [InlineData("interactive", "client-id")]
    [InlineData("devicecode", "client-id")]
    [InlineData("azcli", "client-id")]
    public void RejectsOverridesRatherThanBypassingLibraryCaching(string auth, string option)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["auth"] = auth,
            [option] = "override",
        };
        Assert.Throws<ArgumentException>(() => ConnectionOptions.Create(options, "https://example.crm.dynamics.com"));
    }
}
