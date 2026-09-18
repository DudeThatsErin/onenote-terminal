using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneNoteSystem.Cli;

/// <summary>Reads one environment variable. Injected so tests never touch the real environment.</summary>
public delegate string? EnvLookup(string name);

/// <summary>The shape of config.json.</summary>
public sealed record ConfigFile
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("apiKey")] public string? ApiKey { get; init; }
    [JsonPropertyName("defaultPage")] public string? DefaultPage { get; init; }
}

/// <summary>Where each resolved value came from, so <c>doctor</c> can explain surprises.</summary>
public sealed record SettingSources(string Url, string ApiKey, string DefaultPage);

/// <summary>The deployment URL, API key, and default page, resolved from the environment and the config file.</summary>
public sealed record Settings
{
    public required string Url { get; init; }
    public required string ApiKey { get; init; }
    public required string DefaultPage { get; init; }
    public required SettingSources Sources { get; init; }
    public required string File { get; init; }
}

/// <summary>
/// Where the deployment URL and API key come from, in precedence order:
/// the environment (ONENOTE_URL / ONENOTE_API_KEY), then the file written by `onenotesystem configure`.
/// The key is never printed, logged, or included in error output.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static EnvLookup DefaultEnv { get; } = Environment.GetEnvironmentVariable;

    public static string ConfigDir(EnvLookup? env = null)
    {
        env ??= DefaultEnv;

        var overridden = env("ONENOTE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        if (OperatingSystem.IsWindows())
        {
            var appData = env("APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
                appData = Path.Combine(Home(env), "AppData", "Roaming");
            return Path.Combine(appData, "onenotesystem");
        }

        var xdg = env("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg)) xdg = Path.Combine(Home(env), ".config");
        return Path.Combine(xdg, "onenotesystem");
    }

    public static string ConfigPath(EnvLookup? env = null) => Path.Combine(ConfigDir(env), "config.json");

    public static ConfigFile ReadFile(EnvLookup? env = null)
    {
        try
        {
            var json = System.IO.File.ReadAllText(ConfigPath(env));
            return JsonSerializer.Deserialize<ConfigFile>(json) ?? new ConfigFile();
        }
        catch
        {
            // A missing or unreadable file is simply "nothing configured yet".
            return new ConfigFile();
        }
    }

    /// <summary>
    /// Trailing slashes and an accidental /api suffix are the two things people paste;
    /// normalise both rather than failing later with a confusing 404.
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) throw new CliException("deployment URL is required", ExitCode.Usage);

        var withScheme = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? value
                : $"https://{value}";

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
            throw new CliException($"not a valid URL: {value}", ExitCode.Usage);

        var isLocal = url.Host is "localhost" or "127.0.0.1";
        if (url.Scheme != Uri.UriSchemeHttps && !isLocal)
            throw new CliException("deployment URL must use https (http is allowed only for localhost)", ExitCode.Usage);

        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.Ordinal)) path = path[..^4];

        return $"{url.Scheme}://{url.Authority}{path}";
    }

    /// <summary>Writes the config file with owner-only permissions and returns its path.</summary>
    public static string WriteFile(string url, string apiKey, string? defaultPage, EnvLookup? env = null)
    {
        var dir = ConfigDir(env);
        Directory.CreateDirectory(dir);
        var file = ConfigPath(env);

        var contents = new ConfigFile
        {
            Url = url,
            ApiKey = apiKey,
            DefaultPage = string.IsNullOrWhiteSpace(defaultPage) ? null : defaultPage,
        };

        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(contents, WriteOptions) + Environment.NewLine);
        RestrictPermissions(file, env);
        return file;
    }

    // The OS keychain is the goal; until that ships, at least make the fallback
    // file unreadable by other accounts on the machine.
    private static void RestrictPermissions(string file, EnvLookup? env)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = (env ?? DefaultEnv)("USERNAME");
            if (string.IsNullOrWhiteSpace(user)) return;
            try
            {
                using var icacls = Process.Start(new ProcessStartInfo("icacls")
                {
                    ArgumentList = { file, "/inheritance:r", "/grant:r", $"{user}:F" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                icacls?.WaitForExit(5000);
            }
            catch
            {
                // icacls is best-effort; the file still lives in the per-user AppData dir.
            }
            return;
        }

        try
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort on exotic filesystems.
        }
    }

    public static Settings Load(EnvLookup? env = null)
    {
        env ??= DefaultEnv;
        var file = ReadFile(env);

        var url = First(env("ONENOTE_URL"), file.Url);
        var apiKey = First(env("ONENOTE_API_KEY"), file.ApiKey);
        var defaultPage = First(env("ONENOTE_DEFAULT_PAGE"), file.DefaultPage);

        return new Settings
        {
            Url = url.Length == 0 ? string.Empty : NormalizeUrl(url),
            ApiKey = apiKey,
            DefaultPage = defaultPage,
            Sources = new SettingSources(
                Source(env("ONENOTE_URL"), file.Url),
                Source(env("ONENOTE_API_KEY"), file.ApiKey),
                Source(env("ONENOTE_DEFAULT_PAGE"), file.DefaultPage)),
            File = ConfigPath(env),
        };
    }

    public static Settings Require(EnvLookup? env = null)
    {
        var settings = Load(env);
        if (settings.Url.Length == 0 || settings.ApiKey.Length == 0)
        {
            throw new CliException(
                "not configured. Run `onenotesystem configure`, or set ONENOTE_URL and ONENOTE_API_KEY.",
                ExitCode.Config);
        }
        return settings;
    }

    /// <summary>The request timeout, from ONENOTE_TIMEOUT_MS or the 15s default.</summary>
    public static TimeSpan Timeout(EnvLookup? env = null)
    {
        var raw = (env ?? DefaultEnv)("ONENOTE_TIMEOUT_MS");
        return int.TryParse(raw, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : DeploymentClient.DefaultTimeout;
    }

    private static string First(string? fromEnv, string? fromFile) =>
        !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : fromFile ?? string.Empty;

    private static string Source(string? fromEnv, string? fromFile) =>
        !string.IsNullOrWhiteSpace(fromEnv) ? "env" : !string.IsNullOrWhiteSpace(fromFile) ? "config file" : "unset";

    private static string Home(EnvLookup env) =>
        env("HOME") ?? env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
