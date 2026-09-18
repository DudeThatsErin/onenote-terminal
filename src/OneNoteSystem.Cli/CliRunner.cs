using System.Reflection;

namespace OneNoteSystem.Cli;

/// <summary>Top-level dispatch: help, version, aliases, and turning a <see cref="CliException"/> into an exit code.</summary>
public static class CliRunner
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private const string Footer = """
        Configuration
          Values are read from the environment first, then the saved config file:
            ONENOTE_URL, ONENOTE_API_KEY, ONENOTE_DEFAULT_PAGE, ONENOTE_TIMEOUT_MS

        Exit codes
          0 ok   1 usage   2 unauthorized   3 page not found   4 network
          5 deployment error   6 not configured   7 conflict (no default section,
          or more than one page has that title)

        Docs: https://onenotesystem.erinskidds.com/terminal
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct = default)
    {
        try
        {
            return await DispatchAsync(argv, ct).ConfigureAwait(false);
        }
        catch (CliException err)
        {
            Console.Error.WriteLine($"onenotesystem: {err.Message}");
            if (err.Code == ExitCode.Config)
                Console.Error.WriteLine("Run `onenotesystem configure` to get started.");
            if (err.Code == ExitCode.Auth)
                Console.Error.WriteLine("Re-run `onenotesystem configure` with a current API key.");
            return err.Code;
        }
    }

    private static Task<int> DispatchAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var command = argv.Count > 0 ? argv[0] : null;
        var rest = argv.Count > 0 ? argv.Skip(1).ToArray() : Array.Empty<string>();

        if (command is null or "--help" or "-h")
        {
            Console.Out.Write(GeneralHelp());
            return Task.FromResult(ExitCode.Ok);
        }

        if (command is "--version" or "-v" or "version")
        {
            Console.Out.WriteLine(Version);
            return Task.FromResult(ExitCode.Ok);
        }

        if (command == "help")
        {
            var topic = rest.FirstOrDefault(arg => !arg.StartsWith('-'));
            if (topic is null)
            {
                Console.Out.Write(GeneralHelp());
                return Task.FromResult(ExitCode.Ok);
            }

            var topicSpec = CommandRegistry.Find(topic)
                ?? throw new CliException($"no help topic for \"{topic}\". Run `onenotesystem --help`.", ExitCode.Usage);
            Console.Out.Write(CommandHelp(topicSpec));
            return Task.FromResult(ExitCode.Ok);
        }

        var spec = CommandRegistry.Find(command)
            ?? throw new CliException($"unknown command \"{command}\". Run `onenotesystem --help`.", ExitCode.Usage);

        if (WantsHelp(rest))
        {
            Console.Out.Write(CommandHelp(spec));
            return Task.FromResult(ExitCode.Ok);
        }

        return spec.Handler(rest, ct);
    }

    // Help is resolved before the command's own flag parser runs, so `--help` and `-h` work
    // everywhere rather than being reported as an unknown option (or, worse, quietly becoming
    // a page title).
    private static bool WantsHelp(IReadOnlyList<string> argv)
    {
        foreach (var arg in argv)
        {
            if (arg == "--") return false;
            if (arg is "--help" or "-h") return true;
        }
        return false;
    }

    public static string GeneralHelp()
    {
        var rows = CommandRegistry.All.Select(spec =>
        {
            var alias = spec.Aliases.Count > 0 ? $" (alias: {string.Join(", ", spec.Aliases)})" : "";
            return $"  {spec.Usage.PadRight(45)}{spec.Summary}{alias}";
        });

        return $"""
            onenotesystem {Version} - create and update Microsoft OneNote pages from the terminal.

            Usage
            {string.Join('\n', rows)}

            Run `onenotesystem help <command>` or `onenotesystem <command> --help` for the
            options of a single command.

            Global
              --help, -h, --version, -v

            {Footer}

            """;
    }

    public static string CommandHelp(CommandSpec spec) =>
        $"{spec.Usage}\n\n{spec.Summary}.\n\n{spec.Detail}\n\n{Footer}\n";
}
