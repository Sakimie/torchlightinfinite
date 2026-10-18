'use strict';

// Developer release tool. The private key must stay outside both source and ZIPs.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function signRelease(manifestBytes, privatePem, expectedPublicPem) {
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  if (manifest.schema !== 'talos-release-v1' || manifest.app !== 'TalosHelper' ||
      !/^\d+\.\d+\.\d+$/.test(manifest.version)) {
    throw new Error('Invalid release manifest');
  }
  const privateKey = crypto.createPrivateKey(privatePem);
  if (privateKey.asymmetricKeyType !== 'ed25519') throw new Error('Expected Ed25519 release key');
  const publicKey = crypto.createPublicKey(privateKey);
  const actual = publicKey.export({format: 'der', type: 'spki'});
  const expected = crypto.createPublicKey(expectedPublicPem).export({format: 'der', type: 'spki'});
  if (!actual.equals(expected)) throw new Error('Release key does not match pinned public key');
  const signature = crypto.sign(null, manifestBytes, privateKey);
  if (!crypto.verify(null, manifestBytes, publicKey, signature)) throw new Error('Signature self-check failed');
  return Buffer.from(JSON.stringify({
    schema: 'talos-signed-feed-v1',
    payload: manifestBytes.toString('base64'),
    signature: signature.toString('base64'),
  }, null, 2) + '\n', 'utf8');
}

function main(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i += 2) {
    if (!['--manifest', '--key', '--public', '--out'].includes(argv[i]) || !argv[i + 1]) {
      throw new Error('Usage: node sign-release.cjs --manifest FILE --key PRIVATE_PEM --public PUBLIC_PEM --out FILE');
    }
    if (opts[argv[i]]) throw new Error('Repeated option');
    opts[argv[i]] = argv[i + 1];
  }
  if (Object.keys(opts).length !== 4) throw new Error('All four options are required');
  const keyPath = fs.realpathSync(opts['--key']);
  const repoRoot = path.resolve(__dirname, '..');
  if (keyPath === repoRoot || keyPath.startsWith(repoRoot + path.sep)) {
    throw new Error('Private key must be outside the source repository');
  }
  if (process.platform !== 'win32' && (fs.statSync(keyPath).mode & 0o077)) {
    throw new Error('Private key permissions must exclude group and others');
  }
  const bytes = fs.readFileSync(opts['--manifest']);
  const envelope = signRelease(bytes, fs.readFileSync(keyPath), fs.readFileSync(opts['--public']));
  fs.writeFileSync(opts['--out'], envelope);
  process.stdout.write(JSON.stringify({
    output: path.resolve(opts['--out']),
    bytes: envelope.length,
    sha256: crypto.createHash('sha256').update(envelope).digest('hex'),
  }) + '\n');
}

module.exports = {signRelease};
if (require.main === module) {
  try { main(process.argv.slice(2)); }
  catch (error) { process.stderr.write(error.message + '\n'); process.exitCode = 1; }
}
