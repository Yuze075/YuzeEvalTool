'use strict';

const path = require('node:path');

const supported = new Map([
  ['darwin-arm64', '@yuzetoolkit/unityevaltool-darwin-arm64'],
  ['darwin-x64', '@yuzetoolkit/unityevaltool-darwin-x64'],
  ['linux-arm64', '@yuzetoolkit/unityevaltool-linux-arm64'],
  ['linux-x64', '@yuzetoolkit/unityevaltool-linux-x64'],
  ['win32-arm64', '@yuzetoolkit/unityevaltool-win32-arm64'],
  ['win32-x64', '@yuzetoolkit/unityevaltool-win32-x64']
]);

function resolveNativeExecutable() {
  const key = `${process.platform}-${process.arch}`;
  const packageName = supported.get(key);
  if (!packageName) {
    throw new Error(`Yuze Eval Tool does not publish a native binary for ${key}.`);
  }

  let manifest;
  try {
    manifest = require.resolve(`${packageName}/package.json`);
  } catch {
    throw new Error(
      `The optional native package ${packageName} is missing. ` +
      'Reinstall without --no-optional and confirm this platform is supported.'
    );
  }

  return path.join(path.dirname(manifest), 'bin', process.platform === 'win32' ? 'unity.exe' : 'unity');
}

module.exports = { resolveNativeExecutable };
