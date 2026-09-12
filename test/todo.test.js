import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, test } from 'node:test';
import { EXIT } from '../src/exit.js';
import { formatDue, parseDueDate } from '../src/dates.js';
import { startFakeBackend, VALID_KEY } from './fake-backend.js';

const BIN = fileURLToPath(new URL('../bin/onenotesystem.js', import.meta.url));
const configDir = mkdtempSync(join(tmpdir(), 'onenotesystem-todo-'));

after(() => rmSync(configDir, { recursive: true, force: true }));

const TODO = {
  lists: [
    { id: 'l1', name: 'Tasks', isDefault: true },
    { id: 'l2', name: 'Errands', isDefault: false },
  ],
  tasks: [
    { id: 'task-a', title: 'Renew the domain', status: 'notStarted', completed: false, dueDateTime: '2026-09-15' },
    { id: 'task-b', title: 'Call the bank', status: 'completed', completed: true, dueDateTime: null },
  ],
};

function cli(args, { url, apiKey = VALID_KEY } = {}) {
  return new Promise((resolve) => {
    execFile(
      process.execPath,
      [BIN, ...args],
      {
        env: {
          ...process.env,
          ONENOTE_CONFIG_DIR: configDir,
          ...(url ? { ONENOTE_URL: url } : {}),
          ...(apiKey ? { ONENOTE_API_KEY: apiKey } : {}),
        },
      },
      (err, stdout, stderr) => resolve({ code: err ? err.code : 0, stdout, stderr })
    ).stdin.end();
  });
}

// ------------------------------------------------------------------ dates

test('parseDueDate accepts plain dates, day words, and offsets', () => {
  const now = new Date(2026, 8, 12); // 12 Sep 2026, local
  assert.equal(parseDueDate('2026-09-15', '--due', now), '2026-09-15');
  assert.equal(parseDueDate('today', '--due', now), '2026-09-12');
  assert.equal(parseDueDate('tomorrow', '--due', now), '2026-09-13');
  assert.equal(parseDueDate('+3d', '--due', now), '2026-09-15');
  assert.equal(parseDueDate('+1w', '--due', now), '2026-09-19');
});

// A bare date must not become UTC midnight, or it lands a day early for
// anyone west of London.
test('parseDueDate keeps a bare date as a bare date', () => {
  const parsed = parseDueDate('2026-09-15', '--due');
  assert.equal(parsed, '2026-09-15');
  assert.doesNotMatch(parsed, /T|Z/);
});

test('parseDueDate keeps a date-time local and unzoned', () => {
  assert.equal(parseDueDate('2026-09-15T14:30', '--due'), '2026-09-15T14:30:00');
});

test('parseDueDate rejects impossible and unparseable dates', () => {
  assert.throws(() => parseDueDate('2026-02-30', '--due'), { code: EXIT.USAGE });
  assert.throws(() => parseDueDate('next thursday', '--due'), (err) => /is not a date/.test(err.message));
  assert.throws(() => parseDueDate('', '--due'), { code: EXIT.USAGE });
});

test('formatDue leaves a bare date alone', () => {
  assert.equal(formatDue('2026-09-15'), '2026-09-15');
  assert.equal(formatDue(null), '');
});

// ------------------------------------------------------------------- cli

test('todo add posts the title, list, due date, and time zone', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'add', 'Renew', 'the', 'domain', '--list', 'Errands', '--due', '2026-09-15'], {
      url: backend.url,
    });
    assert.equal(result.code, EXIT.OK);

    const request = backend.lastRequest();
    assert.equal(request.path, '/api/todo');
    assert.equal(request.body.title, 'Renew the domain');
    assert.equal(request.body.list, 'Errands');
    assert.equal(request.body.dueDate, '2026-09-15');
    assert.ok(request.body.timeZone, 'a time zone must be sent so a bare date is anchored correctly');
    assert.match(result.stdout, /Added "Renew the domain" to Errands/);
  } finally {
    await backend.close();
  }
});

test('todo add validates the date before sending anything', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'add', 'Thing', '--due', 'someday'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /is not a date/);
    assert.equal(backend.requests.length, 0);
  } finally {
    await backend.close();
  }
});

test('todo add requires a title', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'add', '--due', 'tomorrow'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /title is required/);
    assert.equal(backend.requests.length, 0);
  } finally {
    await backend.close();
  }
});

test('todo list shows open tasks with their ids', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'list'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    assert.match(result.stdout, /\[ \] Renew the domain\s+\[due 2026-09-15\]/);
    assert.match(result.stdout, /task-a/);
    assert.match(result.stdout, /\[x\] Call the bank/);
  } finally {
    await backend.close();
  }
});

test('todo list passes --list, --all, and --top through as query params', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    await cli(['todo', 'list', '--list', 'Errands', '--all', '--top', '5'], { url: backend.url });
    const { path } = backend.lastRequest();
    assert.match(path, /^\/api\/todo\?/);
    assert.match(path, /list=Errands/);
    assert.match(path, /all=true/);
    assert.match(path, /top=5/);
  } finally {
    await backend.close();
  }
});

test('todo lists marks the default list', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'lists'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    assert.match(result.stdout, /Tasks\s+\(default\)/);
    assert.match(result.stdout, /Errands/);
  } finally {
    await backend.close();
  }
});

test('todo done completes a task by id', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'done', 'task-a'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    assert.equal(backend.lastRequest().path, '/api/todo/complete');
    assert.equal(backend.lastRequest().body.id, 'task-a');
    assert.match(result.stdout, /Completed/);
  } finally {
    await backend.close();
  }
});

test('todo done requires an id', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'done'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /task id is required/);
    assert.equal(backend.requests.length, 0);
  } finally {
    await backend.close();
  }
});

test('an unknown todo subcommand lists the real ones', async () => {
  const result = await cli(['todo', 'frobnicate'], { url: '', apiKey: '' });
  assert.equal(result.code, EXIT.USAGE);
  assert.match(result.stderr, /add\|list\|lists\|done/);
});

// A deployment without the Tasks.ReadWrite scope simply has no /api/todo. A
// bare "HTTP 404" would read as "no such task".
test('a deployment without To Do says so instead of reporting 404', async () => {
  const backend = await startFakeBackend({ todo: null });
  try {
    const result = await cli(['todo', 'add', 'Anything'], { url: backend.url });
    assert.equal(result.code, EXIT.BACKEND);
    assert.match(result.stderr, /does not support Microsoft To Do/);
    assert.doesNotMatch(result.stderr, /HTTP 404/);
  } finally {
    await backend.close();
  }
});

// The deployment's own wording explains the re-consent step, so it must survive.
test('a real To Do error from the deployment is passed through', async () => {
  const backend = await startFakeBackend({ todo: TODO });
  try {
    const result = await cli(['todo', 'add', 'Thing', '--list', 'Nowhere'], { url: backend.url });
    assert.equal(result.code, EXIT.NOT_FOUND);
    assert.match(result.stderr, /No To Do list called "Nowhere"/);
  } finally {
    await backend.close();
  }
});

test('todo has help, and so does each subcommand path', async () => {
  for (const args of [['todo', '--help'], ['help', 'todo']]) {
    const result = await cli(args, { url: '', apiKey: '' });
    assert.equal(result.code, EXIT.OK);
    assert.match(result.stdout, /^onenotesystem todo/);
    assert.match(result.stdout, /--due <when>/);
  }
});
