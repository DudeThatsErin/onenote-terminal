// Interactive prompts for `onenotesystem configure`. The API key is read
// without echoing, and is never written back to the terminal.

import readline from 'node:readline';
import { CliError, EXIT } from './exit.js';

// One readline interface for the whole session, deliberately. Creating a second
// interface on process.stdin after closing the first leaves stdin paused on
// Windows, so the next prompt never appears and configure hangs with no output.
export function createPrompter({ input = process.stdin, output = process.stdout } = {}) {
  // Without a terminal there is nobody to answer, and readline would wait
  // forever. Say so instead, and name the flags that avoid prompting.
  if (!input.isTTY) {
    throw new CliError(
      'cannot ask for input because stdin is not a terminal. Pass --url and --api-key instead.',
      EXIT.USAGE
    );
  }

  const rl = readline.createInterface({ input, output, terminal: true });
  let closed = false;

  const close = () => {
    if (closed) return;
    closed = true;
    rl.close();
  };

  // Ctrl+C during a prompt should leave the terminal usable rather than
  // stranding it with echo disabled.
  rl.on('close', () => {
    closed = true;
  });

  return {
    ask(question) {
      return new Promise((resolve, reject) => {
        rl.once('close', onClose);
        rl.question(question, (answer) => {
          rl.removeListener('close', onClose);
          resolve(answer.trim());
        });
        function onClose() {
          reject(new CliError('input was cancelled', EXIT.USAGE));
        }
      });
    },

    askSecret(question) {
      return new Promise((resolve, reject) => {
        const original = rl._writeToOutput;
        let muted = false;

        // readline echoes each keystroke through _writeToOutput. Swallow those
        // writes while the answer is being typed, then restore the original so
        // later prompts still render.
        rl._writeToOutput = function writeToOutput(chunk) {
          if (muted) return;
          if (original) original.call(rl, chunk);
          else rl.output.write(chunk);
        };

        const restore = () => {
          muted = false;
          rl._writeToOutput = original;
        };

        rl.once('close', onClose);
        rl.question(question, (answer) => {
          rl.removeListener('close', onClose);
          restore();
          rl.output.write('\n');
          resolve(answer.trim());
        });

        // rl.question writes the prompt synchronously, so muting immediately
        // after hides the typing without hiding the question itself.
        muted = true;

        function onClose() {
          restore();
          reject(new CliError('input was cancelled', EXIT.USAGE));
        }
      });
    },

    close,
  };
}
