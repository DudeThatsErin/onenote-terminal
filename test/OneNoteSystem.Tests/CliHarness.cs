using OneNoteSystem.Cli;
using OneNoteSystem.Cli.Commands;
using Xunit;

namespace OneNoteSystem.Tests;

public sealed record CliResult(int Code, string Stdout, string Stderr);

/// <summary>
/// Runs the CLI end to end in-process: real argument parsing, real HTTP pipeline against a
/// <see cref="FakeDeployment"/>, and stdout/stderr captured separately so the tests can assert
/// that --json output stays pipeable.
/// </summary>
public static class CliHarness
{
    /// <summary>A directory that never exists, so "nothing configured" really means nothing.</summary>
    private static readonly string NoConfigDir =
        Path.Combine(Path.GetTempPath(), $"onenotesystem-no-config-{Guid.NewGuid():N}");

    public static async Task<CliResult> RunAsync(
        string[] argv,
        FakeDeployment? deployment = null,
        string? url = FakeDeployment.Url,
        string? apiKey = FakeDeployment.ValidKey,
        string? stdin = null,
        IDictionary<string, string?>? env = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;

        var overrides = new Dictionary<string, string?>(env ?? new Dictionary<string, string?>())
        {
            ["ONENOTE_URL"] = url,
            ["ONENOTE_API_KEY"] = apiKey,
        };

        // Point at a directory that does not exist unless a test asked for its own, so no test
        // ever reads or writes the developer's real config file.
        overrides.TryAdd("ONENOTE_CONFIG_DIR", NoConfigDir);
        overrides.TryAdd("ONENOTE_DEFAULT_PAGE", null);

        var previous = overrides.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in overrides) Environment.SetEnvironmentVariable(key, value);

        CommandSupport.Transport = deployment;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        if (stdin is not null)
        {
            Console.SetIn(new StringReader(stdin));
            ArgParser.StdinIsPiped = () => true;
        }
        else
        {
            ArgParser.StdinIsPiped = () => false;
        }

        try
        {
            var code = await CliRunner.RunAsync(argv);
            return new CliResult(code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            CommandSupport.Transport = null;
            ArgParser.StdinIsPiped = () => Console.IsInputRedirected;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    public static string BodyText(this RecordedRequest request, string name) =>
        request.Body.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}

/// <summary>
/// The CLI reads process-wide state -- the environment and the console -- so its tests share
/// one collection and never run side by side.
/// </summary>
[CollectionDefinition("cli")]
public sealed class CliCollection
{
}
