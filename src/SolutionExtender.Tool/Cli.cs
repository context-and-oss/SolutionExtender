using System.Text.Json;
using System.Text.Json.Serialization;
using DataverseConnection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using SolutionExtender;
using SolutionExtender.Tool;

namespace SolutionExtender.Tool;

/// <summary>Parses command-line options and orchestrates solution deployment commands.</summary>
public static class Cli
{
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
          --tenant-id <tenant>                      Optional; azcli only
          Browser/device-code credentials and token caches are managed by DataverseConnection.

        Pre/post default to DRY RUN; --apply permits actual mutations.
        PAC remains responsible for export, unpack, pack, import and publish.
        Never use extended reconciliation against managed solutions.
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>Executes a CLI command and returns a process exit code.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>Zero on success, or one on failure.</returns>
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                Console.WriteLine(Help);
                return 0;
            }

            return Execute(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Validates command options and rejects unknown, missing, or duplicate arguments.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="allowed">The permitted option names.</param>
    /// <returns>The validated option values.</returns>
    public static IReadOnlyDictionary<string, string?> Parse(string[] args, string[] allowed)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply",
            "overwrite",
            "reassign-workflows",
        };
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.", nameof(args));
            }

            var key = args[i][2..];
            if (!allowed.Contains(key))
            {
                throw new ArgumentException($"Unknown option '--{key}'.", nameof(args));
            }

            if (result.ContainsKey(key))
            {
                throw new ArgumentException($"Duplicate option '--{key}'.", nameof(args));
            }

            if (flags.Contains(key))
            {
                result.Add(key, null);
            }
            else
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for '--{key}'.", nameof(args));
                }

                result.Add(key, args[i]);
            }
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}.", nameof(options));

    private static void Print(IReadOnlyDictionary<string, string?> options, object value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (options.TryGetValue("output", out var output))
        {
            File.WriteAllText(output!, json + Environment.NewLine);
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    private static int Execute(string[] args)
    {
        var command = args[0];
        var allowed = command switch
        {
            "inspect" => new[]
            {
                    "input",
            },
            "extract" => ["zip", "folder", "overwrite"],
            "attach" => ["zip", "manifest", "output", "overwrite"],
            "plan" => ["source", "target", "phase", "output"],
            "extend" => ["zip", "output", "overwrite", "environment", "auth", "tenant-id", "client-id"],
            "pre-import" or "post-import" => ["zip", "environment", "auth", "tenant-id", "client-id", "apply", "reassign-workflows", "output"],
            _ => throw new ArgumentException($"Unknown command '{command}'. Use --help.", nameof(args)),
        };
        var options = Parse(args.Skip(1).ToArray(), allowed);

        switch (command)
        {
            case "inspect":
                Print(options, SolutionPackage.Read(Required(options, "input")));
                return 0;
            case "extract":
                SolutionPackage.Extract(Required(options, "zip"), Required(options, "folder"), options.ContainsKey("overwrite"));
                return 0;
            case "attach":
                SolutionPackage.Attach(Required(options, "zip"), Required(options, "manifest"), Required(options, "output"), options.ContainsKey("overwrite"));
                return 0;
            case "plan":
                Print(options, Reconciliation.Compare(SolutionPackage.Read(Required(options, "source")), SolutionPackage.Read(Required(options, "target")), Required(options, "phase")));
                return 0;
        }

        return ExecuteConnected(command, options);
    }

    private static int ExecuteConnected(string command, IReadOnlyDictionary<string, string?> options)
    {
        var zip = Required(options, "zip");
        var identity = SolutionPackage.Identity(zip);
        if (identity.Managed)
        {
            throw new InvalidOperationException("Extended reconciliation only supports unmanaged solution ZIPs.");
        }

        // Validate source metadata and arguments before starting authentication.
        var source = string.Equals(command, "extend", StringComparison.Ordinal) ? null : SolutionPackage.Read(zip);
        if (string.Equals(command, "extend", StringComparison.Ordinal))
        {
            var output = Required(options, "output");
            if (File.Exists(output) && !options.ContainsKey("overwrite"))
            {
                throw new IOException("Output exists. Use --overwrite.");
            }
        }

        if (options.ContainsKey("reassign-workflows") && !string.Equals(command, "post-import", StringComparison.Ordinal))
        {
            throw new ArgumentException("--reassign-workflows is only valid for post-import.", nameof(options));
        }

        var environment = Required(options, "environment");
        using var provider = CreateConnection(options);
        var connection = provider.GetRequiredService<ServiceClient>();
        if (!connection.IsReady)
        {
            throw new InvalidOperationException($"Dataverse connection failed: {connection.LastError}");
        }

        var deployment = new DataverseDeployment(connection);
        var solutionId = deployment.FindSolution(identity.UniqueName);
        if (solutionId is null)
        {
            if (string.Equals(command, "pre-import", StringComparison.Ordinal))
            {
                Console.WriteLine("Solution does not exist in target; no pre-import actions needed.");
                return 0;
            }

            throw new InvalidOperationException($"Solution '{identity.UniqueName}' does not exist in the selected environment.");
        }

        if (string.Equals(command, "extend", StringComparison.Ordinal))
        {
            var captured = deployment.Capture(zip, solutionId.Value);
            SolutionPackage.Attach(zip, captured, Required(options, "output"), options.ContainsKey("overwrite"));
            Console.WriteLine($"Extended '{identity.UniqueName}' into {Required(options, "output")}.");
            return 0;
        }

        return ExecutePlan(command, options, source!, identity, environment, deployment, solutionId.Value);
    }

    private static int ExecutePlan(string command, IReadOnlyDictionary<string, string?> options, ExtendedManifest source, SolutionIdentity identity, string environment, DataverseDeployment deployment, Guid solutionId)
    {
        var plan = deployment.Plan(command, source, solutionId, options.ContainsKey("reassign-workflows"));
        Print(options, new
        {
            Environment = environment,
            Solution = identity.UniqueName,
            Mode = options.ContainsKey("apply") ? "apply" : "dry-run",
            plan.Phase,
            plan.Actions,
        });
        if (options.ContainsKey("apply"))
        {
            deployment.Apply(plan, a => Console.Error.WriteLine($"{a.Kind}: {a.LogicalName} {a.Id} ({a.Name})"));
        }
        else
        {
            Console.Error.WriteLine("Dry run only. Re-run with --apply to change Dataverse.");
        }

        return 0;
    }

    private static ServiceProvider CreateConnection(IReadOnlyDictionary<string, string?> options)
    {
        var environment = Required(options, "environment");
        if (!Uri.TryCreate(environment, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "https", StringComparison.Ordinal) || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("--environment must be an HTTPS Dataverse environment URL (not a PAC profile name).", nameof(options));
        }

        var connectionOptions = ConnectionOptions.Create(options, environment);
        var services = new ServiceCollection();
        services.AddDataverse(o =>
        {
            o.DataverseUrl = connectionOptions.DataverseUrl;
            o.CredentialType = connectionOptions.CredentialType;
            o.AzureCliCredentialOptions = connectionOptions.AzureCliCredentialOptions;
        });
        return services.BuildServiceProvider();
    }
}
