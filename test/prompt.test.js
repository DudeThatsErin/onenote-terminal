// The prompter is driven through fake TTY streams. The bug these cover is that
// `configure` asks two questions in a row: the original code built a new
// readline interface per question, which leaves stdin paused on Windows so the
// second prompt never appears and the command hangs with no output.

import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { test } from 'node:test';
import { createPrompter } from '../src/prompt.js';
import { EXIT } from '../src/exit.js';

function fakeTty() {
  const input = new PassThrough();
  const output = new PassThrough();
  input.isTTY = true;
  output.isTTY = true;

  let written = '';
  output.on('data', (chunk) => {
    written += chunk.toString('utf8');
  });

  return { input, output, seen: () => written };
}

test('asks two questions in sequence on one interface', async () => {
  const { input, output, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const first = prompter.ask('URL: ');
  input.write('https://onenote.example.com\n');
  assert.equal(await first, 'https://onenote.example.com');

  // The regression: the second prompt must appear and resolve.
  const second = prompter.ask('Second: ');
  input.write('answer two\n');
  assert.equal(await second, 'answer two');

  assert.match(seen(), /URL: /);
  assert.match(seen(), /Second: /);
  prompter.close();
});

test('a secret question follows a plain one and hides what is typed', async () => {
  const { input, output, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const url = prompter.ask('URL: ');
  input.write('https://onenote.example.com\n');
  await url;

  const key = prompter.askSecret('API key (input hidden): ');
  input.write('ons_super_secret_value\n');
  assert.equal(await key, 'ons_super_secret_value');

  // The question is visible; the answer never reaches the terminal.
  assert.match(seen(), /API key \(input hidden\): /);
  assert.doesNotMatch(seen(), /ons_super_secret_value/);
  prompter.close();
});

test('echo is restored after a secret, so later prompts still render', async () => {
  const { input, output, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const key = prompter.askSecret('Key: ');
  input.write('hidden\n');
  await key;

  const after = prompter.ask('Visible again: ');
  input.write('yes\n');
  assert.equal(await after, 'yes');
  assert.match(seen(), /Visible again: /);
  prompter.close();
});

test('answers are trimmed', async () => {
  const { input, output } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  input.write('   https://onenote.example.com   \n');
  assert.equal(await answer, 'https://onenote.example.com');
  prompter.close();
});

test('closing mid-prompt rejects instead of hanging forever', async () => {
  const { input, output } = fakeTty();
  const prompter = createPrompter({ input, output });
  const pending = prompter.ask('URL: ');
  prompter.close();
  await assert.rejects(pending, (err) => err.code === EXIT.USAGE && /cancelled/.test(err.message));
});

test('refuses to prompt when stdin is not a terminal', () => {
  const input = new PassThrough();
  const output = new PassThrough();
  input.isTTY = false;
  assert.throws(
    () => createPrompter({ input, output }),
    (err) => err.code === EXIT.USAGE && /not a terminal/.test(err.message) && /--api-key/.test(err.message)
  );
});
