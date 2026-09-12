import { createRequire } from 'node:module';
import { CliError, EXIT } from './exit.js';
import { append, capture, configure, doctor } from './commands.js';

// Read from package.json rather than repeating the number here, so `npm version`
// is the only place a release is recorded and `--version` cannot drift from the
// version npm actually published.
export const VERSION = createRequire(import.meta.url)('../package.json').version;

// One entry per command, so `--help`, `help <command>`, and the command list in
// the top-level help can never drift apart.
const COMMANDS = {
  configure: {
    handler: configure,
    usage: 'onenotesystem configure [options]',
    summary: 'Save your deployment URL and API key',
    detail: `Asks for your deployment URL and an API key from your deployment's
setup page, checks that the deployment answers, and saves both to a config file
only your user account can read.

  --url <url>            Deployment URL, e.g. https://onenote.example.com
  --api-key <key>        API key from Setup Step 5
  --default-page <title> Page that \`append\` uses when you do not name one
  --no-verify            Save without checking that the deployment answers

Pass --url and --api-key to configure a server or container without prompts.
The key is never printed back to the terminal.

Config file
  Linux, macOS   ~/.config/onenotesystem/config.json  (mode 600)
  Windows        %APPDATA%\\onenotesystem\\config.json`,
  },

  doctor: {
    handler: doctor,
    usage: 'onenotesystem doctor [--json]',
    summary: 'Check the deployment, database, and key',
    detail: `Tests the deployment, its database, and your API key separately, so
a failure tells you which one is wrong.

  --json                 Machine-readable output on stdout only

Exits 0 when everything is reachable, or the exit code of the first problem it
finds: 2 for a rejected key, 6 when nothing is configured, 7 when no default
OneNote section has been chosen.`,
  },

  capture: {
    handler: capture,
    aliases: ['new'],
    usage: 'onenotesystem capture <title> [options]',
    summary: 'Create a new page in your default section',
    detail: `Creates a new page in the OneNote section you chose during setup.
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
  git log -1 --stat | onenotesystem capture "Today's commit" --stdin`,
  },

  append: {
    handler: append,
    aliases: ['add'],
    usage: 'onenotesystem append <text> [options]',
    summary: 'Add text to an existing page',
    detail: `Adds text to a page that already exists -- a running inbox, a daily
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
  dmesg | tail -20 | onenotesystem append --stdin --page-title "Server log"`,
  },
};

// new -> capture, add -> append, and every canonical name to itself.
const ALIASES = new Map(
  Object.entries(COMMANDS).flatMap(([name, spec]) => [
    [name, name],
    ...(spec.aliases || []).map((alias) => [alias, name]),
  ])
);

const FOOTER = `Configuration
  Values are read from the environment first, then the saved config file:
    ONENOTE_URL, ONENOTE_API_KEY, ONENOTE_DEFAULT_PAGE, ONENOTE_TIMEOUT_MS

Exit codes
  0 ok   1 usage   2 unauthorized   3 page not found   4 network
  5 deployment error   6 not configured   7 conflict (no default section,
  or more than one page has that title)

Docs: https://onenotesystem.erinskidds.com/terminal
`;

function generalHelp() {
  const rows = Object.entries(COMMANDS).map(([name, spec]) => {
    const alias = spec.aliases?.length ? ` (alias: ${spec.aliases.join(', ')})` : '';
    return `  ${spec.usage.padEnd(42)}${spec.summary}${alias}`;
  });

  return `onenotesystem ${VERSION} - create and update Microsoft OneNote pages from the terminal.

Usage
${rows.join('\n')}

Run \`onenotesystem help <command>\` or \`onenotesystem <command> --help\` for the
options of a single command.

Global
  --help, -h, --version, -v

${FOOTER}`;
}

function commandHelp(name) {
  const spec = COMMANDS[name];
  return `${spec.usage}\n\n${spec.summary}.\n\n${spec.detail}\n\n${FOOTER}`;
}

export async function run(argv) {
  try {
    return await dispatch(argv);
  } catch (err) {
    if (err instanceof CliError) {
      process.stderr.write(`onenotesystem: ${err.message}\n`);
      if (err.code === EXIT.CONFIG) process.stderr.write('Run `onenotesystem configure` to get started.\n');
      if (err.code === EXIT.AUTH) process.stderr.write('Re-run `onenotesystem configure` with a current API key.\n');
      return err.code;
    }
    throw err;
  }
}

// Help is resolved before the command's own flag parser runs, so `--help` and
// `-h` work everywhere rather than being reported as an unknown option (or,
// worse, quietly becoming a page title).
function wantsHelp(argv) {
  const end = argv.indexOf('--');
  const scope = end === -1 ? argv : argv.slice(0, end);
  return scope.includes('--help') || scope.includes('-h');
}

async function dispatch(argv) {
  const [command, ...rest] = argv;

  if (!command || command === '--help' || command === '-h') {
    process.stdout.write(generalHelp());
    return EXIT.OK;
  }
  if (command === '--version' || command === '-v' || command === 'version') {
    process.stdout.write(`${VERSION}\n`);
    return EXIT.OK;
  }

  if (command === 'help') {
    const topic = rest.find((arg) => !arg.startsWith('-'));
    if (!topic) {
      process.stdout.write(generalHelp());
      return EXIT.OK;
    }
    const name = ALIASES.get(topic);
    if (!name) {
      throw new CliError(`no help topic for "${topic}". Run \`onenotesystem --help\`.`, EXIT.USAGE);
    }
    process.stdout.write(commandHelp(name));
    return EXIT.OK;
  }

  const name = ALIASES.get(command);
  if (!name) {
    throw new CliError(`unknown command "${command}". Run \`onenotesystem --help\`.`, EXIT.USAGE);
  }

  if (wantsHelp(rest)) {
    process.stdout.write(commandHelp(name));
    return EXIT.OK;
  }

  return COMMANDS[name].handler(rest);
}
