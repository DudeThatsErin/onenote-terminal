# OneNote System Terminal

Create and update Microsoft OneNote pages from any shell.

This is the terminal client for [OneNote System](https://onenotesystem.erinskidds.com). It talks to your own OneNote System deployment over the same two HTTPS endpoints the Apple Shortcuts use — `POST /api/capture` and `POST /api/append` — so a page created from your laptop and a page created from your phone are identical. Where the deployment supports it, the same key also drives Microsoft To Do.

It exists for the machines OneNote will not run on: Linux desktops, servers you only reach over SSH, and work computers where you cannot install the OneNote app. All you need is Node and your API key.

```bash
npm install -g onenotesystem
onenotesystem configure
onenotesystem capture "Standup notes" --content "Shipped the CLI"
```

## Install

```bash
npm install -g onenotesystem
```

Requires Node.js 18.17 or newer. Installs two commands: `onenotesystem` and the shorter alias `ons`.

To run it once without installing:

```bash
npx onenotesystem capture "Quick thought"
```

## Getting help

Every command documents itself. `--help` and `-h` work at the top level and on each command, and `help <command>` does the same thing:

```bash
onenotesystem --help              # commands, configuration, exit codes
onenotesystem capture --help      # every flag capture accepts, with examples
onenotesystem help append         # the same, spelled the other way
```

Aliases resolve too, so `onenotesystem new -h` shows the `capture` help.

## Configure

You need a OneNote System deployment and an API key from its `/setup` page. If you do not have one yet, follow the [guided setup](https://onenotesystem.erinskidds.com/setup) first.

```bash
onenotesystem configure
```

It asks for your deployment URL and API key, checks that the deployment answers, and saves both to a file only your user account can read:

| Platform | Location |
| --- | --- |
| Linux, macOS | `~/.config/onenotesystem/config.json` (mode `600`) |
| Windows | `%APPDATA%\onenotesystem\config.json` |

You can also pass the values as flags, which is what you want in a script:

```bash
onenotesystem configure --url https://onenote.example.com --api-key "$KEY" --default-page "Quick Inbox"
```

Or skip the file entirely and use environment variables, which take precedence over it:

| Variable | Purpose |
| --- | --- |
| `ONENOTE_URL` | Your deployment's origin, e.g. `https://onenote.example.com` |
| `ONENOTE_API_KEY` | An API key from your deployment's setup page |
| `ONENOTE_DEFAULT_PAGE` | Page title `append` uses when you do not name one |
| `ONENOTE_TIMEOUT_MS` | Request timeout, default `15000` |
| `ONENOTE_CONFIG_DIR` | Override where the config file lives |

Check everything at once:

```bash
onenotesystem doctor
```

```
Deployment URL: https://onenote.example.com  (from config file)
API key:        set (from config file)
Default page:   Quick Inbox  (from config file)
Service:        onenote-system
Database:       reachable
Key:            accepted
```

## Create a page

`capture` creates a new page in the OneNote section you chose during setup.

```bash
onenotesystem capture "Standup notes"
onenotesystem capture "Standup notes" --content "Shipped the CLI"
onenotesystem capture "Article" --file notes.md --url https://example.com/article
git log -1 --stat | onenotesystem capture "Today's commit" --stdin
```

| Flag | Meaning |
| --- | --- |
| `--title <text>` | Title, if you would rather not use positional words |
| `--content <text>` | Page body |
| `--file <path>` | Read the body from a file |
| `--stdin` | Read the body from piped stdin |
| `--url <link>` | Record a source link under the body |
| `--json` | Print the deployment's JSON response instead of a summary |

## Append to a page

`append` adds text to a page that already exists — a running inbox, a daily log, a project page.

```bash
onenotesystem append "Ask about the Q3 budget" --page-title "Quick Inbox"
onenotesystem append "Deploy finished" --page-id "1-abc123!..."
dmesg | tail -20 | onenotesystem append --stdin --page-title "Server log"
```

Set a default page once and the flag becomes optional:

```bash
onenotesystem configure --default-page "Quick Inbox"
onenotesystem append "A thought"
```

`--page-title` must match a page title exactly, and that page must be in your configured default section. If two pages share the title, the command stops and asks you to use `--page-id`, because appending to the wrong one silently would be worse.

## Microsoft To Do

`todo` creates and completes Microsoft To Do tasks through the same deployment and the same API key.

```bash
onenotesystem todo add "Renew the domain" --due +7d
onenotesystem todo add "Call the bank" --list Errands --due tomorrow --note "Ask about the fee"
onenotesystem todo list                      # open tasks, with their ids
onenotesystem todo lists                     # your To Do lists
onenotesystem todo done AAMkAG...            # mark one complete
```

| Flag | Meaning |
| --- | --- |
| `--list <name>` | Which To Do list to use; defaults to your default list |
| `--due <when>` | `2026-09-15`, `today`, `tomorrow`, `+3d`, `+2w`, or `2026-09-15T14:30` |
| `--reminder <when>` | Same formats; also switches the reminder on |
| `--note <text>` | A longer note on the task |
| `--all` | On `todo list`, include completed tasks |
| `--top <n>` | On `todo list`, how many to show (default 25, max 100) |

Dates are sent with your local time zone, so a bare date stays that day rather than shifting for anyone west of UTC.

This works only where the deployment's Microsoft connection has approved To Do access (the `Tasks.ReadWrite` scope). Deployments without it have no To Do endpoints at all, and the client says so rather than reporting a bare 404.

## Notes on content

Pages are created with a title, plain text, and an optional source link — the same small, reliable format the Shortcuts use. Line breaks are preserved. Markdown is not rendered; it lands as literal text.

Content is limited to 100,000 characters and titles to 200. Both are checked locally, so oversized input fails immediately instead of after an upload.

## Exit codes

Every command returns a stable exit code so shell scripts can branch on failure.

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Usage error — bad flags, missing input, failed local validation |
| `2` | Unauthorized — the deployment rejected the API key |
| `3` | Not found — no page matched the title or ID |
| `4` | Network — DNS, TLS, timeout, or connection refused |
| `5` | Deployment error the client cannot classify further |
| `6` | Not configured — no deployment URL or API key |
| `7` | Conflict — no default section, or more than one page has that title |

```bash
if ! onenotesystem append "$LINE" --page-title "Log"; then
  case $? in
    2) echo "Key expired; run onenotesystem configure" ;;
    4) echo "Offline; queueing locally" ;;
  esac
fi
```

`--json` output goes to stdout alone and progress messages go to stderr, so `onenotesystem capture ... --json | jq` always works.

## Security

Your API key is sent only to the deployment URL you configured, as `Authorization: Bearer`. It is never printed, logged, or included in an error message — including the "unauthorized" one. The config file is written with owner-only permissions.

Deployment URLs must use HTTPS. `http://` is accepted only for `localhost` and `127.0.0.1`, so a typo cannot quietly send your key over plaintext.

Your note content passes through your own deployment to Microsoft Graph. Nothing is sent anywhere else, and this client has no telemetry.

## Development

```bash
git clone https://github.com/DudeThatsErin/onenote-terminal.git
cd onenote-terminal
npm test
```

There are no runtime dependencies. The tests run the real binary against a fake deployment that reproduces the live API's status codes, so they fail if either side drifts.

## License

[MIT](LICENSE)
