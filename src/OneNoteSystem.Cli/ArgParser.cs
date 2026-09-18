namespace OneNoteSystem.Cli;

/// <summary>Whether an option carries a value or is a bare switch.</summary>
public enum OptionKind
{
    String,
    Boolean,
}

/// <summary>One command's arguments after parsing: named options and the leftover positional words.</summary>
public sealed class ParsedArgs
{
    private readonly Dictionary<string, string> _flags;

    internal ParsedArgs(Dictionary<string, string> flags, List<string> positionals)
    {
        _flags = flags;
        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    /// <summary>True when the option appeared at all, whatever its value.</summary>
    public bool Has(string name) => _flags.ContainsKey(name);

    /// <summary>The option's value, or null when it was not passed.</summary>
    public string? Value(string name) => _flags.TryGetValue(name, out var value) ? value : null;

    /// <summary>The option's value when it is a non-empty string, otherwise null.</summary>
    public string? NonEmpty(string name)
    {
        var value = Value(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>A switch's state. <c>--flag=false</c> and <c>--flag=0</c> turn it off.</summary>
    public bool Bool(string name) => _flags.TryGetValue(name, out var value) && value == "true";

    public string PositionalText => string.Join(' ', Positionals);
}

/// <summary>
/// A small flag parser. It exists rather than a library because "unknown flag" errors
/// that name the command are most of what a CLI's error quality is made of.
/// </summary>
public static class ArgParser
{
    public static ParsedArgs Parse(IReadOnlyList<string> argv, IReadOnlyDictionary<string, OptionKind> spec, string commandName)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();

        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];

            if (arg == "--")
            {
                for (var rest = i + 1; rest < argv.Count; rest++) positionals.Add(argv[rest]);
                break;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var eq = arg.IndexOf('=');
            var name = (eq == -1 ? arg[2..] : arg[2..eq]).Trim();
            if (!spec.TryGetValue(name, out var kind))
                throw new CliException($"unknown option --{name} for `onenotesystem {commandName}`", ExitCode.Usage);

            if (kind == OptionKind.Boolean)
            {
                if (eq != -1)
                {
                    var raw = arg[(eq + 1)..].ToLowerInvariant();
                    flags[name] = raw is "false" or "0" ? "false" : "true";
                }
                else
                {
                    flags[name] = "true";
                }
                continue;
            }

            string value;
            if (eq != -1)
            {
                value = arg[(eq + 1)..];
            }
            else
            {
                if (++i >= argv.Count) throw new CliException($"--{name} needs a value", ExitCode.Usage);
                value = argv[i];
            }

            flags[name] = value;
        }

        return new ParsedArgs(flags, positionals);
    }

    /// <summary>
    /// Whether something is piped into stdin. Overridable so the tests can feed
    /// <c>--stdin</c> through a redirected console.
    /// </summary>
    public static Func<bool> StdinIsPiped { get; set; } = () => Console.IsInputRedirected;

    /// <summary>Reads piped stdin for <c>--stdin</c>, refusing when there is nothing piped in.</summary>
    public static async Task<string> ReadStdinAsync(CancellationToken ct)
    {
        if (!StdinIsPiped())
            throw new CliException("--stdin was passed but nothing is piped in", ExitCode.Usage);

        return await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
