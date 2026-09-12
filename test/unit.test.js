import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseArgs } from '../src/args.js';
import { normalizeUrl } from '../src/config.js';
import { EXIT } from '../src/exit.js';

test('parseArgs reads values, booleans, and positionals', () => {
  const { flags, positionals } = parseArgs(
    ['Standup', 'notes', '--content=Shipped it', '--stdin', '--url', 'https://example.com'],
    { content: 'string', url: 'string', stdin: 'boolean' },
    'capture'
  );
  assert.deepEqual(positionals, ['Standup', 'notes']);
  assert.equal(flags.content, 'Shipped it');
  assert.equal(flags.url, 'https://example.com');
  assert.equal(flags.stdin, true);
});

test('parseArgs names the command in unknown-option errors', () => {
  assert.throws(
    () => parseArgs(['--nope'], { content: 'string' }, 'capture'),
    (err) => err.code === EXIT.USAGE && /--nope/.test(err.message) && /capture/.test(err.message)
  );
});

test('parseArgs rejects a value flag with no value', () => {
  assert.throws(() => parseArgs(['--content'], { content: 'string' }, 'capture'), { code: EXIT.USAGE });
});

test('parseArgs stops flag parsing at --', () => {
  const { positionals } = parseArgs(['--', '--content'], { content: 'string' }, 'append');
  assert.deepEqual(positionals, ['--content']);
});

test('normalizeUrl adds https, trims slashes, and drops a pasted /api suffix', () => {
  assert.equal(normalizeUrl('onenotesystem.example.com'), 'https://onenotesystem.example.com');
  assert.equal(normalizeUrl('https://onenotesystem.example.com///'), 'https://onenotesystem.example.com');
  assert.equal(normalizeUrl('https://onenotesystem.example.com/api'), 'https://onenotesystem.example.com');
});

test('normalizeUrl allows http only for localhost', () => {
  assert.equal(normalizeUrl('http://localhost:3000'), 'http://localhost:3000');
  assert.equal(normalizeUrl('http://127.0.0.1:3000'), 'http://127.0.0.1:3000');
  assert.throws(() => normalizeUrl('http://onenotesystem.example.com'), { code: EXIT.USAGE });
});

test('normalizeUrl rejects junk', () => {
  assert.throws(() => normalizeUrl(''), { code: EXIT.USAGE });
  assert.throws(() => normalizeUrl('https://'), { code: EXIT.USAGE });
});

test('exit codes are distinct and stable', () => {
  const values = Object.values(EXIT);
  assert.equal(new Set(values).size, values.length);
  assert.equal(EXIT.OK, 0);
  assert.equal(EXIT.AUTH, 2);
  assert.equal(EXIT.CONFIG, 6);
});
