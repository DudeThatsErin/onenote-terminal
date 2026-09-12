import { CliError, EXIT } from './exit.js';
import { append, capture, configure, doctor } from './commands.js';

export const VERSION = '1.0.0';

const HELP = `onenotesystem ${VERSION} - create and update Microsoft OneNote pages from the terminal.

Usage
  onenotesystem configure                    Save your deployment URL and API key
  onenotesystem doctor [--json]              Check the deployment, database, and key
  onenotesystem capture <title> [options]    Create a new page in your default section
  onenotesystem append <text> [options]      Add text to an existing page

capture  (alias: new)
  <title>                Page title, as positional words, or use --title
  --content <text>       Page body
  --file <path>          Read the body from a file
  --stdin                Read the body from piped stdin
  --url <link>           Record a source link under the body
  --json                 Print the deployment's JSON response

append  (alias: add)
  <text>                 Text to append, as positional words, or use --content
  --file <path>          Read the text from a file
  --stdin                Read the text from piped stdin
  --page-title <title>   Exact title of a page in your default section
  --page-id <id>         Target a page directly, from any section
  --url <link>           Record a source link under the text
  --json

  With neither --page-title nor --page-id, the saved default page is used.
  Save one with: onenotesystem configure --default-page "Quick Inbox"

Global
  --help, --version

Configuration
  Values are read from the environment first, then the saved config file:
    ONENOTE_URL, ONENOTE_API_KEY, ONENOTE_DEFAULT_PAGE, ONENOTE_TIMEOUT_MS

Exit codes
  0 ok   1 usage   2 unauthorized   3 page not found   4 network
  5 deployment error   6 not configured   7 conflict (no default section,
  or more than one page has that title)

Examples
  onenotesystem capture "Standup notes" --content "Shipped the CLI"
  git log -1 --stat | onenotesystem capture "Today's commit" --stdin
  onenotesystem append "Ask about the Q3 budget" --page-title "Quick Inbox"
  onenotesystem capture "Article" --file notes.md --url https://example.com

Docs: https://onenotesystem.erinskidds.com/terminal
`;

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

async function dispatch(argv) {
  const [command, ...rest] = argv;

  if (!command || command === '--help' || command === '-h' || command === 'help') {
    process.stdout.write(HELP);
    return EXIT.OK;
  }
  if (command === '--version' || command === '-v' || command === 'version') {
    process.stdout.write(`${VERSION}\n`);
    return EXIT.OK;
  }

  switch (command) {
    case 'configure':
      return configure(rest);
    case 'doctor':
      return doctor(rest);
    case 'capture':
    case 'new':
      return capture(rest);
    case 'append':
    case 'add':
      return append(rest);
    default:
      throw new CliError(`unknown command "${command}". Run \`onenotesystem --help\`.`, EXIT.USAGE);
  }
}
