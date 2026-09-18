using System.Net;
using static OneNoteSystem.Cli.Commands.CommandSupport;

namespace OneNoteSystem.Cli.Commands;

/// <summary>Tests the deployment, its database, and the API key separately, so a failure names the cause.</summary>
public static class DoctorCommand
{
    private static readonly Dictionary<string, OptionKind> Spec = new() { ["json"] = OptionKind.Boolean };

    private sealed record KeyProbe(bool Ok, int Code, string Message);

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, Spec, "doctor");
        var json = args.Bool("json");
        var settings = ConfigStore.Load();

        if (settings.Url.Length == 0 || settings.ApiKey.Length == 0)
        {
            if (json)
            {
                EmitJson(new
                {
                    ok = false,
                    configured = false,
                    error = "no deployment URL or API key configured",
                    configFile = settings.File,
                });
            }
            else
            {
                Out($"Deployment URL: {(settings.Url.Length == 0 ? "(unset)" : settings.Url)}");
                Out($"API key:        {(settings.ApiKey.Length == 0 ? "(unset)" : "set")}");
                Note("Run `onenotesystem configure` to set them.");
            }
            return ExitCode.Config;
        }

        var (client, _) = ClientFor();
        using (client)
        {
            var health = await client
                .GetAsync("/health", ct, authenticated: false, HttpStatusCode.ServiceUnavailable)
                .ConfigureAwait(false);

            // /api/health does not read the API key, so prove the key separately. A deliberately
            // invalid append is the only authenticated check available, and a 400 means the key
            // was accepted before validation rejected the body.
            var auth = await ProbeApiKeyAsync(client, ct).ConfigureAwait(false);
            var databaseDown = health.IsExplicitlyFalse("ok");

            if (json)
            {
                EmitJson(new
                {
                    ok = !databaseDown && auth.Ok,
                    url = settings.Url,
                    sources = new
                    {
                        url = settings.Sources.Url,
                        apiKey = settings.Sources.ApiKey,
                        defaultPage = settings.Sources.DefaultPage,
                    },
                    health,
                    auth = new { ok = auth.Ok, message = auth.Message },
                });
                return !databaseDown && auth.Ok ? ExitCode.Ok : auth.Ok ? ExitCode.Backend : auth.Code;
            }

            Out($"Deployment URL: {settings.Url}  (from {settings.Sources.Url})");
            Out($"API key:        set (from {settings.Sources.ApiKey})");
            if (settings.DefaultPage.Length > 0)
                Out($"Default page:   {settings.DefaultPage}  (from {settings.Sources.DefaultPage})");
            Out($"Service:        {health.Text("service") ?? "unknown"}");
            Out($"Database:       {(databaseDown ? $"unavailable — {health.Text("error")}" : "reachable")}");
            Out($"Key:            {auth.Message}");

            if (databaseDown) return ExitCode.Backend;
            return auth.Ok ? ExitCode.Ok : auth.Code;
        }
    }

    private static async Task<KeyProbe> ProbeApiKeyAsync(DeploymentClient client, CancellationToken ct)
    {
        try
        {
            await client.PostAsync("/append", new { content = "" }, ct).ConfigureAwait(false);
            // A success here would mean the deployment stopped validating content.
            return new KeyProbe(true, ExitCode.Ok, "accepted");
        }
        catch (CliException err)
        {
            return err.Code switch
            {
                ExitCode.Usage => new KeyProbe(true, ExitCode.Ok, "accepted"),
                ExitCode.Auth => new KeyProbe(false, ExitCode.Auth, "rejected — run `onenotesystem configure` again"),
                ExitCode.Conflict => new KeyProbe(false, ExitCode.Conflict, $"accepted, but {err.Message}"),
                _ => new KeyProbe(false, err.Code, err.Message),
            };
        }
    }
}
