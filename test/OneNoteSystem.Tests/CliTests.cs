using System.Text.RegularExpressions;
using OneNoteSystem.Cli;
using Xunit;

namespace OneNoteSystem.Tests;

[Collection("cli")]
public class HelpTests
{
    [Fact]
    public async Task Help_and_version_exit_zero()
    {
        var help = await CliHarness.RunAsync(new[] { "--help" }, url: null, apiKey: null);
        Assert.Equal(ExitCode.Ok, help.Code);
        Assert.Contains("onenotesystem capture", help.Stdout, StringComparison.Ordinal);

        var version = await CliHarness.RunAsync(new[] { "--version" }, url: null, apiKey: null);
        Assert.Equal(ExitCode.Ok, version.Code);
        Assert.Matches(@"^\d+\.\d+\.\d+", version.Stdout.Trim());
    }

    // The assembly version is what `dotnet pack` ships, so the two cannot be allowed to drift.
    [Fact]
    public async Task Version_matches_the_help_banner()
    {
        var version = (await CliHarness.RunAsync(new[] { "--version" }, url: null, apiKey: null)).Stdout.Trim();
        var help = await CliHarness.RunAsync(new[] { "--help" }, url: null, apiKey: null);

        Assert.StartsWith($"onenotesystem {version} ", help.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("configure")]
    [InlineData("doctor")]
    [InlineData("capture")]
    [InlineData("append")]
    [InlineData("todo")]
    public async Task Every_command_has_help_three_ways(string command)
    {
        foreach (var argv in new[] { new[] { command, "--help" }, new[] { command, "-h" }, new[] { "help", command } })
        {
            var result = await CliHarness.RunAsync(argv, url: null, apiKey: null);

            Assert.Equal(ExitCode.Ok, result.Code);
            Assert.StartsWith($"onenotesystem {command}", result.Stdout, StringComparison.Ordinal);
            Assert.Equal("", result.Stderr);
        }
    }

    [Theory]
    [InlineData("new", "capture")]
    [InlineData("add", "append")]
    public async Task Help_resolves_aliases_to_the_command_they_stand_for(string alias, string canonical)
    {
        foreach (var argv in new[] { new[] { alias, "--help" }, new[] { "help", alias } })
        {
            var result = await CliHarness.RunAsync(argv, url: null, apiKey: null);
            Assert.Equal(ExitCode.Ok, result.Code);
            Assert.StartsWith($"onenotesystem {canonical}", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_top_level_help_lists_every_command_and_its_aliases()
    {
        var result = await CliHarness.RunAsync(new[] { "help" }, url: null, apiKey: null);

        Assert.Equal(ExitCode.Ok, result.Code);
        foreach (var spec in CommandRegistry.All)
            Assert.Contains(spec.Usage, result.Stdout, StringComparison.Ordinal);
        Assert.Contains("alias: new", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("alias: add", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_help_topic_is_a_usage_error()
    {
        var result = await CliHarness.RunAsync(new[] { "help", "bogus" }, url: null, apiKey: null);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("no help topic for \"bogus\"", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        var result = await CliHarness.RunAsync(new[] { "bogus" }, url: null, apiKey: null);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("unknown command \"bogus\"", result.Stderr, StringComparison.Ordinal);
    }

    // Help is resolved before the flag parser, so this guards against `-h` being silently
    // swallowed as a positional and becoming a page title.
    [Fact]
    public async Task Help_wins_over_sending_a_request_and_never_hits_the_network()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture", "-h" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Empty(deployment.Requests);
        Assert.DoesNotContain("Created", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dash_h_after_double_dash_is_content_not_a_request_for_help()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "--page-title", "Quick Inbox", "--", "-h" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("-h", deployment.LastRequest.BodyText("content"));
    }
}

[Collection("cli")]
public class CaptureTests
{
    [Fact]
    public async Task Creates_a_page_from_positional_words()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture", "Standup", "notes" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("/api/capture", deployment.LastRequest.Path);
        Assert.Equal("Standup notes", deployment.LastRequest.BodyText("title"));
        Assert.Contains("Created \"Standup notes\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("https://onenote.example/page-created", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_the_body_from_stdin()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "capture", "Today's commit", "--stdin" }, deployment, stdin: "one file changed");

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("one file changed", deployment.LastRequest.BodyText("content"));
    }

    [Fact]
    public async Task Refuses_more_than_one_content_source()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "capture", "Notes", "--content", "a", "--stdin" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("pass only one of", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Requires_a_title()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("a page title is required", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Checks_the_title_limit_before_uploading()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture", new string('x', 201) }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Json_output_goes_to_stdout_alone()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture", "Notes", "--json" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Contains("\"page-created\"", result.Stdout, StringComparison.Ordinal);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task A_missing_default_section_is_a_conflict()
    {
        var deployment = new FakeDeployment { HasDefaultSection = false };
        var result = await CliHarness.RunAsync(new[] { "capture", "Notes" }, deployment);

        Assert.Equal(ExitCode.Conflict, result.Code);
        Assert.Contains("no default OneNote section", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_exits_two()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "capture", "Notes" }, deployment, apiKey: "wrong");

        Assert.Equal(ExitCode.Auth, result.Code);
        Assert.Contains("rejected this API key", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_configured_exits_six_with_a_hint()
    {
        var result = await CliHarness.RunAsync(new[] { "capture", "Notes" }, new FakeDeployment(), url: null, apiKey: null);

        Assert.Equal(ExitCode.Config, result.Code);
        Assert.Contains("not configured", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run `onenotesystem configure`", result.Stderr, StringComparison.Ordinal);
    }
}

[Collection("cli")]
public class AppendTests
{
    [Fact]
    public async Task Appends_positional_words_to_the_default_page()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "Ask about the budget" }, deployment,
            env: new Dictionary<string, string?> { ["ONENOTE_DEFAULT_PAGE"] = "Quick Inbox" });

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("Quick Inbox", deployment.LastRequest.BodyText("pageTitle"));
        Assert.Equal("Ask about the budget", deployment.LastRequest.BodyText("content"));
        Assert.Contains("Appended to \"Quick Inbox\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_page_anywhere_it_names_the_ways_to_set_one()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "append", "a thought" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("--default-page", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Page_id_targets_a_page_directly()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "Deploy finished", "--page-id", "1-abc123" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("1-abc123", deployment.LastRequest.BodyText("pageId"));
    }

    [Fact]
    public async Task Page_title_and_page_id_together_are_a_usage_error()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "x", "--page-title", "A", "--page-id", "1" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Words_and_content_together_are_a_usage_error()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "words", "--content", "flag", "--page-title", "Quick Inbox" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("not both", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task An_unknown_page_exits_three_with_the_deployments_message()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "x", "--page-title", "Nowhere" }, deployment);

        Assert.Equal(ExitCode.NotFound, result.Code);
        Assert.Contains("No page titled \"Nowhere\" was found.", result.Stderr, StringComparison.Ordinal);
    }

    // Appending to the wrong page is worse than not appending at all.
    [Fact]
    public async Task An_ambiguous_title_stops_and_asks_for_a_page_id()
    {
        var deployment = new FakeDeployment { DuplicateTitles = new[] { "Quick Inbox" } };
        var result = await CliHarness.RunAsync(
            new[] { "append", "x", "--page-title", "Quick Inbox" }, deployment);

        Assert.Equal(ExitCode.Conflict, result.Code);
        Assert.Contains("More than one page", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_content_never_reaches_the_deployment()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "--content", "   ", "--page-title", "Quick Inbox" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Content_over_the_limit_never_reaches_the_deployment()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "--content", new string('x', 100_001), "--page-title", "Quick Inbox" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Matches(new Regex(@"100,001 characters"), result.Stderr);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task A_source_link_is_validated_locally()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "append", "x", "--page-title", "Quick Inbox", "--url", "not a url" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }
}

[Collection("cli")]
public class DoctorTests
{
    [Fact]
    public async Task Reports_a_healthy_deployment_and_an_accepted_key()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "doctor" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Contains("Service:        onenote-system", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Database:       reachable", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Key:            accepted", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_the_database_when_it_is_the_broken_part()
    {
        var deployment = new FakeDeployment { Healthy = false };
        var result = await CliHarness.RunAsync(new[] { "doctor" }, deployment);

        Assert.Equal(ExitCode.Backend, result.Code);
        Assert.Contains("DATABASE_URL is not configured.", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_the_key_when_it_is_the_broken_part()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(new[] { "doctor" }, deployment, apiKey: "wrong");

        Assert.Equal(ExitCode.Auth, result.Code);
        Assert.Contains("rejected", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_exits_six_and_says_so_in_json()
    {
        var result = await CliHarness.RunAsync(new[] { "doctor", "--json" }, url: null, apiKey: null);

        Assert.Equal(ExitCode.Config, result.Code);
        Assert.Contains("\"configured\": false", result.Stdout, StringComparison.Ordinal);
    }
}

[Collection("cli")]
public class TodoTests
{
    [Fact]
    public async Task Adds_a_task_with_a_due_date_and_a_time_zone()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(
            new[] { "todo", "add", "Renew the domain", "--due", "2026-09-15" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("/api/todo", deployment.LastRequest.Path);
        Assert.Equal("Renew the domain", deployment.LastRequest.BodyText("title"));
        Assert.Equal("2026-09-15", deployment.LastRequest.BodyText("dueDate"));
        Assert.NotEqual("", deployment.LastRequest.BodyText("timeZone"));
    }

    [Fact]
    public async Task Rejects_a_bad_due_date_without_a_round_trip()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(
            new[] { "todo", "add", "Renew", "--due", "next thursday" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task Lists_open_tasks_with_their_ids()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(new[] { "todo", "list", "--list", "Errands" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("/api/todo?list=Errands", deployment.LastRequest.Path);
        Assert.Contains("[ ] Renew the domain", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("task-1", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shows_the_lists_and_marks_the_default()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(new[] { "todo", "lists" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Contains("Tasks  (default)", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Errands", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completes_a_task_by_id()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(new[] { "todo", "done", "task-1" }, deployment);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("/api/todo/complete", deployment.LastRequest.Path);
        Assert.Equal("task-1", deployment.LastRequest.BodyText("id"));
    }

    [Fact]
    public async Task Requires_a_task_id_to_complete()
    {
        var deployment = new FakeDeployment { Todo = true };
        var result = await CliHarness.RunAsync(new[] { "todo", "done" }, deployment);

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task An_unknown_subcommand_names_the_ones_that_exist()
    {
        var result = await CliHarness.RunAsync(new[] { "todo", "bogus" }, new FakeDeployment { Todo = true });

        Assert.Equal(ExitCode.Usage, result.Code);
        Assert.Contains("add|list|lists|done", result.Stderr, StringComparison.Ordinal);
    }

    // A deployment without the Tasks.ReadWrite scope answers 404, which would otherwise
    // read as "no such task".
    [Fact]
    public async Task A_deployment_without_to_do_says_so()
    {
        var deployment = new FakeDeployment { Todo = false };
        var result = await CliHarness.RunAsync(new[] { "todo", "list" }, deployment);

        Assert.Equal(ExitCode.Backend, result.Code);
        Assert.Contains("does not support Microsoft To Do", result.Stderr, StringComparison.Ordinal);
    }
}

[Collection("cli")]
public class ConfigureTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), $"onenotesystem-tests-{Guid.NewGuid():N}");

    private Dictionary<string, string?> Env => new() { ["ONENOTE_CONFIG_DIR"] = _configDir };

    [Fact]
    public async Task Saves_the_url_and_key_after_checking_the_deployment()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "configure", "--url", FakeDeployment.Url, "--api-key", FakeDeployment.ValidKey, "--default-page", "Quick Inbox" },
            deployment, url: null, apiKey: null, env: Env);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Equal("/api/health", deployment.Requests[0].Path);

        var saved = File.ReadAllText(Path.Combine(_configDir, "config.json"));
        Assert.Contains(FakeDeployment.ValidKey, saved, StringComparison.Ordinal);
        Assert.Contains("Quick Inbox", saved, StringComparison.Ordinal);

        // The key is written to the file, never echoed back to the terminal.
        Assert.DoesNotContain(FakeDeployment.ValidKey, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDeployment.ValidKey, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_verify_skips_the_health_check()
    {
        var deployment = new FakeDeployment();
        var result = await CliHarness.RunAsync(
            new[] { "configure", "--url", FakeDeployment.Url, "--api-key", "k", "--no-verify" },
            deployment, url: null, apiKey: null, env: Env);

        Assert.Equal(ExitCode.Ok, result.Code);
        Assert.Empty(deployment.Requests);
    }

    [Fact]
    public async Task A_site_that_is_not_a_deployment_says_which_problem_it_is()
    {
        var deployment = new FakeDeployment { Impersonate = "html404" };
        var result = await CliHarness.RunAsync(
            new[] { "configure", "--url", "https://intranet.example.com", "--api-key", "k" },
            deployment, url: null, apiKey: null, env: Env);

        Assert.Equal(ExitCode.Backend, result.Code);
        Assert.Contains("is not a OneNote System deployment", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_json_service_is_named_rather_than_accepted()
    {
        var deployment = new FakeDeployment { Impersonate = "some-other-app" };
        var result = await CliHarness.RunAsync(
            new[] { "configure", "--url", "https://other.example.com", "--api-key", "k" },
            deployment, url: null, apiKey: null, env: Env);

        Assert.Equal(ExitCode.Backend, result.Code);
        Assert.Contains("is running \"some-other-app\"", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unhealthy_deployment_is_not_saved()
    {
        var deployment = new FakeDeployment { Healthy = false };
        var result = await CliHarness.RunAsync(
            new[] { "configure", "--url", FakeDeployment.Url, "--api-key", "k" },
            deployment, url: null, apiKey: null, env: Env);

        Assert.Equal(ExitCode.Backend, result.Code);
        Assert.False(File.Exists(Path.Combine(_configDir, "config.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
    }
}
