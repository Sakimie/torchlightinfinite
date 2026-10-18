#!/usr/bin/env python3
"""Publish an already signed/reviewed C# component release; --check is entirely offline.

Never rebuilds or signs reviewed packages, reads credentials/private keys,
changes proxy settings, force-pushes, or uses the release upload endpoint.
"""
from __future__ import annotations
import argparse
import base64
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
CS = ROOT / 'csharp'
REPO = 'Sakimie/torchlightinfinite'
BRANCH = 'talos-updates'
RAW = 'https://raw.githubusercontent.com/' + REPO + '/'
FEED = RAW + BRANCH + '/csharp/latest.json'
OLD_SHA = '7e11930bb99b09421f3b9d6e958ab42a9dc35ed97ceaf700a31be96784564406'
PUBLIC_SHA = 'f753f951eb75a0077a0a00cd28c3f4c7abf1aa827a063bd8b3a7df61dad1dd72'
REPORTS = ('build-report.json', 'payload-report.json', 'test-results.json',
           'integration-review.json', 'capture-review.json', 'offline-update-validation.json',
           'embedded-check/bundle-inspection.json', 'offline-package-report.json')


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def load(path):
    return json.loads(Path(path).read_text('utf8'))


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def relative(name):
    require(isinstance(name, str) and name and '\\' not in name and ':' not in name,
            'Invalid publication relative path')
    p = PurePosixPath(name)
    require(not p.is_absolute() and all(x not in ('', '.', '..') for x in name.split('/')),
            'Unsafe publication relative path')
    return name


def plain(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink(), 'Missing or symlink input: ' + path.name)
    for parent in path.parents:
        require(not parent.is_symlink(), 'Symlink input ancestor')
    return path


def identity(path):
    plain(path)
    return {'bytes': path.stat().st_size, 'sha256': digest(path)}


def verify_item(path, expected):
    actual = identity(path)
    require(actual == {k: expected[k] for k in ('bytes', 'sha256')},
            'Reviewed bytes changed: ' + path.name)
    return actual


def run(args, cwd=None, env=None):
    # Do not echo command arguments or child diagnostics: Git's configured remote
    # may contain platform authentication, which this publisher never inspects.
    result = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True)
    require(result.returncode == 0, 'Command failed: ' + Path(args[0]).name +
            ' (exit ' + str(result.returncode) + '; no credentials or diagnostics printed)')
    return result.stdout.strip()


def signature(feed, public):
    require(digest(public) == PUBLIC_SHA, 'Pinned product public key changed')
    code = """const f=require('fs'),c=require('crypto');
const e=JSON.parse(f.readFileSync(process.argv[1],'utf8'));
if(e.schema!=='talos-signed-csharp-feed-v1'||e.algorithm!=='ES256')throw Error('schema');
const p=Buffer.from(e.payload,'base64'),s=Buffer.from(e.signature,'base64');
if(s.length!==64||!c.verify('sha256',p,{key:f.readFileSync(process.argv[2]),dsaEncoding:'ieee-p1363'},s))throw Error('signature');
process.stdout.write('verified');"""
    require(run(['node', '-e', code, str(feed), str(public)]) == 'verified', 'Signed feed verification failed')
    e = load(feed)
    return base64.b64decode(e['payload'], validate=True)


def zip_members(path):
    with zipfile.ZipFile(path) as archive:
        require(archive.testzip() is None, 'ZIP CRC failure: ' + path.name)
        entries = archive.infolist()
        names = [relative(item.filename) for item in entries]
        require(len(names) == len(set(name.casefold() for name in names)), 'ZIP duplicate/alias members')
        require(all(not item.is_dir() and ((item.external_attr >> 16) & 0o170000) in (0, 0o100000)
                    for item in entries), 'ZIP has special members')
        require(not any(re.search(r'(^|/)(\.git|\.cache|_internal|runtime|english-source)(/|$)|\.(pem|key|pfx|p12)$',
                                  name, re.I) for name in names), 'Unexpected runtime/source/key file in ZIP')
        return {item.filename: {'bytes': item.file_size,
                'sha256': hashlib.sha256(archive.read(item)).hexdigest()} for item in entries}


def checked(version):
    require(re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version), 'Invalid version')
    out = ROOT / 'dist' / ('csharp-' + version)
    delivery, build = load(out/'delivery-report.json'), load(out/'build-report.json')
    require(delivery.get('passed') and delivery['version'] == build['version'] == version, 'Delivery/build not approved')
    require(delivery['launcherMinimum'] == '1.1.0', 'Expected portable-host compatibility requirement')
    require(delivery['sourceHashes'] == build['sourceHashes'], 'Delivery/build source identities differ')
    for name, expected in build['sourceHashes'].items():
        require(digest(plain(CS/relative(name))) == expected, 'Compiled source changed: ' + name)
    for name, expected in delivery['reports'].items():
        require(digest(plain(out/relative(name))) == expected, 'Reviewed report changed: ' + name)
    artifacts = {}
    for item in delivery['artifacts']:
        name = relative(item['file']); artifacts[name] = verify_item(out/name, item)
    require(len(artifacts) == 3, 'Expected complete, offline and inner ZIPs')
    expected_names = {f'TalosHelper-CSharp-{version}-{suffix}.zip' for suffix in ('Windows-x64', 'Offline-Update', 'update')}
    require(set(artifacts) == expected_names, 'Unexpected delivery artifacts')
    for kind in ('complete', 'payload'):
        require(artifacts[build[kind]['file']] == {k: build[kind][k] for k in ('bytes', 'sha256')}, 'Build artifact mismatch')
    feed = out/'latest.json'
    require(digest(feed) == delivery['feedSha256'] == build['feedSha256'], 'Frozen signed feed changed')
    raw_manifest = signature(feed, CS/'update-public-key.pem')
    require(raw_manifest == (out/'manifest.json').read_bytes(), 'Signed manifest bytes differ from reviewed manifest')
    manifest = json.loads(raw_manifest)
    require(manifest['version'] == version and manifest['schema'] == 'talos-csharp-release-v1' and
            manifest['app'] == 'TalosHelper' and manifest['launcherMin'] == '1.1.0', 'Signed release identity mismatch')
    payload_name = build['payload']['file']
    prefix = 'csharp/releases/' + version + '/'
    require(manifest['package']['url'] == RAW+BRANCH+'/'+prefix+payload_name, 'Signed package URL differs; never re-sign to publish')
    require({k: manifest['package'][k] for k in ('bytes', 'sha256')} == artifacts[payload_name], 'Signed package identity mismatch')
    payload_members = zip_members(out/payload_name)
    declared = {relative(item['path']): {k:item[k] for k in ('bytes','sha256')} for item in manifest['files']}
    require(payload_members == declared, 'Payload ZIP differs from signed file list')
    for name, expected in declared.items(): verify_item(out/'payload'/name, expected)
    offline = load(out/'offline-package-report.json')
    require(offline['version'] == version and artifacts[offline['file']]['sha256'] == offline['sha256'], 'Offline package identity mismatch')
    with zipfile.ZipFile(out/offline['file']) as archive:
        require(archive.read('latest.json') == feed.read_bytes() and archive.read(payload_name) == (out/payload_name).read_bytes(), 'Offline outer archive embeds different bytes')
    zip_members(out/offline['file'])
    complete_members = zip_members(out/build['complete']['file'])
    local_complete = {p.relative_to(out/'complete-package').as_posix(): identity(p)
                      for p in (out/'complete-package').rglob('*') if p.is_file()}
    require(complete_members == local_complete, 'Complete ZIP differs from approved complete-package folder')
    bundle = load(out/'embedded-check/bundle-inspection.json')
    require(bundle['passed'] and bundle['noBundleExtractionRequired'] and len(bundle['externalNative']) == 5, 'Real host bundle check missing')
    verify_item(out/'complete-package/TalosHelper.exe', {'bytes': bundle['executableBytes'], 'sha256': bundle['executableSha256']})
    require(bundle['resources']['Talos.BootstrapFeed'] == digest(feed) and
            bundle['resources']['Talos.BootstrapPackage'] == artifacts[payload_name]['sha256'] and
            bundle['resources']['Talos.PublicKey'] == PUBLIC_SHA, 'Actual host embeds different bootstrap/key')
    require((out/'embedded-check/latest.json').read_bytes() == feed.read_bytes() and
            (out/'embedded-check/payload.zip').read_bytes() == (out/payload_name).read_bytes(), 'Extracted actual host resources differ')
    for name, sha in bundle['externalNative'].items():
        require(digest(plain(out/'complete-package'/relative(name))) == sha, 'Native WPF dependency changed')
    tests = load(out/'test-results.json')
    require(len(tests) >= 109 and all(test.get('Passed') is True for test in tests), 'At least 109 passing real signed-payload tests required')
    for name in ('integration-review.json', 'offline-update-validation.json'):
        report = load(out/name); require(report.get('passed') is True, 'Failed review: '+name)
        for source, sha in report.get('sourceHashes', {}).items():
            require(digest(plain(CS/relative(source))) == sha, 'Review source changed: '+source)
    capture = load(out/'capture-review.json')
    require(capture['schema'] == 'talos-capture-review-v1' and capture['version'] == version and
            capture['standaloneBuild']['exitCode'] == 0 and capture['usefulPixelsChecks']['exitCode'] == 0 and
            capture['usefulPixelsChecks']['passed'] >= 6 and capture['usefulPixelsChecks']['failed'] == 0 and
            capture['staticCaptureFlowReview']['status'] == 'passed-static-review', 'Capture review failed')
    capture_source = relative(capture['source']['path'])
    require(capture_source.startswith('csharp/') and digest(plain(ROOT/capture_source)) == capture['source']['sha256'],
            'Capture reviewed source changed')
    offline_check = load(out/'offline-update-validation.json')
    require(offline_check['baselineVersion'] == '0.4.2' and offline_check['importedVersion'] == version and
            offline_check['networkRequests'] == 0 and offline_check['userCalibrationPreserved'], 'Actual offline compatibility check mismatch')
    baseline = ROOT/'dist/csharp-0.4.2'
    baseline_manifest = json.loads(signature(plain(baseline/'latest.json'), CS/'update-public-key.pem'))
    require(baseline_manifest['version'] == '0.4.2', 'Real original 0.4.2 baseline required')
    verify_item(baseline/'TalosHelper-CSharp-0.4.2-update.zip', baseline_manifest['package'])
    sums = {line.split('  ',1)[1]:line.split('  ',1)[0] for line in (out/'SHA256SUMS.txt').read_text().splitlines()}
    require(sums == {**{name:row['sha256'] for name,row in artifacts.items()}, 'latest.json':digest(feed)}, 'SHA256SUMS differs from frozen assets')
    files = {prefix+name: out/name for name in [*artifacts, 'latest.json', 'SHA256SUMS.txt', 'delivery-report.json', *REPORTS]}
    files[prefix+'release-notes.md'] = CS/f'RELEASE-NOTES-{version}.md'
    for source in files.values():
        plain(source)
        if source.suffix in ('.json', '.md', '.txt'):
            require(b'PRIVATE KEY-----' not in source.read_bytes(), 'Sensitive key material in public text')
    planned = {name:identity(path) for name,path in files.items()}
    plan_id = hashlib.sha256(json.dumps(planned, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    plan = {'schema':'talos-component-publication-plan-v1','version':version,'passed':True,'offlineCheck':True,
            'networkUsed':False,'remoteChangesMade':False,'signedFeedSha256':digest(feed),'planId':plan_id,
            'testCount':len(tests),'repository':REPO,'branch':BRANCH,'tag':'v'+version+'-csharp-preview',
            'prerelease':True,'makeLatest':False,'files':planned,'channelPath':'csharp/latest.json',
            'steps':['Append immutable version files with normal authenticated Git; preserve branch parents.',
                     'Verify every version file through public immutable HTTPS URLs and the exact signed package URL.',
                     'Atomically advance only csharp/latest.json through a non-force Git push.',
                     'Run native HTTPS VerifyChannel from real signed 0.4.2, preserve update on old bootstrap, then offline rollback.',
                     'Publish validation evidence and create prerelease make_latest=false; verify public bytes and unchanged Python stable channel.'],
            'unchangedPythonStableSha256':OLD_SHA,'requiresResigning':False,'privateKeyRead':False}
    return out, files, plan


def atomic_json(path, value):
    temp = path.with_suffix(path.suffix+'.tmp')
    with temp.open('w', encoding='utf8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2); stream.write('\n'); stream.flush()
        os.fsync(stream.fileno())
    temp.replace(path)


def api(endpoint, method='GET', value=None, missing_ok=False):
    args = ['gh','api',endpoint,'--method',method,'-H','X-GitHub-Api-Version: 2022-11-28']
    with tempfile.TemporaryDirectory(prefix='talos-component-api-', dir='/workspace/scratch') as folder:
        if value is not None:
            request=Path(folder)/'request.json'; request.write_text(json.dumps(value,ensure_ascii=False)); args += ['--input',str(request)]
        result=subprocess.run(args,capture_output=True,text=True)
        if missing_ok and result.returncode and 'HTTP 404' in result.stderr: return None
        require(result.returncode==0,'GitHub API failed: '+method+' '+endpoint+' (no authentication diagnostics printed)')
        return json.loads(result.stdout)


def public_check(url, expected):
    require(url.startswith(RAW), 'Unexpected public download host')
    request=urllib.request.Request(url,headers={'User-Agent':'TalosHelper-Component-Public-Check'})
    with urllib.request.urlopen(request,timeout=90) as response:
        require(response.geturl().startswith(RAW), 'Unexpected public download redirect')
        h=hashlib.sha256(); size=0
        while chunk:=response.read(1024*1024):
            size += len(chunk); require(size<=expected['bytes'],'Oversized public file')
            h.update(chunk)
    require(size==expected['bytes'] and h.hexdigest()==expected['sha256'], 'Public bytes mismatch: '+url.rsplit('/',1)[-1])


def old_stable():
    public_check(RAW+BRANCH+'/latest.json',{'bytes':(ROOT/'dist/online-0.3.3/latest.json').stat().st_size,'sha256':OLD_SHA})
    require(api('repos/'+REPO+'/releases/latest')['tag_name']=='v0.3.3','Python stable latest release changed')


def authorized_remote(work):
    allowed = {'https://github.com/'+REPO, 'https://github.com/'+REPO+'.git'}
    # Validate both effective directions on every reuse. Never print configured URLs.
    fetch = run(['git','remote','get-url','--all','origin'],cwd=work).splitlines()
    push = run(['git','remote','get-url','--push','--all','origin'],cwd=work).splitlines()
    require(len(fetch) == len(push) == 1 and fetch[0] in allowed and push[0] in allowed,
            'Fetch/push origin differs from the exact authorized HTTPS repository')
    return fetch[0]


def prepare_checkout(version):
    work=ROOT.parent/'scratch'/('talos-component-publish-'+version)
    remote = authorized_remote(ROOT)
    if not (work/'.git').exists():
        require(not work.exists(),'Unexpected publication checkout directory')
        run(['git','clone','--single-branch','--branch',BRANCH,remote,str(work)])
    require(work.is_dir() and not work.is_symlink(),'Invalid private publication checkout')
    require(not (work/'.git').is_symlink(), 'Symlink publication Git directory')
    authorized_remote(work)
    fetch=['git','fetch']
    if run(['git','rev-parse','--is-shallow-repository'],cwd=work)=='true': fetch.append('--unshallow')
    run(fetch+['origin','refs/heads/'+BRANCH],cwd=work)
    run(['git','reset','--hard','FETCH_HEAD'],cwd=work)  # Only this publisher's isolated scratch clone.
    require(digest(plain(work/'latest.json'))==OLD_SHA,'Scratch branch Python stable feed differs')
    return work


def remote_version(work, version, desired):
    current=work/'csharp/latest.json'; plain(current)
    if digest(current)==desired: return
    manifest=json.loads(base64.b64decode(load(current)['payload'],validate=True))
    def key(value):
        require(re.fullmatch(r'\d+\.\d+\.\d+',value),'Invalid existing channel version')
        return tuple(map(int,value.split('.')))
    require(key(manifest['version'])<key(version),'Existing C# channel is newer or same-version with different content')


def commit_files(work, files, message, *, mutable=False):
    allowed=set(files)
    for name,source in files.items():
        relative(name); destination=work/name
        for parent in destination.parents:
            if parent==work: break
            require(not parent.is_symlink(),'Remote tree contains a symlink parent')
        require(not destination.is_symlink(),'Remote tree contains a symlink destination')
        if destination.exists():
            require(destination.is_file(),'Remote destination is not a file')
            if digest(destination)==digest(source): continue
            require(mutable and name=='csharp/latest.json','Refuse overwrite of a different published version file: '+name)
        destination.parent.mkdir(parents=True,exist_ok=True); shutil.copyfile(source,destination)
    run(['git','add','--',*sorted(allowed)],cwd=work)
    changed=set(filter(None,run(['git','diff','--cached','--name-only'],cwd=work).splitlines()))
    require(changed<=allowed,'Unexpected staged branch paths')
    if changed:
        run(['git','-c','user.name=Codex','-c','user.email=codex@openai.com','commit','-m',message],cwd=work)
        authorized_remote(work)
        run(['git','push','origin','HEAD:refs/heads/'+BRANCH],cwd=work)  # Non-force CAS: rejects concurrent remote advances.
    return run(['git','rev-parse','HEAD'],cwd=work)


@contextmanager
def publication_lock(out):
    with (out/'component-publish.lock').open('a+b') as lock:
        try:
            import fcntl
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except (ImportError, BlockingIOError) as error:
            raise RuntimeError('Publication requires the Linux publisher lock; another publisher may be active') from error
        yield


def publish(version):
    # The developer publisher runs on the configured Linux cloud environment.
    # Local checks remain portable and do not acquire a network/publication lock.
    out, _, _=checked(version)
    with publication_lock(out): return publish_locked(version)


def publish_locked(version):
    out, files, plan=checked(version)
    receipt_path=out/'component-publish-receipt.json'
    receipt=load(receipt_path) if receipt_path.exists() else {'version':version,'planId':plan['planId']}
    require(receipt.get('version')==version and receipt.get('planId')==plan['planId'],'Receipt belongs to different reviewed content')
    # Compile only the developer verifier before touching any remote state. The
    # signed application, host, feed and archives are never rebuilt or re-signed.
    dotnet_env = os.environ.copy()
    dotnet_env.update({'DOTNET_CLI_HOME':'/workspace/.dotnet-cli','TMPDIR':'/workspace/.dotnet-tmp'})
    run(['/workspace/.dotnet/dotnet','build','scripts/VerifyChannel/VerifyChannel.csproj','-c','Release',
         '--no-restore','--disable-build-servers','-m:1','-nr:false','-p:UseSharedCompilation=false'],cwd=CS,env=dotnet_env)
    require(api('repos/'+REPO)['private'] is False,'Expected authorized public repository')
    old_stable()
    work=prepare_checkout(version)
    remote_version(work,version,plan['signedFeedSha256'])
    # Existing version paths are immutable, including on an interrupted retry.
    stage_commit=commit_files(work,files,'Stage reviewed C# '+version+' component release')
    recorded=receipt.get('stageCommit')
    if recorded:
        # prepare_checkout unshallows legacy scratch clones before this check, so
        # receipt commits remain provable ancestors after channel/evidence commits.
        run(['git','fetch','origin',recorded],cwd=work)
        result=subprocess.run(['git','merge-base','--is-ancestor',recorded,'HEAD'],cwd=work,capture_output=True)
        require(result.returncode==0,'Previously staged commit no longer belongs to branch history')
        stage_commit=recorded
    receipt['stageCommit']=stage_commit; atomic_json(receipt_path,receipt)
    for name, expected in plan['files'].items(): public_check(RAW+stage_commit+'/'+name,expected)
    payload=load(out/'manifest.json')['package']; public_check(payload['url'],payload)
    receipt['versionDownloadsVerified']=True; atomic_json(receipt_path,receipt)
    # Re-fetch and reject concurrent changes before the channel switch. No version paths
    # may be replaced, and a newer channel can never be downgraded on a retry.
    work=prepare_checkout(version)
    for name,expected in plan['files'].items(): verify_item(work/name,expected)
    remote_version(work,version,plan['signedFeedSha256']); old_stable()
    channel_commit=commit_files(work,{'csharp/latest.json':out/'latest.json'},'Activate signed C# '+version+' update channel',mutable=True)
    receipt['channelCommit']=channel_commit; atomic_json(receipt_path,receipt)
    public_check(FEED,identity(out/'latest.json'))
    command=['/workspace/.dotnet/dotnet','run','--project','scripts/VerifyChannel/VerifyChannel.csproj','-c','Release','--no-restore','--no-build','--',
             FEED,str(CS/'update-public-key.pem'),version,str(ROOT/'dist/csharp-0.4.2/latest.json'),
             str(ROOT/'dist/csharp-0.4.2/TalosHelper-CSharp-0.4.2-update.zip')]
    stdout=run(command,cwd=CS,env=dotnet_env); live=json.loads(stdout[stdout.index('{'):])
    require(live.get('passed') and live['version']==version and live['actualHttpsTransport'] and
            live['signatureAlgorithm']=='ES256' and live['activeFilesVerified'] is True and
            live['signedPayloadBytes']==payload['bytes'] and
            live['upgradedFrom']=='0.4.2' and live['offlineRollbackVersion']=='0.4.2' and
            live['originalHostPreservesUpdate'] and live['signedPayloadSha256']==payload['sha256'], 'Native real-channel upgrade/rollback failed')
    live_path=out/'component-live-channel-report.json'
    if live_path.exists():
        # The verifier emits deterministic facts; preserve already published evidence bytes.
        require(load(live_path)==live,'Live verification differs from prior publication facts')
    else: atomic_json(live_path,live)
    prefix='csharp/releases/'+version+'/'
    work=prepare_checkout(version); remote_version(work,version,plan['signedFeedSha256'])
    evidence_commit=commit_files(work,{prefix+'LIVE-CHANNEL-REPORT.json':live_path},'Record native C# '+version+' public upgrade verification')
    receipt['verificationCommit']=receipt.get('verificationCommit',evidence_commit); atomic_json(receipt_path,receipt)
    public_check(RAW+receipt['verificationCommit']+'/'+prefix+'LIVE-CHANNEL-REPORT.json',identity(live_path))
    base=RAW+stage_commit+'/'+prefix
    notes=(CS/f'RELEASE-NOTES-{version}.md').read_text('utf8')
    complete=next(name for name in plan['files'] if name.endswith('-Windows-x64.zip'))
    offline=next(name for name in plan['files'] if name.endswith('-Offline-Update.zip'))
    body=(f'**[下载完整 Windows x64 便携包]({RAW+stage_commit}/{complete})** · '
          f'[离线小更新]({RAW+stage_commit}/{offline})\n\n'
          '已有0.4.2或更新的便携版，可在助手中主动检查并下载更新。保留原data，无需重发运行环境。\n\n'+notes+
          f'\n\n[校验值]({base}SHA256SUMS.txt) · [原生在线升级及离线回退验证]({RAW+receipt["verificationCommit"]}/{prefix}LIVE-CHANNEL-REPORT.json)\n')
    endpoint='repos/'+REPO+'/releases/tags/'+plan['tag']
    release=api(endpoint,missing_ok=True)
    value={'name':'典狱助手 '+version+' C# 测试版','body':body,'draft':False,'prerelease':True,'make_latest':'false'}
    if release:
        require(release['body']==body and release['draft'] is False and release['prerelease'] is True,
                'Existing release has different public content; refuse to overwrite')
    else:
        value.update({'tag_name':plan['tag'],'target_commitish':stage_commit})
        release=api('repos/'+REPO+'/releases',method='POST',value=value)
    final=api(endpoint)
    require(final['draft'] is False and final['prerelease'] is True and final['body']==body,'Release final metadata mismatch')
    public_check(FEED,identity(out/'latest.json')); public_check(payload['url'],payload); old_stable()
    result={'schema':'talos-component-publication-v1','passed':True,'version':version,'planId':plan['planId'],
            'releaseUrl':final['html_url'],'feedUrl':FEED,'feedSha256':plan['signedFeedSha256'],
            'stageCommit':stage_commit,'channelCommit':channel_commit,'verificationCommit':receipt['verificationCommit'],
            'publicVersionFilesVerified':plan['files'],'actualNativeUpgradeAndOfflineRollback':True,
            'pythonStableUnchanged':True,'prerelease':True,'makeLatest':False,'windowsExeExecuted':False,
            'privateKeyRead':False,'requiresResigning':False}
    atomic_json(out/'component-publication-report.json',result)
    receipt['completed']=True; receipt['releaseUrl']=final['html_url']; atomic_json(receipt_path,receipt)
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version',required=True)
    mode=parser.add_mutually_exclusive_group(); mode.add_argument('--check',action='store_true'); mode.add_argument('--publish',action='store_true')
    args=parser.parse_args()
    result=publish(args.version) if args.publish else checked(args.version)[2]
    print(json.dumps(result,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
