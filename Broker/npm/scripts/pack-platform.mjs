import { execFileSync } from 'node:child_process';
import { chmodSync, cpSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { arch, platform } from 'node:process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveAndValidateVersion } from './version.mjs';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const root = resolve(scriptDirectory, '../..');
const version = resolveAndValidateVersion(root);
const platformName = process.env.UNITY_EVAL_TOOL_PLATFORM ?? platform;
const architecture = process.env.UNITY_EVAL_TOOL_ARCH ?? arch;
const key = `${platformName}-${architecture}`;
const targets = {
  'darwin-arm64': { rid: 'osx-arm64', os: 'darwin', cpu: 'arm64' },
  'darwin-x64': { rid: 'osx-x64', os: 'darwin', cpu: 'x64' },
  'linux-arm64': { rid: 'linux-arm64', os: 'linux', cpu: 'arm64' },
  'linux-x64': { rid: 'linux-x64', os: 'linux', cpu: 'x64' },
  'win32-arm64': { rid: 'win-arm64', os: 'win32', cpu: 'arm64' },
  'win32-x64': { rid: 'win-x64', os: 'win32', cpu: 'x64' }
};
const target = targets[key];
if (!target) throw new Error(`Unsupported package target ${key}.`);

const project = join(root, 'src/UnityEvalTool.Broker/UnityEvalTool.Broker.csproj');
const publish = join(root, 'artifacts/publish', target.rid);
const stage = join(root, 'artifacts/npm', key);
rmSync(publish, { recursive: true, force: true });
rmSync(stage, { recursive: true, force: true });
mkdirSync(join(stage, 'bin'), { recursive: true });

execFileSync('dotnet', [
  'publish', project, '-c', 'Release', '-r', target.rid,
  '--self-contained', 'true', '-o', publish
], { stdio: 'inherit' });

const executableName = target.os === 'win32' ? 'unity.exe' : 'unity';
cpSync(join(publish, executableName), join(stage, 'bin', executableName));
if (target.os !== 'win32') chmodSync(join(stage, 'bin', executableName), 0o755);
cpSync(join(root, '../LICENSE'), join(stage, 'LICENSE'));

writeFileSync(join(stage, 'package.json'), JSON.stringify({
  name: `@yuzetoolkit/unityevaltool-${key}`,
  version,
  description: `UnityEvalTool native Broker and CLI for ${key}`,
  author: {
    name: 'Yuze075',
    email: '925581968@qq.com',
    url: 'https://github.com/Yuze075'
  },
  license: 'MIT',
  homepage: 'https://github.com/Yuze075/YuzeEvalTool#readme',
  bugs: { url: 'https://github.com/Yuze075/YuzeEvalTool/issues' },
  repository: {
    type: 'git',
    url: 'git+https://github.com/Yuze075/YuzeEvalTool.git',
    directory: 'Broker'
  },
  os: [target.os],
  cpu: [target.cpu],
  files: ['bin', 'LICENSE'],
  publishConfig: { access: 'public' }
}, null, 2) + '\n');

const npmExecutable = platform === 'win32' ? 'npm.cmd' : 'npm';
execFileSync(npmExecutable, ['pack', stage, '--pack-destination', join(root, 'artifacts/npm')], {
  stdio: 'inherit',
  shell: platform === 'win32'
});
