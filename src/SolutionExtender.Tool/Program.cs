using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using DataverseConnection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using SolutionExtender;
using SolutionExtender.Tool;

return Cli.Run(args);

namespace SolutionExtender.Tool
{
    public static class Cli
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        public static int Run(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(Help); return 0; }
                var command = args[0];
                var allowed = command switch
                {
                    "inspect" => new[] { "input" },
                    "extract" => ["zip", "folder", "overwrite"],
                    "attach" => ["zip", "manifest", "output", "overwrite"],
                    "plan" => ["source", "target", "phase", "output"],
                    "extend" => ["zip", "output", "overwrite", "environment", "auth", "tenant-id", "client-id"],
                    "pre-import" or "post-import" => ["zip", "environment", "auth", "tenant-id", "client-id", "apply", "reassign-workflows", "output"],
                    _ => throw new ArgumentException($"Unknown command '{command}'. Use --help.")
                };
                var options = Parse(args.Skip(1).ToArray(), allowed);
                string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value : throw new ArgumentException($"Missing --{key}.");
                bool Flag(string key) => options.ContainsKey(key);
                void Print(object value)
                {
                    var json = JsonSerializer.Serialize(value, Json);
                    if (options.TryGetValue("output", out var output)) File.WriteAllText(output!, json + Environment.NewLine);
                    else Console.WriteLine(json);
                }
                switch (command)
                {
                    case "inspect": Print(SolutionPackage.Read(Required("input"))); return 0;
                    case "extract": SolutionPackage.Extract(Required("zip"), Required("folder"), Flag("overwrite")); return 0;
                    case "attach": SolutionPackage.Attach(Required("zip"), Required("manifest"), Required("output"), Flag("overwrite")); return 0;
                    case "plan": Print(Reconciliation.Compare(SolutionPackage.Read(Required("source")), SolutionPackage.Read(Required("target")), Required("phase"))); return 0;
                }
                var zip = Required("zip");
                var identity = SolutionPackage.Identity(zip);
                if (identity.Managed) throw new InvalidOperationException("Extended reconciliation only supports unmanaged solution ZIPs.");
                // Validate source metadata and arguments before starting authentication.
                var source = command == "extend" ? null : SolutionPackage.Read(zip);
                if (command == "extend")
                {
                    var output = Required("output");
                    if (File.Exists(output) && !Flag("overwrite")) throw new IOException("Output exists. Use --overwrite.");
                }
                if (Flag("reassign-workflows") && command != "post-import")
                    throw new ArgumentException("--reassign-workflows is only valid for post-import.");
                var environment = Required("environment");
                if (!Uri.TryCreate(environment, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("--environment must be an HTTPS Dataverse environment URL (not a PAC profile name).");
                var auth = options.GetValueOrDefault("auth") ?? "interactive";
                var credential = auth switch
                {
                    "azcli" => DataverseCredentialType.AzureCliCredential,
                    "devicecode" => DataverseCredentialType.DeviceCodeCredential,
                    "interactive" or "browser" => DataverseCredentialType.InteractiveBrowserCredential,
                    _ => throw new ArgumentException("--auth must be azcli, devicecode, or interactive.")
                };
                options.TryGetValue("tenant-id", out var tenant); options.TryGetValue("client-id", out var clientId);
                if (credential == DataverseCredentialType.AzureCliCredential && clientId is not null)
                    throw new ArgumentException("--client-id does not apply to azcli authentication.");
                var services = new ServiceCollection();
                services.AddDataverse(o =>
                {
                    o.DataverseUrl = environment; o.CredentialType = credential;
                    if (credential == DataverseCredentialType.AzureCliCredential && tenant is not null)
                        o.AzureCliCredentialOptions = new AzureCliCredentialOptions { TenantId = tenant };
                    // Leave options unset by default to use DataverseConnection's persistent token caching.
                    if (credential == DataverseCredentialType.DeviceCodeCredential && (tenant is not null || clientId is not null))
                        o.DeviceCodeCredentialOptions = new DeviceCodeCredentialOptions
                        {
                            TenantId = tenant, ClientId = clientId,
                            TokenCachePersistenceOptions = new TokenCachePersistenceOptions(),
                            DeviceCodeCallback = (code, _) => { Console.Error.WriteLine(code.Message); return Task.CompletedTask; }
                        };
                    if (credential == DataverseCredentialType.InteractiveBrowserCredential && (tenant is not null || clientId is not null))
                        o.InteractiveBrowserCredentialOptions = new InteractiveBrowserCredentialOptions
                        { TenantId = tenant, ClientId = clientId, TokenCachePersistenceOptions = new TokenCachePersistenceOptions() };
                });
                using var provider = services.BuildServiceProvider();
                var connection = provider.GetRequiredService<ServiceClient>();
                if (!connection.IsReady) throw new InvalidOperationException($"Dataverse connection failed: {connection.LastError}");
                var deployment = new DataverseDeployment(connection);
                var solutionId = deployment.FindSolution(identity.UniqueName);
                if (solutionId is null)
                {
                    if (command == "pre-import") { Console.WriteLine("Solution does not exist in target; no pre-import actions needed."); return 0; }
                    throw new InvalidOperationException($"Solution '{identity.UniqueName}' does not exist in the selected environment.");
                }
                if (command == "extend")
                {
                    var captured = deployment.Capture(zip, solutionId.Value);
                    SolutionPackage.Attach(zip, captured, Required("output"), Flag("overwrite"));
                    Console.WriteLine($"Extended '{identity.UniqueName}' into {Required("output")}."); return 0;
                }
                var plan = deployment.Plan(command, source!, solutionId.Value, Flag("reassign-workflows"));
                Print(new { Environment = environment, Solution = identity.UniqueName, Mode = Flag("apply") ? "apply" : "dry-run", plan.Phase, plan.Actions });
                if (Flag("apply")) deployment.Apply(plan, a => Console.Error.WriteLine($"{a.Kind}: {a.LogicalName} {a.Id} ({a.Name})"));
                else Console.Error.WriteLine("Dry run only. Re-run with --apply to change Dataverse.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
        }

        public static Dictionary<string, string?> Parse(string[] args, string[] allowed)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            var flags = new HashSet<string> { "apply", "overwrite", "reassign-workflows" };
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[i]}'.");
                var key = args[i][2..];
                if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option '--{key}'.");
                if (result.ContainsKey(key)) throw new ArgumentException($"Duplicate option '--{key}'.");
                if (flags.Contains(key)) result.Add(key, null);
                else
                {
                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for '--{key}'.");
                    result.Add(key, args[i]);
                }
            }
            return result;
        }

        private const string Help = """
        SolutionExtender — extended solution deployment for PAC / .NET 10

        Offline commands (no authentication):
          inspect --input <zip|ExtendedSolution.xml|folder>
          extract --zip <extended.zip> --folder <PAC folder> [--overwrite]
          attach --zip <PAC-packed.zip> --manifest <xml|folder> --output <extended.zip> [--overwrite]
          plan --source <zip|xml|folder> --target <zip|xml|folder> --phase <pre-import|post-import> [--output <json>]

        Dataverse commands:
          extend --zip <export.zip> --output <extended.zip> [--overwrite] <connection options>
          pre-import --zip <extended.zip> [--apply] [--output <plan.json>] <connection options>
          post-import --zip <extended.zip> [--apply] [--reassign-workflows] [--output <plan.json>] <connection options>

        Connection options:
          --environment <https://org.crm.dynamics.com>
          --auth <azcli|devicecode|interactive>       Default: interactive
          --tenant-id <tenant>                      Optional
          --client-id <app-registration>            Optional; browser/device code only

        Pre/post default to DRY RUN; --apply permits actual mutations.
        PAC remains responsible for export, unpack, pack, import and publish.
        Never use extended reconciliation against managed solutions.
        """;
    }
}
