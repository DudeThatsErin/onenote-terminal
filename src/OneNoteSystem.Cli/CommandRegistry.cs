using OneNoteSystem.Cli.Commands;

namespace OneNoteSystem.Cli;

/// <summary>One command's help text and handler, so the command list and the help can never drift apart.</summary>
public sealed record CommandSpec
{
    public required string Name { get; init; }
    public required string Usage { get; init; }
    public required string Summary { get; init; }
    public required string Detail { get; init; }
    public required Func<IReadOnlyList<string>, CancellationToken, Task<int>> Handler { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

public static class CommandRegistry
{
    public static IReadOnlyList<CommandSpec> All { get; } = new[]
    {
        new CommandSpec
        {
            Name = "configure",
            Usage = "onenotesystem configure [options]",
            Summary = "Save your deployment URL and API key",
            Handler = ConfigureCommand.RunAsync,
            Detail = """
                Asks for your deployment URL and an API key from your deployment's
                setup page, checks that the deployment answers, and saves both to a config file
                only your user account can read.

                  --url <url>            Deployment URL, e.g. https://onenote.example.com
                  --api-key <key>        API key from Setup Step 5
                  --default-page <title> Page that `append` uses when you do not name one
                  --no-verify            Save without checking that the deployment answers

                Pass --url and --api-key to configure a server or container without prompts.
                The key is never printed back to the terminal.

                Config file
                  Linux, macOS   ~/.config/onenotesystem/config.json  (mode 600)
                  Windows        %APPDATA%\onenotesystem\config.json
                """,
        },

        new CommandSpec
        {
            Name = "doctor",
            Usage = "onenotesystem doctor [--json]",
            Summary = "Check the deployment, database, and key",
            Handler = DoctorCommand.RunAsync,
            Detail = """
                Tests the deployment, its database, and your API key separately, so
                a failure tells you which one is wrong.

                  --json                 Machine-readable output on stdout only

                Exits 0 when everything is reachable, or the exit code of the first problem it
                finds: 2 for a rejected key, 6 when nothing is configured, 7 when no default
                OneNote section has been chosen.
                """,
        },

        new CommandSpec
        {
            Name = "capture",
            Aliases = new[] { "new" },
            Usage = "onenotesystem capture <title> [options]",
            Summary = "Create a new page in your default section",
            Handler = CaptureCommand.RunAsync,
            Detail = """
                Creates a new page in the OneNote section you chose during setup.
                Calls POST /api/capture -- the same endpoint the Capture to OneNote Shortcut uses.

                  <title>                Page title, as positional words
                  --title <text>         Title, if you would rather not use positional words
                  --content <text>       Page body
                  --file <path>          Read the body from a file
                  --stdin                Read the body from piped stdin
                  --url <link>           Record a source link under the body
                  --json                 Print the deployment's JSON response

                Use only one of --content, --file, or --stdin. Titles are limited to 200
                characters and content to 100,000; both are checked before anything is uploaded.
                Markdown is not rendered -- it arrives as literal text.

                Examples
                  onenotesystem capture "Standup notes"
                  onenotesystem capture "Standup notes" --content "Shipped the CLI"
                  onenotesystem capture "Article" --file notes.md --url https://example.com
                  git log -1 --stat | onenotesystem capture "Today's commit" --stdin
                """,
        },

        new CommandSpec
        {
            Name = "append",
            Aliases = new[] { "add" },
            Usage = "onenotesystem append <text> [options]",
            Summary = "Add text to an existing page",
            Handler = AppendCommand.RunAsync,
            Detail = """
                Adds text to a page that already exists -- a running inbox, a daily
                log, a project page. Calls POST /api/append.

                  <text>                 Text to append, as positional words
                  --content <text>       Text to append
                  --file <path>          Read the text from a file
                  --stdin                Read the text from piped stdin
                  --page-title <title>   Exact title of a page in your default section
                  --page-id <id>         Target a page directly, from any section
                  --url <link>           Record a source link under the text
                  --json                 Print the deployment's JSON response

                With neither --page-title nor --page-id, the saved default page is used. Save
                one with: onenotesystem configure --default-page "Quick Inbox"

                --page-title must match exactly, including capitalisation, and the page must be
                in your default section. If two pages share the title the command stops and asks
                for --page-id rather than appending to the wrong one.

                Examples
                  onenotesystem append "Ask about the Q3 budget" --page-title "Quick Inbox"
                  onenotesystem append "Deploy finished" --page-id "1-abc123!..."
                  dmesg | tail -20 | onenotesystem append --stdin --page-title "Server log"
                """,
        },

        new CommandSpec
        {
            Name = "todo",
            Usage = "onenotesystem todo <add|list|lists|done> ...",
            Summary = "Work with Microsoft To Do tasks",
            Handler = TodoCommand.RunAsync,
            Detail = """
                Creates and completes Microsoft To Do tasks through the same
                deployment and the same API key. Available only where the deployment's Microsoft
                connection has approved To Do access.

                  onenotesystem todo add <title> [options]   Add a task
                  onenotesystem todo list [options]          Show open tasks
                  onenotesystem todo lists                   Show your To Do lists
                  onenotesystem todo done <id>               Mark a task complete

                todo add
                  <title>                Task title, as positional words, or use --title
                  --note <text>          Longer note on the task
                  --list <name>          To Do list to use (default: your default list)
                  --due <when>           2026-09-15, "tomorrow", or "+3d"
                  --reminder <when>      Same formats; sets a reminder
                  --json

                todo list
                  --list <name>          Which list to read
                  --all                  Include completed tasks
                  --top <n>              How many to show (default 25, max 100)
                  --json

                todo done
                  <id>                   Task id, as shown by `todo list`
                  --list <name>          The list the task is in
                  --json

                Dates are sent with your local time zone, so a bare date stays that day
                rather than shifting.

                Examples
                  onenotesystem todo add "Renew the domain" --due +7d
                  onenotesystem todo add "Call the bank" --list Errands --due tomorrow
                  onenotesystem todo list --list Errands
                  onenotesystem todo done AAMkAG...
                """,
        },
    };

    /// <summary>The command for a typed word, resolving aliases. Null when unknown.</summary>
    public static CommandSpec? Find(string word) =>
        All.FirstOrDefault(spec => spec.Name == word || spec.Aliases.Contains(word));
}
