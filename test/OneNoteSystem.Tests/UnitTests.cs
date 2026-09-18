using OneNoteSystem.Cli;
using Xunit;

namespace OneNoteSystem.Tests;

public class ArgParserTests
{
    private static readonly Dictionary<string, OptionKind> CaptureSpec = new()
    {
        ["content"] = OptionKind.String,
        ["url"] = OptionKind.String,
        ["stdin"] = OptionKind.Boolean,
    };

    [Fact]
    public void Reads_values_booleans_and_positionals()
    {
        var args = ArgParser.Parse(
            new[] { "Standup", "notes", "--content=Shipped it", "--stdin", "--url", "https://example.com" },
            CaptureSpec,
            "capture");

        Assert.Equal(new[] { "Standup", "notes" }, args.Positionals);
        Assert.Equal("Shipped it", args.Value("content"));
        Assert.Equal("https://example.com", args.Value("url"));
        Assert.True(args.Bool("stdin"));
    }

    [Fact]
    public void Names_the_command_in_unknown_option_errors()
    {
        var err = Assert.Throws<CliException>(() => ArgParser.Parse(new[] { "--nope" }, CaptureSpec, "capture"));

        Assert.Equal(ExitCode.Usage, err.Code);
        Assert.Contains("--nope", err.Message, StringComparison.Ordinal);
        Assert.Contains("capture", err.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_value_flag_with_no_value()
    {
        var err = Assert.Throws<CliException>(() => ArgParser.Parse(new[] { "--content" }, CaptureSpec, "capture"));
        Assert.Equal(ExitCode.Usage, err.Code);
    }

    [Fact]
    public void Stops_flag_parsing_at_double_dash()
    {
        var args = ArgParser.Parse(new[] { "--", "--content" }, CaptureSpec, "append");
        Assert.Equal(new[] { "--content" }, args.Positionals);
    }

    [Fact]
    public void Negated_switches_turn_off()
    {
        var args = ArgParser.Parse(new[] { "--stdin=false" }, CaptureSpec, "capture");
        Assert.False(args.Bool("stdin"));
        Assert.True(args.Has("stdin"));
    }
}

public class NormalizeUrlTests
{
    [Theory]
    [InlineData("onenotesystem.example.com", "https://onenotesystem.example.com")]
    [InlineData("https://onenotesystem.example.com///", "https://onenotesystem.example.com")]
    [InlineData("https://onenotesystem.example.com/api", "https://onenotesystem.example.com")]
    public void Adds_https_trims_slashes_and_drops_a_pasted_api_suffix(string input, string expected) =>
        Assert.Equal(expected, ConfigStore.NormalizeUrl(input));

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000")]
    public void Allows_http_for_localhost(string input) => Assert.Equal(input, ConfigStore.NormalizeUrl(input));

    [Fact]
    public void Rejects_http_elsewhere() =>
        Assert.Equal(ExitCode.Usage,
            Assert.Throws<CliException>(() => ConfigStore.NormalizeUrl("http://onenotesystem.example.com")).Code);

    [Theory]
    [InlineData("")]
    [InlineData("https://")]
    public void Rejects_junk(string input) =>
        Assert.Equal(ExitCode.Usage, Assert.Throws<CliException>(() => ConfigStore.NormalizeUrl(input)).Code);
}

public class ExitCodeTests
{
    [Fact]
    public void Codes_are_distinct_and_stable()
    {
        int[] codes =
        {
            ExitCode.Ok, ExitCode.Usage, ExitCode.Auth, ExitCode.NotFound,
            ExitCode.Network, ExitCode.Backend, ExitCode.Config, ExitCode.Conflict,
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, ExitCode.Ok);
        Assert.Equal(2, ExitCode.Auth);
        Assert.Equal(6, ExitCode.Config);
    }
}

public class DueDateTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 10, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("+3d", "2026-09-21")]
    [InlineData("+2w", "2026-10-02")]
    [InlineData("today", "2026-09-18")]
    [InlineData("tomorrow", "2026-09-19")]
    [InlineData("2026-09-15", "2026-09-15")]
    public void Parses_the_shorthand_people_type(string input, string expected) =>
        Assert.Equal(expected, DueDates.Parse(input, "--due", Now));

    [Fact]
    public void Keeps_a_date_time_local_and_unzoned() =>
        Assert.Equal("2026-09-15T14:30:00", DueDates.Parse("2026-09-15 14:30", "--due", Now));

    [Theory]
    [InlineData("2026-02-31")]
    [InlineData("next thursday")]
    [InlineData("")]
    public void Rejects_what_is_not_a_date(string input) =>
        Assert.Equal(ExitCode.Usage, Assert.Throws<CliException>(() => DueDates.Parse(input, "--due", Now)).Code);

    [Fact]
    public void Renders_a_bare_date_unchanged() => Assert.Equal("2026-09-15", DueDates.Format("2026-09-15"));

    // Microsoft To Do wants an IANA zone; a Windows id like "Eastern Standard Time" is rejected.
    // A UTC machine must say "UTC" rather than a tzdata alias such as "Universal", which is
    // what a CI runner with TZ=UTC reports.
    [Theory]
    [InlineData("UTC")]
    [InlineData("Universal")]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void Reports_an_iana_time_zone(string machineZone)
    {
        var zone = RunWithLocalTimeZone(machineZone, DueDates.LocalTimeZone);

        Assert.True(zone == "UTC" || zone.Contains('/', StringComparison.Ordinal), zone);
        Assert.NotEqual("Universal", zone);
    }

    /// <summary>
    /// TimeZoneInfo.Local is cached per process and reads the TZ variable, so a test that
    /// changes it has to clear the cache on both sides.
    /// </summary>
    private static string RunWithLocalTimeZone(string zone, Func<string> body)
    {
        var previous = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", zone);
            TimeZoneInfo.ClearCachedData();
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", previous);
            TimeZoneInfo.ClearCachedData();
        }
    }
}
