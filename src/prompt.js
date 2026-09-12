// Interactive prompts for `onenotesystem configure`. The API key is read
// without echoing, and is never written back to the terminal.
//
// This reads keystrokes directly rather than using node:readline. readline
// cannot hide input: masking means overriding `_writeToOutput`, but its line
// refresh calls cursorTo/clearScreenDown straight on the output stream and only
// then writes the text through `_writeToOutput`. Muting therefore erases the
// prompt on the first keystroke and writes nothing back, leaving a blank line
// and a cursor. Reading the bytes ourselves also avoids readline's interface
// lifecycle, which is what made two prompts in a row hang on Windows.

import { CliError, EXIT } from './exit.js';

// Written as escapes rather than literals so the file stays copy-paste safe
// and a stripped control byte cannot silently disable a key.
const ENTER = new Set(['\r', '\n']);
const BACKSPACE = new Set(['\x7f', '\b']);
const CTRL_C = '\x03';
const CTRL_D = '\x04';
const CTRL_U = '\x15';
const ESCAPE = '\x1b';

export function createPrompter({ input = process.stdin, output = process.stdout } = {}) {
  // Without a terminal there is nobody to answer, and a raw read would wait
  // forever. Say so instead, and name the flags that avoid prompting.
  if (!input.isTTY) {
    throw new CliError(
      'cannot ask for input because stdin is not a terminal. Pass --url and --api-key instead.',
      EXIT.USAGE
    );
  }

  let closed = false;
  // Windows sends CRLF for Enter. The CR ends one answer and the LF is still
  // queued, so without this the next prompt is answered instantly and empty.
  let swallowNewline = false;

  function read(question, { echo }) {
    return new Promise((resolve, reject) => {
      if (closed) {
        reject(new CliError('input was cancelled', EXIT.USAGE));
        return;
      }

      output.write(question);

      const wasRaw = input.isRaw === true;
      input.setRawMode?.(true);
      input.setEncoding('utf8');
      input.resume();

      let value = '';
      // Arrow keys and similar arrive as escape sequences; swallow the whole
      // sequence rather than pasting its letters into the answer.
      let skipEscape = false;

      const finish = (fn, arg) => {
        input.off('data', onData);
        input.setRawMode?.(wasRaw);
        input.pause();
        output.write('\n');
        fn(arg);
      };

      function onData(chunk) {
        for (const char of String(chunk)) {
          if (swallowNewline) {
            swallowNewline = false;
            if (char === '\n') continue;
          }
          if (skipEscape) {
            // A CSI/SS3 sequence ends at its first letter or tilde.
            if (/[A-Za-z~]/.test(char)) skipEscape = false;
            continue;
          }
          if (char === ESCAPE) {
            skipEscape = true;
            continue;
          }
          if (ENTER.has(char)) {
            swallowNewline = char === '\r';
            finish(resolve, value.trim());
            return;
          }
          if (char === CTRL_C) {
            finish(reject, new CliError('input was cancelled', EXIT.USAGE));
            return;
          }
          if (char === CTRL_D) {
            // End-of-input on an empty answer is a cancel, not an empty string.
            if (!value) {
              finish(reject, new CliError('input was cancelled', EXIT.USAGE));
              return;
            }
            finish(resolve, value.trim());
            return;
          }
          if (BACKSPACE.has(char)) {
            if (value.length) {
              value = value.slice(0, -1);
              if (echo) output.write('\b \b');
            }
            continue;
          }
          if (char === CTRL_U) {
            if (echo && value.length) output.write('\b \b'.repeat(value.length));
            value = '';
            continue;
          }
          // Ignore the remaining control characters; keep everything printable,
          // which includes non-ASCII text.
          if (char < ' ') continue;
          value += char;
          if (echo) output.write(char);
        }
      }

      input.on('data', onData);
    });
  }

  return {
    ask: (question) => read(question, { echo: true }),
    askSecret: (question) => read(question, { echo: false }),
    close() {
      closed = true;
      input.setRawMode?.(false);
      input.pause();
    },
  };
}
