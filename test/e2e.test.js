// Runs the real bin against the fake deployment in a child process, so the
// tests cover argument parsing, HTTP, stdout/stderr separation, and exit codes
// the same way a user's shell does.

import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, test } from 'node:test';
import { EXIT } from '../src/exit.js';
import { startFakeBackend, VALID_KEY } from './fake-backend.js';

const BIN = fileURLToPath(new URL('../bin/onenotesystem.js', import.meta.url));
const configDir = mkdtempSync(join(tmpdir(), 'onenotesystem-test-'));

after(() => rmSync(configDir, { recursive: true, force: true }));

function cli(args, { url, apiKey = VALID_KEY, stdin, ...extraEnv } = {}) {
  return new Promise((resolve) => {
    const child = execFile(
      process.execPath,
      [BIN, ...args],
      {
        env: {
          ...process.env,
          ONENOTE_CONFIG_DIR: configDir,
          ...(url ? { ONENOTE_URL: url } : {}),
          ...(apiKey ? { ONENOTE_API_KEY: apiKey } : {}),
          ...extraEnv,
        },
      },
      (err, stdout, stderr) => resolve({ code: err ? err.code : 0, stdout, stderr })
    );
    if (stdin !== undefined) child.stdin.end(stdin);
    else child.stdin.end();
  });
}

test('--help and --version exit 0', async () => {
  const help = await cli(['--help'], { url: '', apiKey: '' });
  assert.equal(help.code, EXIT.OK);
  assert.match(help.stdout, /onenotesystem capture/);

  const version = await cli(['--version'], { url: '', apiKey: '' });
  assert.equal(version.code, EXIT.OK);
  assert.match(version.stdout.trim(), /^\d+\.\d+\.\d+$/);
});

test('an unknown command is a usage error', async () => {
  const result = await cli(['frobnicate'], { url: '', apiKey: '' });
  assert.equal(result.code, EXIT.USAGE);
  assert.match(result.stderr, /unknown command "frobnicate"/);
});

test('commands exit 6 with no configuration', async () => {
  const result = await cli(['capture', 'Hello'], { url: '', apiKey: '' });
  assert.equal(result.code, EXIT.CONFIG);
  assert.match(result.stderr, /not configured/);
});

test('capture posts title, content, and url, and prints the web link', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(
      ['capture', 'Standup', 'notes', '--content', 'Shipped the CLI', '--url', 'https://example.com/a'],
      { url: backend.url }
    );
    assert.equal(result.code, EXIT.OK);

    const request = backend.lastRequest();
    assert.equal(request.path, '/api/capture');
    assert.equal(request.headers.authorization, `Bearer ${VALID_KEY}`);
    assert.deepEqual(request.body, {
      title: 'Standup notes',
      content: 'Shipped the CLI',
      url: 'https://example.com/a',
    });
    assert.match(result.stdout, /Created "Standup notes"/);
    assert.match(result.stdout, /https:\/\/onenote\.example\/page-created/);
  } finally {
    await backend.close();
  }
});

test('capture reads the body from stdin', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['capture', 'Piped', '--stdin'], { url: backend.url, stdin: 'from a pipe\nline two\n' });
    assert.equal(result.code, EXIT.OK);
    assert.equal(backend.lastRequest().body.content, 'from a pipe\nline two\n');
  } finally {
    await backend.close();
  }
});

test('--json writes only JSON to stdout', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['capture', 'Machine readable', '--json'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.page.title, 'Machine readable');
  } finally {
    await backend.close();
  }
});

test('capture requires a title', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['capture', '--content', 'orphan text'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /title is required/);
    assert.equal(backend.requests.length, 0, 'a local validation failure must not hit the network');
  } finally {
    await backend.close();
  }
});

test('a bad --url fails locally', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['capture', 'Bad link', '--url', 'ftp://example.com'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /--url must be http or https/);
    assert.equal(backend.requests.length, 0);
  } finally {
    await backend.close();
  }
});

test('append takes positional words as the content', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['append', 'Ask about the budget', '--page-title', 'Quick Inbox'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    assert.deepEqual(backend.lastRequest().body, { content: 'Ask about the budget', pageTitle: 'Quick Inbox' });
    assert.match(result.stdout, /Appended to "Quick Inbox"/);
  } finally {
    await backend.close();
  }
});

test('append falls back to the configured default page', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['append', 'A thought'], {
      url: backend.url,
      ONENOTE_DEFAULT_PAGE: 'Quick Inbox',
    });
    assert.equal(result.code, EXIT.OK);
    assert.equal(backend.lastRequest().body.pageTitle, 'Quick Inbox');
  } finally {
    await backend.close();
  }
});

test('append with no target and no default is a usage error', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['append', 'A thought'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /no page to append to/);
    assert.equal(backend.requests.length, 0);
  } finally {
    await backend.close();
  }
});

test('append rejects --page-title together with --page-id', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['append', 'text', '--page-title', 'A', '--page-id', 'B'], { url: backend.url });
    assert.equal(result.code, EXIT.USAGE);
    assert.match(result.stderr, /not both/);
  } finally {
    await backend.close();
  }
});

test('a missing page exits 3 and passes the deployment message through', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['append', 'text', '--page-title', 'Nowhere'], { url: backend.url });
    assert.equal(result.code, EXIT.NOT_FOUND);
    assert.match(result.stderr, /No page titled "Nowhere"/);
  } finally {
    await backend.close();
  }
});

test('an ambiguous page title exits 7', async () => {
  const backend = await startFakeBackend({ knownPages: ['Notes'], duplicateTitles: ['Notes'] });
  try {
    const result = await cli(['append', 'text', '--page-title', 'Notes'], { url: backend.url });
    assert.equal(result.code, EXIT.CONFLICT);
    assert.match(result.stderr, /More than one page/);
  } finally {
    await backend.close();
  }
});

test('a missing default section exits 7 on capture', async () => {
  const backend = await startFakeBackend({ hasDefaultSection: false });
  try {
    const result = await cli(['capture', 'Homeless'], { url: backend.url });
    assert.equal(result.code, EXIT.CONFLICT);
    assert.match(result.stderr, /no default OneNote section/);
  } finally {
    await backend.close();
  }
});

test('a rejected key exits 2 and never echoes the key', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['capture', 'Hello'], { url: backend.url, apiKey: 'ons_wrong_key' });
    assert.equal(result.code, EXIT.AUTH);
    assert.match(result.stderr, /unauthorized/);
    assert.doesNotMatch(result.stderr + result.stdout, /ons_wrong_key/);
  } finally {
    await backend.close();
  }
});

test('an unreachable deployment exits 4', async () => {
  const backend = await startFakeBackend();
  const url = backend.url;
  await backend.close();

  const result = await cli(['capture', 'Hello'], { url, ONENOTE_TIMEOUT_MS: '2000' });
  assert.equal(result.code, EXIT.NETWORK);
  assert.match(result.stderr, /could not reach/);
});

test('doctor reports a healthy deployment and an accepted key', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(['doctor'], { url: backend.url });
    assert.equal(result.code, EXIT.OK);
    assert.match(result.stdout, /Database:\s+reachable/);
    assert.match(result.stdout, /Key:\s+accepted/);
    assert.doesNotMatch(result.stdout, new RegExp(VALID_KEY));
  } finally {
    await backend.close();
  }
});

test('doctor reports an unhealthy database', async () => {
  const backend = await startFakeBackend({ healthy: false });
  try {
    const result = await cli(['doctor'], { url: backend.url });
    assert.equal(result.code, EXIT.BACKEND);
    assert.match(result.stdout, /Database:\s+unavailable/);
  } finally {
    await backend.close();
  }
});

test('doctor exits 6 and stays offline when nothing is configured', async () => {
  const result = await cli(['doctor'], { url: '', apiKey: '' });
  assert.equal(result.code, EXIT.CONFIG);
  assert.match(result.stdout, /\(unset\)/);
});

test('configure --no-verify writes an owner-only config file', async () => {
  const backend = await startFakeBackend();
  try {
    const result = await cli(
      ['configure', '--url', backend.url, '--api-key', VALID_KEY, '--default-page', 'Quick Inbox'],
      { url: '', apiKey: '' }
    );
    assert.equal(result.code, EXIT.OK);
    assert.match(result.stdout, /Saved configuration to/);
    assert.doesNotMatch(result.stdout + result.stderr, new RegExp(VALID_KEY));

    const { readFileSync, statSync } = await import('node:fs');
    const file = join(configDir, 'config.json');
    // Windows has no POSIX mode bits; `configure` uses icacls there instead.
    if (process.platform !== 'win32') assert.equal(statSync(file).mode & 0o777, 0o600);
    const saved = JSON.parse(readFileSync(file, 'utf8'));
    assert.equal(saved.apiKey, VALID_KEY);
    assert.equal(saved.defaultPage, 'Quick Inbox');

    // The saved file alone must be enough to run a command.
    const capture = await cli(['capture', 'From the config file'], { url: '', apiKey: '' });
    assert.equal(capture.code, EXIT.OK);
  } finally {
    await backend.close();
  }
});
