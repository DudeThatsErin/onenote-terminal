using static OneNoteSystem.Cli.Commands.CommandSupport;

namespace OneNoteSystem.Cli.Commands;

/// <summary>POST /api/append -- the same call the "add to my inbox page" Shortcut makes.</summary>
public static class AppendCommand
{
    private static readonly Dictionary<string, OptionKind> Spec = new()
    {
        ["page-title"] = OptionKind.String,
        ["page-id"] = OptionKind.String,
        ["content"] = OptionKind.String,
        ["file"] = OptionKind.String,
        ["url"] = OptionKind.String,
        ["stdin"] = OptionKind.Boolean,
        ["json"] = OptionKind.Boolean,
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, Spec, "append");

        if (args.NonEmpty("page-title") is not null && args.NonEmpty("page-id") is not null)
            throw new CliException("pass either --page-title or --page-id, not both", ExitCode.Usage);

        // Bare words are the content, not the title: appending to a configured default page is
        // the common case, and `onenotesystem append "a thought"` should just work.
        var positionalContent = args.PositionalText.Trim();
        if (positionalContent.Length > 0 && (args.Has("content") || args.Has("file") || args.Bool("stdin")))
        {
            throw new CliException(
                "pass the content either as words or with --content/--file/--stdin, not both", ExitCode.Usage);
        }

        var content = positionalContent.Length > 0
            ? positionalContent
            : await ResolveContentAsync(args, required: true, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
            throw new CliException("content is required and cannot be empty", ExitCode.Usage);
        CheckContentLength(content);

        var (client, settings) = ClientFor();
        using (client)
        {
            var payload = new Dictionary<string, object> { ["content"] = content };
            string? pageTitle = null;

            if (args.NonEmpty("page-id") is { } pageId)
            {
                payload["pageId"] = pageId;
            }
            else
            {
                pageTitle = CheckTitle((args.Value("page-title") ?? settings.DefaultPage).Trim(), "--page-title");
                if (pageTitle.Length == 0)
                {
                    throw new CliException(
                        "no page to append to. Pass --page-title or --page-id, or save one with " +
                        "`onenotesystem configure --default-page \"Quick Inbox\"`.",
                        ExitCode.Usage);
                }
                payload["pageTitle"] = pageTitle;
            }

            if (args.NonEmpty("url") is { } sourceUrl) payload["url"] = ValidateUrl(sourceUrl);

            var response = await client.PostAsync("/append", payload, ct).ConfigureAwait(false);
            var page = response.Object("page");

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            Out($"Appended to \"{page.Text("title") ?? pageTitle ?? page.Text("id")}\"");
            if (page.Text("webUrl") is { } webUrl) Out(webUrl);
            return ExitCode.Ok;
        }
    }
}
