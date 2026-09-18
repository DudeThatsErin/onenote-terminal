namespace OneNoteSystem.Cli;

/// <summary>Stable exit codes so shell automation can branch on failures.</summary>
public static class ExitCode
{
    public const int Ok = 0;
    public const int Usage = 1;     // bad flags, missing required input, local validation failure
    public const int Auth = 2;      // 401 -- the deployment rejected the API key
    public const int NotFound = 3;  // 404 -- no page matched the title or id
    public const int Network = 4;   // DNS/TLS/timeout/connection refused
    public const int Backend = 5;   // 4xx/5xx the CLI cannot classify further
    public const int Config = 6;    // no deployment URL or API key configured
    public const int Conflict = 7;  // 409 -- no default section, or an ambiguous page title
}

/// <summary>An error reported to the user as a message plus an exit code, never a stack trace.</summary>
public sealed class CliException : Exception
{
    public CliException(string message, int code = ExitCode.Usage) : base(message) => Code = code;

    public int Code { get; }
}
