'use strict';

const { spawnSync } = require('node:child_process');
const { resolveNativeExecutable } = require('./native-package');

try {
  const result = spawnSync(resolveNativeExecutable(), ['service', 'uninstall'], {
    stdio: 'inherit',
    windowsHide: true
  });
  if (result.error) {
    throw result.error;
  }
  if (result.signal || result.status !== 0) {
    throw new Error(result.signal
      ? `service uninstall terminated by ${result.signal}`
      : `service uninstall exited with status ${result.status}`);
  }
} catch (error) {
  console.error(`Yuze Eval Tool service removal failed: ${error.message}`);
  process.exitCode = 1;
}
