import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export function resolveAndValidateVersion(brokerRoot) {
  const repositoryRoot = join(brokerRoot, '..');
  const metadata = JSON.parse(readFileSync(join(repositoryRoot, 'version.json'), 'utf8'));
  const version = metadata.version;
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) {
    throw new Error(`version.json contains invalid SemVer '${version}'.`);
  }
  if (!/^\d+\.\d+$/.test(metadata.protocolVersion ?? '')) {
    throw new Error(`version.json contains invalid protocolVersion '${metadata.protocolVersion}'.`);
  }
  if (process.env.UNITY_EVAL_TOOL_VERSION && process.env.UNITY_EVAL_TOOL_VERSION !== version) {
    throw new Error(`Requested version ${process.env.UNITY_EVAL_TOOL_VERSION} does not match version.json ${version}.`);
  }

  const unityManifest = JSON.parse(readFileSync(
    join(repositoryRoot, 'Packages/com.yuzetoolkit.yuzeevaltool/package.json'), 'utf8'));
  const npmManifest = JSON.parse(readFileSync(join(brokerRoot, 'npm/root/package.json'), 'utf8'));
  const directoryProps = readFileSync(join(brokerRoot, 'Directory.Build.props'), 'utf8');
  const brokerProtocolSource = readFileSync(join(brokerRoot,
    'src/UnityEvalTool.Broker/BrokerConstants.cs'), 'utf8');
  const unityVersionSource = readFileSync(join(repositoryRoot,
    'Packages/com.yuzetoolkit.yuzeevaltool/Runtime/Core/UnityEvalToolVersion.cs'), 'utf8');
  const unityProtocolSource = readFileSync(join(repositoryRoot,
    'Packages/com.yuzetoolkit.yuzeevaltool/Runtime/Broker/BrokerProtocolUtility.cs'), 'utf8');

  const mismatches = [];
  if (unityManifest.version !== version) mismatches.push(`Unity package=${unityManifest.version}`);
  if (npmManifest.version !== version) mismatches.push(`npm entry=${npmManifest.version}`);
  if (npmManifest.scripts?.postinstall || npmManifest.scripts?.preuninstall) {
    mismatches.push('npm install/uninstall lifecycle scripts must not be used');
  }
  if (npmManifest.scripts?.['service:install'] !== 'node scripts/install-service.js') {
    mismatches.push('npm service:install script');
  }
  if (npmManifest.scripts?.['service:uninstall'] !== 'node scripts/uninstall-service.js') {
    mismatches.push('npm service:uninstall script');
  }
  for (const [name, dependencyVersion] of Object.entries(npmManifest.optionalDependencies ?? {})) {
    if (dependencyVersion !== version) mismatches.push(`${name}=${dependencyVersion}`);
  }
  if (!directoryProps.includes(`<Version>${version}</Version>`)) mismatches.push('Broker Directory.Build.props');
  if (!unityVersionSource.includes(`Current = "${version}"`)) mismatches.push('UnityEvalToolVersion.Current');
  if (!brokerProtocolSource.includes(`ProtocolVersion = "${metadata.protocolVersion}"`)) {
    mismatches.push('BrokerConstants.ProtocolVersion');
  }
  if (!unityProtocolSource.includes(`ProtocolVersion = "${metadata.protocolVersion}"`)) {
    mismatches.push('BrokerProtocolUtility.ProtocolVersion');
  }
  if (mismatches.length) {
    throw new Error(`UnityEvalTool version mismatch: ${mismatches.join(', ')}; expected ${version}.`);
  }
  return version;
}
