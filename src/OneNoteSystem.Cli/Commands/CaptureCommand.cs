using static OneNoteSystem.Cli.Commands.CommandSupport;

namespace OneNoteSystem.Cli.Commands;

/// <summary>POST /api/capture -- the same call the "create a page" Shortcut makes.</summary>
public static class CaptureCommand
{
    private static readonly Dictionary<string, OptionKind> Spec = new()
    {
        ["title"] = OptionKind.String,
        ["content"] = OptionKind.String,
        ["file"] = OptionKind.String,
        ["url"] = OptionKind.String,
        ["stdin"] = OptionKind.Boolean,
        ["json"] = OptionKind.Boolean,
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, Spec, "capture");

        var title = CheckTitle((args.Value("title") ?? args.PositionalText).Trim(), "--title");
        if (title.Length == 0)
            throw new CliException("a page title is required (positional or --title)", ExitCode.Usage);

        var content = await ResolveContentAsync(args, required: false, ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object> { ["title"] = title, ["content"] = content };
        if (args.NonEmpty("url") is { } sourceUrl) payload["url"] = ValidateUrl(sourceUrl);

        var (client, _) = ClientFor();
        using (client)
        {
            var response = await client.PostAsync("/capture", payload, ct).ConfigureAwait(false);
            var page = response.Object("page");

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            Out($"Created \"{page.Text("title") ?? title}\"");
            if (page.Text("webUrl") is { } webUrl) Out(webUrl);
            else if (page.Text("id") is { } id) Out($"id: {id}");
            return ExitCode.Ok;
        }
    }
}
