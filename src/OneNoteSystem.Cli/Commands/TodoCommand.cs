using System.Text.Json;
using static OneNoteSystem.Cli.Commands.CommandSupport;

namespace OneNoteSystem.Cli.Commands;

/// <summary>Microsoft To Do tasks, through the same deployment and the same API key.</summary>
public static class TodoCommand
{
    public static Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var sub = argv.Count > 0 ? argv[0] : null;
        var rest = argv.Count > 0 ? argv.Skip(1).ToArray() : Array.Empty<string>();

        return sub switch
        {
            "add" => AddAsync(rest, ct),
            "list" => ListAsync(rest, ct),
            "lists" => ListsAsync(rest, ct),
            "done" => DoneAsync(rest, ct),
            _ => throw new CliException(
                "usage: onenotesystem todo <add|list|lists|done> ... (see `onenotesystem help todo`)",
                ExitCode.Usage),
        };
    }

    private static async Task<int> AddAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, new Dictionary<string, OptionKind>
        {
            ["title"] = OptionKind.String,
            ["note"] = OptionKind.String,
            ["list"] = OptionKind.String,
            ["due"] = OptionKind.String,
            ["reminder"] = OptionKind.String,
            ["json"] = OptionKind.Boolean,
        }, "todo add");

        var title = CheckTitle((args.Value("title") ?? args.PositionalText).Trim(), "--title");
        if (title.Length == 0)
            throw new CliException("a task title is required (positional or --title)", ExitCode.Usage);

        var payload = new Dictionary<string, object>
        {
            ["title"] = title,
            ["timeZone"] = DueDates.LocalTimeZone(),
        };
        if (args.NonEmpty("note") is { } note) payload["note"] = note;
        if (args.NonEmpty("list") is { } list) payload["list"] = list;
        if (args.NonEmpty("due") is { } due) payload["dueDate"] = DueDates.Parse(due, "--due");
        if (args.NonEmpty("reminder") is { } reminder) payload["reminder"] = DueDates.Parse(reminder, "--reminder");

        var (client, settings) = ClientFor();
        using (client)
        {
            var response = await RequestAsync(settings, () => client.PostAsync("/todo", payload, ct)).ConfigureAwait(false);
            var task = response.Object("task");

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            Out($"Added \"{task.Text("title") ?? title}\" to {task.Text("list") ?? "To Do"}");
            if (task.Text("dueDateTime") is { } dueDateTime) Out($"due: {DueDates.Format(dueDateTime)}");
            return ExitCode.Ok;
        }
    }

    private static async Task<int> ListAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, new Dictionary<string, OptionKind>
        {
            ["list"] = OptionKind.String,
            ["all"] = OptionKind.Boolean,
            ["top"] = OptionKind.String,
            ["json"] = OptionKind.Boolean,
        }, "todo list");

        var query = new List<string>();
        if (args.NonEmpty("list") is { } list) query.Add($"list={Uri.EscapeDataString(list)}");
        if (args.Bool("all")) query.Add("all=true");
        if (args.NonEmpty("top") is { } top) query.Add($"top={Uri.EscapeDataString(top)}");
        var path = query.Count > 0 ? $"/todo?{string.Join('&', query)}" : "/todo";

        var (client, settings) = ClientFor();
        using (client)
        {
            var response = await RequestAsync(settings, () => client.GetAsync(path, ct)).ConfigureAwait(false);

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            var tasks = response.Object("tasks");
            if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
            {
                Out($"No {(args.Bool("all") ? "" : "open ")}tasks in \"{response.Text("list") ?? "To Do"}\".");
                return ExitCode.Ok;
            }

            foreach (var task in tasks.EnumerateArray())
            {
                var due = task.Text("dueDateTime") is { } value ? $"  [due {DueDates.Format(value)}]" : "";
                Out($"{(task.Flag("completed") ? "[x]" : "[ ]")} {task.Text("title")}{due}");
                Out($"    {task.Text("id")}");
            }
            return ExitCode.Ok;
        }
    }

    private static async Task<int> ListsAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, new Dictionary<string, OptionKind> { ["json"] = OptionKind.Boolean }, "todo lists");

        var (client, settings) = ClientFor();
        using (client)
        {
            var response = await RequestAsync(settings, () => client.GetAsync("/todo/lists", ct)).ConfigureAwait(false);

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            var lists = response.Object("lists");
            if (lists.ValueKind == JsonValueKind.Array)
            {
                foreach (var list in lists.EnumerateArray())
                    Out($"{list.Text("name")}{(list.Flag("isDefault") ? "  (default)" : "")}");
            }
            return ExitCode.Ok;
        }
    }

    private static async Task<int> DoneAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var args = ArgParser.Parse(argv, new Dictionary<string, OptionKind>
        {
            ["list"] = OptionKind.String,
            ["json"] = OptionKind.Boolean,
        }, "todo done");

        var id = (args.Positionals.Count > 0 ? args.Positionals[0] : string.Empty).Trim();
        if (id.Length == 0)
            throw new CliException("a task id is required. Run `onenotesystem todo list` to see them.", ExitCode.Usage);

        var payload = new Dictionary<string, object> { ["id"] = id };
        if (args.NonEmpty("list") is { } list) payload["list"] = list;

        var (client, settings) = ClientFor();
        using (client)
        {
            var response = await RequestAsync(settings, () => client.PostAsync("/todo/complete", payload, ct)).ConfigureAwait(false);

            if (args.Bool("json"))
            {
                EmitJson(response);
                return ExitCode.Ok;
            }

            Out($"Completed \"{response.Object("task").Text("title") ?? id}\"");
            return ExitCode.Ok;
        }
    }

    // Not every deployment exposes To Do: the endpoints exist only where the Microsoft
    // connection has the Tasks.ReadWrite scope. A bare 404 would read as "no such task",
    // so name the real reason.
    private static async Task<JsonElement> RequestAsync(Settings settings, Func<Task<JsonElement>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (CliException err) when (err.Code == ExitCode.NotFound && err.Message == "HTTP 404")
        {
            throw new CliException(
                $"{settings.Url} does not support Microsoft To Do. " +
                "Point at a deployment that does, or use capture/append.",
                ExitCode.Backend);
        }
    }
}
