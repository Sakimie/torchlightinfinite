'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const opts = {};
for (let i=2;i<process.argv.length;i+=2) {
  const option=process.argv[i], value=process.argv[i+1];
  if (!['--manifest','--key','--public','--out'].includes(option)||!value||opts[option]) throw new Error('Expected --manifest FILE --key PRIVATE_PEM --public PUBLIC_PEM --out FILE');
  opts[option]=value;
}
if(Object.keys(opts).length!==4)throw new Error('All four options are required');
const keyPath=fs.realpathSync(opts['--key']);
const repository=path.resolve(__dirname,'../..');
if(keyPath===repository||keyPath.startsWith(repository+path.sep))throw new Error('Signing key must be outside repository');
if(process.platform!=='win32'&&(fs.statSync(keyPath).mode&0o077))throw new Error('Private key must exclude group/other access');
const key=crypto.createPrivateKey(fs.readFileSync(keyPath));
if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails.namedCurve!=='prime256v1')throw new Error('Expected P-256 release key');
const publicKey=crypto.createPublicKey(key);
if(!publicKey.export({type:'spki',format:'der'}).equals(crypto.createPublicKey(fs.readFileSync(opts['--public'])).export({type:'spki',format:'der'})))throw new Error('Pinned public key mismatch');
const raw=fs.readFileSync(opts['--manifest']);
const manifest=JSON.parse(raw.toString('utf8'));
if(manifest.schema!=='talos-csharp-release-v1'||manifest.app!=='TalosHelper'||!/^\d+\.\d+\.\d+$/.test(manifest.version))throw new Error('Invalid C# manifest');
const signature=crypto.sign('sha256',raw,{key,dsaEncoding:'ieee-p1363'});
if(signature.length!==64||!crypto.verify('sha256',raw,{key:publicKey,dsaEncoding:'ieee-p1363'},signature))throw new Error('Signature self-verification failed');
fs.writeFileSync(opts['--out'],JSON.stringify({schema:'talos-signed-csharp-feed-v1',algorithm:'ES256',payload:raw.toString('base64'),signature:signature.toString('base64')},null,2)+'\n');
process.stdout.write(JSON.stringify({bytes:fs.statSync(opts['--out']).size,signatureAlgorithm:'ES256'})+'\n');
