// Every user-facing command. Each returns an exit code; --json output goes to
// stdout alone, progress and hints go to stderr, so piping stays clean.

import { readFileSync } from 'node:fs';
import { parseArgs, readStdin } from './args.js';
import { Client } from './client.js';
import { loadSettings, normalizeUrl, requireSettings, writeConfigFile } from './config.js';
import { CliError, EXIT } from './exit.js';
import { createPrompter } from './prompt.js';
import { formatDue, localTimeZone, parseDueDate } from './dates.js';

// Mirrors MAX_NOTE_CONTENT_LENGTH on the deployment. Checked locally so a
// pasted file that is far too long fails instantly instead of after an upload.
export const MAX_CONTENT_LENGTH = 100_000;
export const MAX_TITLE_LENGTH = 200;

const out = (text) => process.stdout.write(`${text}\n`);
const note = (text) => process.stderr.write(`${text}\n`);
const emitJson = (value) => process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);

function clientFor(env = process.env) {
  const settings = requireSettings(env);
  const timeout = Number(env.ONENOTE_TIMEOUT_MS || 15000);
  return { client: new Client({ ...settings, timeoutMs: timeout }), settings };
}

// --content, --file, and --stdin all fill the same field, so take exactly one.
async function resolveContent(flags, { required }) {
  const sources = ['content', 'file', 'stdin'].filter((name) => flags[name] !== undefined);
  if (sources.length > 1) {
    throw new CliError(`pass only one of ${sources.map((s) => `--${s}`).join(', ')}`, EXIT.USAGE);
  }

  let content = '';
  if (flags.stdin) content = await readStdin();
  else if (flags.file !== undefined) content = readContentFile(flags.file);
  else if (flags.content !== undefined) content = flags.content;

  if (required && !content.trim()) {
    throw new CliError('content is required. Use --content, --file, or --stdin.', EXIT.USAGE);
  }
  if (content.length > MAX_CONTENT_LENGTH) {
    throw new CliError(
      `content is ${content.length.toLocaleString('en-US')} characters; the limit is ${MAX_CONTENT_LENGTH.toLocaleString('en-US')}`,
      EXIT.USAGE
    );
  }
  return content;
}

function readContentFile(path) {
  try {
    return readFileSync(path, 'utf8');
  } catch (err) {
    throw new CliError(`could not read --file ${path}: ${err.code || err.message}`, EXIT.USAGE);
  }
}

// The deployment silently drops a URL it cannot parse, so reject it here where
// the user can still see which flag was wrong.
function validateUrl(raw) {
  let url;
  try {
    url = new URL(raw);
  } catch {
    throw new CliError(`--url "${raw}" is not a URL`, EXIT.USAGE);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new CliError('--url must be http or https', EXIT.USAGE);
  }
  return url.toString();
}

function checkTitle(value, flag) {
  if (value.length > MAX_TITLE_LENGTH) {
    throw new CliError(`${flag} cannot exceed ${MAX_TITLE_LENGTH} characters`, EXIT.USAGE);
  }
  return value;
}

// ---------------------------------------------------------------- configure

export async function configure(argv) {
  const { flags } = parseArgs(
    argv,
    { url: 'string', 'api-key': 'string', 'default-page': 'string', 'no-verify': 'boolean' },
    'configure'
  );

  // Only open a prompter when something is actually missing, so a fully
  // flagged `configure` works in scripts and containers with no terminal.
  const prompter = flags.url && flags['api-key'] ? null : createPrompter();
  let url;
  let apiKey;
  try {
    url = normalizeUrl(flags.url || (await prompter.ask('OneNote System deployment URL: ')));
    apiKey = flags['api-key'] || (await prompter.askSecret('API key (input hidden): '));
  } finally {
    prompter?.close();
  }
  if (!apiKey) {
    throw new CliError(`an API key is required. Create one in Setup Step 5 at ${url}/setup`, EXIT.USAGE);
  }

  if (!flags['no-verify']) {
    note('Checking the deployment...');
    const health = await checkDeployment(url, apiKey);
    if (health.ok === false) {
      throw new CliError(health.error || 'the deployment reported that it is not healthy', EXIT.BACKEND);
    }
    note(`Reached ${health.service || 'the deployment'}.`);
  }

  const file = writeConfigFile({ url, apiKey, defaultPage: flags['default-page'] });
  out(`Saved configuration to ${file}`);
  note('The API key is stored in that file with owner-only permissions and is never printed.');
  return EXIT.OK;
}

// Pointing at the wrong site is the most common setup mistake -- a personal
// dashboard, a marketing page, a company intranet. Those answer, so the bare
// transport error ("HTTP 404") reads like the deployment is broken rather than
// like the URL is wrong. Name the real problem instead.
const WRONG_SITE_CODES = new Set([EXIT.NOT_FOUND, EXIT.BACKEND, EXIT.USAGE]);

async function checkDeployment(url, apiKey) {
  let health;
  try {
    health = await new Client({ url, apiKey }).get('/health', { authenticated: false, tolerate: [503] });
  } catch (err) {
    if (err instanceof CliError && WRONG_SITE_CODES.has(err.code)) {
      throw new CliError(
        `${url} answered, but it is not a OneNote System deployment (GET ${url}/api/health said: ${err.message}). ` +
        'Use the address of your own OneNote System deployment.',
        EXIT.BACKEND
      );
    }
    throw err; // network and auth failures already say the right thing
  }

  // A healthy deployment identifies itself. Anything else that happens to
  // return JSON here is some other service.
  if (health.service && health.service !== 'onenote-system') {
    throw new CliError(
      `${url} is running "${health.service}", not OneNote System. Use the address of your own OneNote System deployment.`,
      EXIT.BACKEND
    );
  }
  return health;
}

// ------------------------------------------------------------------- doctor

export async function doctor(argv) {
  const { flags } = parseArgs(argv, { json: 'boolean' }, 'doctor');
  const settings = loadSettings();

  if (!settings.url || !settings.apiKey) {
    const problem = {
      ok: false,
      configured: false,
      error: 'no deployment URL or API key configured',
      configFile: settings.file,
    };
    if (flags.json) emitJson(problem);
    else {
      out(`Deployment URL: ${settings.url || '(unset)'}`);
      out(`API key:        ${settings.apiKey ? 'set' : '(unset)'}`);
      note('Run `onenotesystem configure` to set them.');
    }
    return EXIT.CONFIG;
  }

  const { client } = clientFor();
  const health = await client.get('/health', { authenticated: false, tolerate: [503] });

  // /api/health does not read the API key, so prove the key separately. A
  // deliberately invalid append is the only authenticated check available, and
  // a 400 means the key was accepted before validation rejected the body.
  const auth = await probeApiKey(client);

  if (flags.json) {
    emitJson({ ok: health.ok !== false && auth.ok, url: settings.url, sources: settings.source, health, auth });
    return auth.ok && health.ok !== false ? EXIT.OK : auth.code || EXIT.BACKEND;
  }

  out(`Deployment URL: ${settings.url}  (from ${settings.source.url})`);
  out(`API key:        set (from ${settings.source.apiKey})`);
  if (settings.defaultPage) out(`Default page:   ${settings.defaultPage}  (from ${settings.source.defaultPage})`);
  out(`Service:        ${health.service || 'unknown'}`);
  out(`Database:       ${health.ok === false ? `unavailable — ${health.error}` : 'reachable'}`);
  out(`Key:            ${auth.message}`);
  if (health.ok === false) return EXIT.BACKEND;
  return auth.ok ? EXIT.OK : auth.code;
}

async function probeApiKey(client) {
  try {
    await client.post('/append', { content: '' });
    // A success here would mean the deployment stopped validating content.
    return { ok: true, message: 'accepted' };
  } catch (err) {
    if (err.code === EXIT.USAGE) return { ok: true, message: 'accepted' };
    if (err.code === EXIT.AUTH) return { ok: false, code: EXIT.AUTH, message: 'rejected — run `onenotesystem configure` again' };
    if (err.code === EXIT.CONFLICT) return { ok: false, code: EXIT.CONFLICT, message: `accepted, but ${err.message}` };
    return { ok: false, code: err.code || EXIT.BACKEND, message: err.message };
  }
}

// ------------------------------------------------------------------ capture

// POST /api/capture -- the same call the "create a page" Shortcut makes.
export async function capture(argv) {
  const { flags, positionals } = parseArgs(
    argv,
    { title: 'string', content: 'string', file: 'string', url: 'string', stdin: 'boolean', json: 'boolean' },
    'capture'
  );

  const title = checkTitle((flags.title || positionals.join(' ')).trim(), '--title');
  if (!title) throw new CliError('a page title is required (positional or --title)', EXIT.USAGE);

  const content = await resolveContent(flags, { required: false });

  const payload = { title, content };
  if (flags.url) payload.url = validateUrl(flags.url);

  const { client } = clientFor();
  const response = await client.post('/capture', payload);
  const page = response.page || {};

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  out(`Created "${page.title || title}"`);
  if (page.webUrl) out(page.webUrl);
  else if (page.id) out(`id: ${page.id}`);
  return EXIT.OK;
}

// ------------------------------------------------------------------- append

// POST /api/append -- the same call the "add to my inbox page" Shortcut makes.
export async function append(argv) {
  const { flags, positionals } = parseArgs(
    argv,
    {
      'page-title': 'string',
      'page-id': 'string',
      content: 'string',
      file: 'string',
      url: 'string',
      stdin: 'boolean',
      json: 'boolean',
    },
    'append'
  );

  if (flags['page-title'] && flags['page-id']) {
    throw new CliError('pass either --page-title or --page-id, not both', EXIT.USAGE);
  }

  // Bare words are the content, not the title: appending to a configured
  // default page is the common case, and `onenotesystem append "a thought"`
  // should just work.
  const positionalContent = positionals.join(' ').trim();
  if (positionalContent && (flags.content !== undefined || flags.file !== undefined || flags.stdin)) {
    throw new CliError('pass the content either as words or with --content/--file/--stdin, not both', EXIT.USAGE);
  }

  const content = positionalContent || (await resolveContent(flags, { required: true }));
  if (!content.trim()) throw new CliError('content is required and cannot be empty', EXIT.USAGE);
  if (content.length > MAX_CONTENT_LENGTH) {
    throw new CliError(
      `content is ${content.length.toLocaleString('en-US')} characters; the limit is ${MAX_CONTENT_LENGTH.toLocaleString('en-US')}`,
      EXIT.USAGE
    );
  }

  const { client, settings } = clientFor();

  const payload = { content };
  if (flags['page-id']) {
    payload.pageId = flags['page-id'];
  } else {
    const pageTitle = checkTitle((flags['page-title'] || settings.defaultPage || '').trim(), '--page-title');
    if (!pageTitle) {
      throw new CliError(
        'no page to append to. Pass --page-title or --page-id, or save one with `onenotesystem configure --default-page "Quick Inbox"`.',
        EXIT.USAGE
      );
    }
    payload.pageTitle = pageTitle;
  }
  if (flags.url) payload.url = validateUrl(flags.url);

  const response = await client.post('/append', payload);
  const page = response.page || {};

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  out(`Appended to "${page.title || payload.pageTitle || page.id}"`);
  if (page.webUrl) out(page.webUrl);
  return EXIT.OK;
}

// -------------------------------------------------------------- microsoft to do

// Not every deployment exposes To Do: the endpoints exist only where the
// Microsoft connection has the Tasks.ReadWrite scope. A bare 404 would read as
// "no such task", so name the real reason.
function todoUnsupported(err, settings) {
  if (err instanceof CliError && err.code === EXIT.NOT_FOUND && /^HTTP 404$/.test(err.message)) {
    return new CliError(
      `${settings.url} does not support Microsoft To Do. Point at a deployment that does, or use capture/append.`,
      EXIT.BACKEND
    );
  }
  return err;
}

async function todoRequest(client, settings, run) {
  try {
    return await run();
  } catch (err) {
    throw todoUnsupported(err, settings);
  }
}

export async function todoAdd(argv) {
  const { flags, positionals } = parseArgs(
    argv,
    { title: 'string', note: 'string', list: 'string', due: 'string', reminder: 'string', json: 'boolean' },
    'todo add'
  );

  const title = checkTitle((flags.title || positionals.join(' ')).trim(), '--title');
  if (!title) throw new CliError('a task title is required (positional or --title)', EXIT.USAGE);

  const payload = { title, timeZone: localTimeZone() };
  if (flags.note) payload.note = flags.note;
  if (flags.list) payload.list = flags.list;
  if (flags.due) payload.dueDate = parseDueDate(flags.due, '--due');
  if (flags.reminder) payload.reminder = parseDueDate(flags.reminder, '--reminder');

  const { client, settings } = clientFor();
  const response = await todoRequest(client, settings, () => client.post('/todo', payload));
  const task = response.task || {};

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  out(`Added "${task.title || title}" to ${task.list || 'To Do'}`);
  if (task.dueDateTime) out(`due: ${formatDue(task.dueDateTime)}`);
  return EXIT.OK;
}

export async function todoList(argv) {
  const { flags } = parseArgs(argv, { list: 'string', all: 'boolean', top: 'string', json: 'boolean' }, 'todo list');

  const params = new URLSearchParams();
  if (flags.list) params.set('list', flags.list);
  if (flags.all) params.set('all', 'true');
  if (flags.top) params.set('top', flags.top);
  const query = params.toString();

  const { client, settings } = clientFor();
  const response = await todoRequest(client, settings, () => client.get(`/todo${query ? `?${query}` : ''}`));
  const tasks = response.tasks || [];

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  if (!tasks.length) {
    out(`No ${flags.all ? '' : 'open '}tasks in "${response.list || 'To Do'}".`);
    return EXIT.OK;
  }
  for (const task of tasks) {
    const due = task.dueDateTime ? `  [due ${formatDue(task.dueDateTime)}]` : '';
    out(`${task.completed ? '[x]' : '[ ]'} ${task.title}${due}`);
    out(`    ${task.id}`);
  }
  return EXIT.OK;
}

export async function todoLists(argv) {
  const { flags } = parseArgs(argv, { json: 'boolean' }, 'todo lists');
  const { client, settings } = clientFor();
  const response = await todoRequest(client, settings, () => client.get('/todo/lists'));

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  for (const list of response.lists || []) {
    out(`${list.name}${list.isDefault ? '  (default)' : ''}`);
  }
  return EXIT.OK;
}

export async function todoDone(argv) {
  const { flags, positionals } = parseArgs(argv, { list: 'string', json: 'boolean' }, 'todo done');
  const id = (positionals[0] || '').trim();
  if (!id) throw new CliError('a task id is required. Run `onenotesystem todo list` to see them.', EXIT.USAGE);

  const payload = { id };
  if (flags.list) payload.list = flags.list;

  const { client, settings } = clientFor();
  const response = await todoRequest(client, settings, () => client.post('/todo/complete', payload));
  const task = response.task || {};

  if (flags.json) {
    emitJson(response);
    return EXIT.OK;
  }
  out(`Completed "${task.title || id}"`);
  return EXIT.OK;
}
