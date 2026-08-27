#!/usr/bin/env node

'use strict';

const { spawnSync } = require('node:child_process');
const { resolveNativeExecutable } = require('../scripts/native-package');

let executable;
try {
  executable = resolveNativeExecutable();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}

const result = spawnSync(executable, process.argv.slice(2), {
  stdio: 'inherit',
  windowsHide: false
});

if (result.error) {
  console.error(`Unable to launch Yuze Eval Tool native executable: ${result.error.message}`);
  process.exit(1);
}

process.exit(result.status === null ? 1 : result.status);
