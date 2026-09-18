using System.Net;
using System.Text.Json;
using static OneNoteSystem.Cli.Commands.CommandSupport;

namespace OneNoteSystem.Cli.Commands;

/// <summary>Asks for (or accepts as flags) the deployment URL and API key, verifies them, and saves them.</summary>
public static class ConfigureCommand
{
    private static readonly Dictionary<string, OptionKind> Spec = new()
    {
        ["url"] = OptionKind.String,
        ["api-key"] = OptionKind.String,
        ["default-page"] = OptionKind.String,
        ["no-verify"] = OptionKind.Boolean,
    };

    // Pointing at the wrong site is the most common setup mistake -- a personal dashboard, a
    // marketing page, a company intranet. Those answer, so the bare transport error ("HTTP 404")
    // reads like the deployment is broken rather than like the URL is wrong.
    private static readonly HashSet<int> WrongSiteCodes = new() { ExitCode.NotFound, ExitCode.Backend, ExitCode.Usage };

    /// <summary>Overridable so tests can answer the prompts without a terminal.</summary>
    public static Func<IPrompt> PromptFactory { get; set; } = () => new ConsolePrompt();

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, Spec, "configure");

        var flagUrl = args.NonEmpty("url");
        var flagKey = args.NonEmpty("api-key");

        // Only open a prompter when something is actually missing, so a fully flagged
        // `configure` works in scripts and containers with no terminal.
        var prompt = flagUrl is not null && flagKey is not null ? null : PromptFactory();

        var url = ConfigStore.NormalizeUrl(flagUrl ?? prompt!.Ask("OneNote System deployment URL: "));
        var apiKey = flagKey ?? prompt!.AskSecret("API key (input hidden): ");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CliException($"an API key is required. Create one in Setup Step 5 at {url}/setup", ExitCode.Usage);

        if (!args.Bool("no-verify"))
        {
            Note("Checking the deployment...");
            var health = await CheckDeploymentAsync(url, apiKey, ct).ConfigureAwait(false);
            if (health.IsExplicitlyFalse("ok"))
            {
                throw new CliException(
                    health.Text("error") ?? "the deployment reported that it is not healthy", ExitCode.Backend);
            }
            Note($"Reached {health.Text("service") ?? "the deployment"}.");
        }

        var file = ConfigStore.WriteFile(url, apiKey, args.NonEmpty("default-page"));
        Out($"Saved configuration to {file}");
        Note("The API key is stored in that file with owner-only permissions and is never printed.");
        return ExitCode.Ok;
    }

    private static async Task<JsonElement> CheckDeploymentAsync(string url, string apiKey, CancellationToken ct)
    {
        using var client = new DeploymentClient(url, apiKey, timeout: null, Transport);

        JsonElement health;
        try
        {
            health = await client
                .GetAsync("/health", ct, authenticated: false, HttpStatusCode.ServiceUnavailable)
                .ConfigureAwait(false);
        }
        catch (CliException err) when (WrongSiteCodes.Contains(err.Code))
        {
            throw new CliException(
                $"{url} answered, but it is not a OneNote System deployment " +
                $"(GET {url}/api/health said: {err.Message}). " +
                "Use the address of your own OneNote System deployment.",
                ExitCode.Backend);
        }
        // Network and auth failures already say the right thing, so they pass through.

        // A healthy deployment identifies itself. Anything else that happens to return JSON
        // here is some other service.
        var service = health.Text("service");
        if (service is not null && service != "onenote-system")
        {
            throw new CliException(
                $"{url} is running \"{service}\", not OneNote System. " +
                "Use the address of your own OneNote System deployment.",
                ExitCode.Backend);
        }

        return health;
    }
}
