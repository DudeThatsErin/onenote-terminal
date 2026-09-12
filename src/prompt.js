// Interactive prompts for `onenotesystem configure`. The API key is read
// without echoing, and is never written back to the terminal.

import readline from 'node:readline';

export function ask(question, { secret = false } = {}) {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: true });

  return new Promise((resolve) => {
    if (!secret) {
      rl.question(question, (answer) => {
        rl.close();
        resolve(answer.trim());
      });
      return;
    }

    // Suppress echo: write the prompt once, then swallow everything readline
    // would otherwise mirror back.
    process.stdout.write(question);
    let muted = true;
    const originalWrite = rl._writeToOutput?.bind(rl);
    rl._writeToOutput = (chunk) => {
      if (!muted && originalWrite) originalWrite(chunk);
    };
    rl.question('', (answer) => {
      muted = false;
      rl.close();
      process.stdout.write('\n');
      resolve(answer.trim());
    });
  });
}
