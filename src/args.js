// A small flag parser. node:util parseArgs exists, but it cannot express
// "unknown flag" errors that name the command, which is most of what a CLI's
// error quality is made of.

import { CliError, EXIT } from './exit.js';

export function parseArgs(argv, spec, commandName) {
  const flags = {};
  const positionals = [];
  const booleans = new Set(Object.keys(spec).filter((k) => spec[k] === 'boolean'));

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];

    if (arg === '--') {
      positionals.push(...argv.slice(i + 1));
      break;
    }

    if (!arg.startsWith('--')) {
      positionals.push(arg);
      continue;
    }

    const eq = arg.indexOf('=');
    const name = (eq === -1 ? arg.slice(2) : arg.slice(2, eq)).trim();
    if (!(name in spec)) {
      throw new CliError(`unknown option --${name} for \`onenotesystem ${commandName}\``, EXIT.USAGE);
    }

    if (booleans.has(name)) {
      if (eq !== -1) {
        const raw = arg.slice(eq + 1).toLowerCase();
        flags[name] = raw !== 'false' && raw !== '0';
      } else {
        flags[name] = true;
      }
      continue;
    }

    let value;
    if (eq !== -1) {
      value = arg.slice(eq + 1);
    } else {
      value = argv[++i];
      if (value === undefined) throw new CliError(`--${name} needs a value`, EXIT.USAGE);
    }

    flags[name] = value;
  }

  return { flags, positionals };
}

export async function readStdin() {
  if (process.stdin.isTTY) {
    throw new CliError('--stdin was passed but nothing is piped in', EXIT.USAGE);
  }
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}
