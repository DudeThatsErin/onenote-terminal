#!/usr/bin/env node
import { run } from '../src/cli.js';

run(process.argv.slice(2)).then(
  (code) => process.exit(code),
  (err) => {
    process.stderr.write(`onenotesystem: ${err && err.message ? err.message : err}\n`);
    process.exit(70);
  }
);
