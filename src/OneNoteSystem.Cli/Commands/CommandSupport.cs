using System.Globalization;
using System.Text.Json;

namespace OneNoteSystem.Cli.Commands;

/// <summary>
/// Shared plumbing for the commands. --json output goes to stdout alone and progress and
/// hints go to stderr, so piping stays clean.
/// </summary>
public static class CommandSupport
{
    /// <summary>
    /// Mirrors MAX_NOTE_CONTENT_LENGTH on the deployment. Checked locally so a pasted file
    /// that is far too long fails instantly instead of after an upload.
    /// </summary>
    public const int MaxContentLength = 100_000;

    public const int MaxTitleLength = 200;

    private static readonly JsonSerializerOptions JsonOutput = new() { WriteIndented = true };

    public static void Out(string text) => Console.Out.WriteLine(text);

    public static void Note(string text) => Console.Error.WriteLine(text);

    public static void EmitJson(JsonElement value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOutput));

    public static void EmitJson(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOutput));

    /// <summary>
    /// The transport every command uses. Null means a real socket; the tests set a fake
    /// deployment here so the whole CLI can be exercised without a listening port.
    /// </summary>
    public static HttpMessageHandler? Transport { get; set; }

    public static (DeploymentClient Client, Settings Settings) ClientFor(EnvLookup? env = null)
    {
        var settings = ConfigStore.Require(env);
        return (new DeploymentClient(settings, ConfigStore.Timeout(env), Transport), settings);
    }

    /// <summary>--content, --file, and --stdin all fill the same field, so take exactly one.</summary>
    public static async Task<string> ResolveContentAsync(ParsedArgs args, bool required, CancellationToken ct)
    {
        var sources = new[] { "content", "file", "stdin" }.Where(args.Has).ToArray();
        if (sources.Length > 1)
            throw new CliException($"pass only one of {string.Join(", ", sources.Select(s => $"--{s}"))}", ExitCode.Usage);

        var content = string.Empty;
        if (args.Bool("stdin")) content = await ArgParser.ReadStdinAsync(ct).ConfigureAwait(false);
        else if (args.Has("file")) content = ReadContentFile(args.Value("file")!);
        else if (args.Has("content")) content = args.Value("content")!;

        if (required && string.IsNullOrWhiteSpace(content))
            throw new CliException("content is required. Use --content, --file, or --stdin.", ExitCode.Usage);

        CheckContentLength(content);
        return content;
    }

    public static void CheckContentLength(string content)
    {
        if (content.Length <= MaxContentLength) return;
        throw new CliException(
            $"content is {content.Length.ToString("N0", CultureInfo.InvariantCulture)} characters; " +
            $"the limit is {MaxContentLength.ToString("N0", CultureInfo.InvariantCulture)}",
            ExitCode.Usage);
    }

    private static string ReadContentFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new CliException($"could not read --file {path}: {err.Message}", ExitCode.Usage);
        }
    }

    /// <summary>
    /// The deployment silently drops a URL it cannot parse, so reject it here where the user
    /// can still see which flag was wrong.
    /// </summary>
    public static string ValidateUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
            throw new CliException($"--url \"{raw}\" is not a URL", ExitCode.Usage);
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            throw new CliException("--url must be http or https", ExitCode.Usage);
        return url.ToString();
    }

    public static string CheckTitle(string value, string flag)
    {
        if (value.Length > MaxTitleLength)
            throw new CliException($"{flag} cannot exceed {MaxTitleLength} characters", ExitCode.Usage);
        return value;
    }

    /// <summary>Reads a string property from a response object, or null when it is absent.</summary>
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an object property, or an undefined element when it is absent.</summary>
    public static JsonElement Object(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    public static bool Flag(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>True only when the property is present and explicitly false.</summary>
    public static bool IsExplicitlyFalse(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.False;
}
