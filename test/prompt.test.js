// The prompter is driven through fake TTY streams, one keystroke at a time,
// because the two bugs it has had were both about what reaches the terminal:
//   1. Two questions in a row hung on Windows (a new readline interface per
//      question leaves stdin paused).
//   2. Hiding the API key erased the prompt on the first keystroke, because
//      readline's line refresh clears the line outside `_writeToOutput`.
// Asserting on exactly what was written is what catches those.

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
  input.setRawMode = function setRawMode(value) {
    this.isRaw = value;
    return this;
  };

  let written = '';
  output.on('data', (chunk) => {
    written += chunk.toString('utf8');
  });

  // Type like a terminal does: one keystroke per data event.
  const type = (text) => {
    for (const char of text) input.write(char);
  };

  return { input, output, type, seen: () => written };
}

test('asks two questions in sequence, both prompts visible', async () => {
  const { input, output, type, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const first = prompter.ask('URL: ');
  type('https://onenote.example.com\r');
  assert.equal(await first, 'https://onenote.example.com');

  // The Windows hang: the second prompt must appear and resolve.
  const second = prompter.ask('Second: ');
  type('answer two\r');
  assert.equal(await second, 'answer two');

  assert.match(seen(), /URL: /);
  assert.match(seen(), /Second: /);
  prompter.close();
});

test('a hidden prompt stays on screen while the answer does not', async () => {
  const { input, output, type, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const key = prompter.askSecret('API key (input hidden): ');
  type('ons_super_secret\r');
  assert.equal(await key, 'ons_super_secret');

  // The regression: typing must not erase the question.
  assert.match(seen(), /API key \(input hidden\): /);
  assert.doesNotMatch(seen(), /ons_super_secret/);
  // Nothing should clear the line or move the cursor.
  assert.doesNotMatch(seen(), /\x1b\[/);
  prompter.close();
});

test('a visible prompt echoes what is typed', async () => {
  const { input, output, type, seen } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('https://a.example\r');
  await answer;
  assert.match(seen(), /URL: https:\/\/a\.example/);
  prompter.close();
});

test('backspace edits both the value and the echo', async () => {
  const { input, output, type, seen } = fakeTty();
  const prompter = createPrompter({ input, output });

  const visible = prompter.ask('URL: ');
  type('abcX\x7f\r');
  assert.equal(await visible, 'abc');
  assert.match(seen(), /\x08 \x08/, "backspace must erase the echoed character");

  const hidden = prompter.askSecret('Key: ');
  type('secretX\x7f\r');
  assert.equal(await hidden, 'secret');
  prompter.close();
});

test('backspace on an empty answer does nothing', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('\x7f\x7f\x7fok\r');
  assert.equal(await answer, 'ok');
  prompter.close();
});

test('ctrl+U clears the line', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('wrong\x15right\r');
  assert.equal(await answer, 'right');
  prompter.close();
});

test('arrow keys are swallowed instead of pasted as letters', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('ab\x1b[Ac\x1b[Dd\r'); // up arrow, left arrow
  assert.equal(await answer, 'abcd');
  prompter.close();
});

test('accepts \\r\\n without producing a second empty answer', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const first = prompter.ask('URL: ');
  type('value\r\n');
  assert.equal(await first, 'value');

  const second = prompter.ask('Next: ');
  type('second\r');
  assert.equal(await second, 'second');
  prompter.close();
});

test('non-ASCII input survives', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('Name: ');
  type('Café-Über\r');
  assert.equal(await answer, 'Café-Über');
  prompter.close();
});

test('answers are trimmed', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('   https://a.example   \r');
  assert.equal(await answer, 'https://a.example');
  prompter.close();
});

test('an empty answer resolves empty rather than hanging', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.askSecret('Key: ');
  type('\r');
  assert.equal(await answer, '');
  prompter.close();
});

test('ctrl+C cancels', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('\x03');
  await assert.rejects(answer, (err) => err.code === EXIT.USAGE && /cancelled/.test(err.message));
  prompter.close();
});

test('raw mode is turned off again after each answer', async () => {
  const { input, output, type } = fakeTty();
  const prompter = createPrompter({ input, output });
  const answer = prompter.ask('URL: ');
  type('value\r');
  await answer;
  assert.equal(input.isRaw, false, 'the terminal must not be left in raw mode');
  prompter.close();
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
