# onenotesystem

Terminal client for OneNote System: create and update Microsoft OneNote pages, and manage
Microsoft To Do tasks, from any shell — including Linux and locked-down work machines.

Rewritten in C# on .NET 8; the previous JavaScript implementation has been removed. The
command surface, help text, exit codes, and HTTP contract are unchanged, so an existing
deployment and config file keep working.

## Layout

```
OneNoteSystem.sln
Directory.Build.props              shared TFM, nullable, warnings-as-errors, version
src/OneNoteSystem.Cli/
  Program.cs                       entry point; Ctrl-C wiring
  CliRunner.cs                     dispatch, help, version, error-to-exit-code
  CommandRegistry.cs               one entry per command: usage, summary, detail, handler
  ArgParser.cs                     options / flags / positional words, `--` terminator
  CliConfig.cs                     config file + environment overrides
  DeploymentClient.cs              the only place that speaks HTTP
  DueDates.cs                      --due / --reminder date formats
  Prompt.cs                        interactive and no-echo input for `configure`
  ExitCode.cs                      exit codes and CliException
  Commands/                        configure, doctor, capture, append, todo
test/OneNoteSystem.Tests/          xUnit; FakeDeployment replaces the old fake backend server
```

## Build and test

```bash
dotnet test
```

Run the CLI locally:

```bash
dotnet run --project src/OneNoteSystem.Cli -- --help
```

Install as a global tool from source:

```bash
dotnet pack -c Release && dotnet tool install --global --add-source src/OneNoteSystem.Cli/bin/Release OneNoteSystem.Cli
```

### Coming from npm

Versions up to 1.1.0 shipped as the npm package `onenotesystem`. That package is
deprecated and receives no further releases — uninstall it and install the .NET tool above:

```bash
npm uninstall -g onenotesystem
```

## Commands

| Command | Purpose |
| --- | --- |
| `configure` | Save your deployment URL and API key |
| `doctor` | Check the deployment, database, and key |
| `capture` (`new`) | Create a new page in your default section |
| `append` (`add`) | Add text to an existing page |
| `todo add\|list\|lists\|done` | Work with Microsoft To Do tasks |

## Configuration

Read from the environment first, then the saved config file:
`ONENOTE_URL`, `ONENOTE_API_KEY`, `ONENOTE_DEFAULT_PAGE`, `ONENOTE_TIMEOUT_MS`.

| Platform | Config file |
| --- | --- |
| Linux, macOS | `~/.config/onenotesystem/config.json` (mode 600) |
| Windows | `%APPDATA%\onenotesystem\config.json` |

## Exit codes

`0` ok · `1` usage · `2` unauthorized · `3` page not found · `4` network ·
`5` deployment error · `6` not configured · `7` conflict (no default section, or more than
one page has that title)

Docs: <https://onenotesystem.erinskidds.com/terminal>

## License

MIT — see [LICENSE](LICENSE).
