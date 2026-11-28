"""Verify and publish signed clients and their corresponding source on GitHub."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import time
from urllib.error import HTTPError
from urllib.parse import urlencode, urlparse
from urllib.request import Request, build_opener, HTTPRedirectHandler
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8', newline='\n')


def git(*arguments):
    return subprocess.check_output(['git', '-C', str(ROOT), *arguments], encoding='utf-8').strip()


def artifact_types_from_manifest(manifest):
    artifacts = manifest.get('artifacts')
    require(isinstance(artifacts, list) and artifacts, 'Release manifest must contain at least one client artifact')
    types = [row.get('type') if isinstance(row, dict) else None for row in artifacts]
    require(all(kind in ('apk', 'setup') for kind in types), 'Release manifest contains an unknown client artifact type')
    require(len(types) == len(set(types)), 'Release manifest contains duplicate client artifact types')
    require('apk' in types and len(types) in (1, 2), 'Release manifest must contain exactly one APK and at most one Setup')
    return frozenset(types)


def expected_names(version, build, artifact_types=None):
    require(isinstance(version, str) and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid release version')
    require(type(build) is int and 1 <= build <= 65535, 'Invalid release build')
    raw_types = ('apk', 'setup') if artifact_types is None else tuple(artifact_types)
    require(len(raw_types) == len(set(raw_types)), 'Duplicate client artifact types')
    types = frozenset(raw_types)
    require(types in (frozenset(('apk',)), frozenset(('apk', 'setup'))), 'Invalid client artifact types')
    names = {f'TermLink-Android-{version}-{build}.apk',
            f'TermLink-{version}-{build}-source.zip',
            f'TermLink-{version}-{build}-source.zip.sha256.json',
            'artifact-manifest.json', 'SHA256SUMS'}
    if 'setup' in types:
        names.add(f'TermLink-Setup-{version}.{build}-x64.exe')
    return names


def macos_asset_names(version, build):
    require(isinstance(version, str) and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid release version')
    require(type(build) is int and 1 <= build <= 65535, 'Invalid release build')
    return {
        f'TermLink-macOS-{version}-{build}-osx-arm64.zip',
        f'TermLink-macOS-{version}-{build}-osx-x64.zip',
        'macos-osx-arm64-verification.json',
        'macos-osx-x64-verification.json',
    }


def read_macos_policy(revision):
    try:
        policy = json.loads(git('show', revision+':release/github-macos-signing-policy.json'))
    except (subprocess.CalledProcessError, json.JSONDecodeError):
        raise ValueError('GitHub macOS signing policy is missing from the release commit') from None
    require(policy.get('schema_version') == 1, 'Unsupported GitHub macOS signing policy schema')
    signer = str(policy.get('signer_sha1', '')).replace(':', '').replace('-', '').lower()
    require(re.fullmatch(r'[0-9a-f]{40}', signer), 'GitHub macOS signing policy has an invalid signer_sha1')
    if policy.get('signer_sha256'):
        signer_sha256 = str(policy['signer_sha256']).replace(':', '').replace('-', '').lower()
        require(re.fullmatch(r'[0-9a-f]{64}', signer_sha256),
                'GitHub macOS signing policy has an invalid signer_sha256')
    if policy.get('bundle_id'):
        require(isinstance(policy['bundle_id'], str) and
                re.fullmatch(r'[A-Za-z0-9.-]+', policy['bundle_id']),
                'GitHub macOS signing policy has an invalid bundle_id')
    return policy, signer


def validate_macos_evidence(directory, version, build, revision, mac_lock_revision, signer, rid, policy=None):
    filename = f'TermLink-macOS-{version}-{build}-{rid}.zip'
    evidence_path = directory/f'macos-{rid}-verification.json'
    evidence = read_json(evidence_path)
    require(evidence.get('schema_version') == 1 and evidence.get('verified') is True,
            f'Invalid macOS verification evidence: {evidence_path.name}')
    require(evidence.get('notarized') is False, 'Self-signed macOS evidence must declare notarized=false')
    require(evidence.get('source_revision') == revision and evidence.get('core_revision') == mac_lock_revision and
            evidence.get('version') == version and evidence.get('build_number') == build and
            evidence.get('rid') == rid and evidence.get('filename') == filename and
            str(evidence.get('signer_sha1', '')).replace(':', '').replace('-', '').lower() == signer,
            f'MacOS verification evidence identity mismatch: {evidence_path.name}')
    artifact = directory/filename
    actual_size = artifact.stat().st_size
    actual_sha = sha256(artifact)
    require(type(evidence.get('size')) is int and evidence.get('size') == actual_size and
            str(evidence.get('sha256', '')).lower() == actual_sha,
            f'MacOS verification evidence hash mismatch: {filename}')
    if policy and policy.get('signer_sha256'):
        signer_sha256 = str(policy['signer_sha256']).replace(':', '').replace('-', '').lower()
        require(re.fullmatch(r'[0-9a-f]{64}', signer_sha256) and
                str(evidence.get('signer_sha256', '')).replace(':', '').replace('-', '').lower() == signer_sha256,
                f'MacOS SHA-256 signer mismatch: {evidence_path.name}')
    if policy and policy.get('bundle_id'):
        require(evidence.get('bundle_id') == policy['bundle_id'],
                f'MacOS bundle identifier mismatch: {evidence_path.name}')


def validate_bundle(directory, revision, digest=None, with_macos=False):
    require(re.fullmatch('[0-9a-f]{40}', revision), 'Expected a full source commit')
    metadata_path = directory/'github-release.json'
    if digest:
        require(sha256(metadata_path) == digest, 'Release metadata digest changed')
    metadata = read_json(metadata_path)
    version, build = metadata['version'], metadata['build_number']
    require(metadata['schema_version'] == 1 and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid release identity')
    require(type(build) is int and 1 <= build <= 65535, 'Invalid release build')
    require(metadata['source_revision'] == revision == git('rev-parse', '--verify', revision+'^{commit}'), 'Release source commit is unavailable')
    require(metadata['signers'] == json.loads(git('show', revision+':release/github-release-policy.json')), 'Unexpected signing identities')
    if 'macos' in metadata:
        require(type(metadata['macos']) is bool, 'Invalid macOS release flag')
        require(metadata['macos'] == with_macos, 'MacOS release flag differs from validation mode')
    else:
        require(not with_macos, 'MacOS assets require macos=true release metadata')
    records = metadata['files']
    manifest = read_json(directory/'artifact-manifest.json')
    artifact_types = artifact_types_from_manifest(manifest)
    expected = expected_names(version, build, artifact_types)
    if with_macos:
        expected |= macos_asset_names(version, build)
    require(len(records) == len(expected), 'Unexpected release file count')
    require({row['name'] for row in records} == expected, 'Unexpected release filenames')
    for row in records:
        path = directory/row['name']
        require(path.is_file() and not path.is_symlink(), f'Missing regular release file: {row["name"]}')
        require(path.stat().st_size == row['size'] and sha256(path) == row['sha256'], f'Release file changed: {row["name"]}')
    require(manifest['release_mode'] == 'production' and manifest['version'] == version and
            manifest['build_number'] == build and manifest['source_revision'] == revision, 'Client manifest identity mismatch')
    lock = json.loads(git('show', revision+':terminal-core.lock.json'))
    require(not lock['source_dirty'] and manifest['core_revision'] == metadata['core_revision'] == lock['revision'], 'Core revision mismatch')
    mac_lock_revision = None
    mac_signer = None
    if with_macos:
        mac_lock = json.loads(git('show', revision+':terminal-core.macos.lock.json'))
        mac_lock_revision = str(mac_lock.get('revision', ''))
        require(re.fullmatch(r'[0-9a-f]{40}', mac_lock_revision) and not mac_lock.get('source_dirty', False),
                'Invalid macOS native lock')
        mac_policy, mac_signer = read_macos_policy(revision)
        require(str(metadata.get('macos_signer_sha1', '')).replace(':', '').replace('-', '').lower() == mac_signer,
                'MacOS signer policy differs from release metadata')
        if mac_policy.get('signer_sha256'):
            mac_signer_sha256 = str(mac_policy['signer_sha256']).replace(':', '').replace('-', '').lower()
            require(re.fullmatch(r'[0-9a-f]{64}', mac_signer_sha256) and
                    str(metadata.get('macos_signer_sha256', '')).replace(':', '').replace('-', '').lower() == mac_signer_sha256,
                    'MacOS SHA-256 signer policy differs from release metadata')
        else:
            mac_signer_sha256 = None
        if mac_policy.get('bundle_id'):
            require(metadata.get('macos_bundle_id') == mac_policy['bundle_id'],
                    'MacOS bundle identifier policy differs from release metadata')
    if 'artifact_types' in metadata:
        require(frozenset(metadata['artifact_types']) == artifact_types, 'Release metadata artifact types differ from manifest')
    for row in manifest['artifacts']:
        name = PurePosixPath(row['path']).name
        require(row['path'] == f'artifacts/release/{version}-{build}/{name}', 'Unsafe client artifact path')
        expected_name = (f'TermLink-Android-{version}-{build}.apk' if row['type'] == 'apk'
                         else f'TermLink-Setup-{version}.{build}-x64.exe')
        require(name == expected_name, 'Client artifact filename does not match its type')
        record = next((item for item in records if item['name'] == name), None)
        require(record and row['sha256'] == record['sha256'] and row['size'] == record['size'] and row['version'] == version, 'Client hashes differ from release metadata')
    source = directory/f'TermLink-{version}-{build}-source.zip'
    sidecar = read_json(source.with_name(source.name+'.sha256.json'))
    require(sidecar['archive'] == source.name and sidecar['sha256'] == sha256(source), 'Corresponding-source digest mismatch')
    source_manifest_core_revision = None
    with zipfile.ZipFile(source) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), 'Duplicate corresponding-source entries')
        for name in names:
            require(not name.startswith('/') and '\\' not in name and ':' not in name and
                    all(part not in ('', '.', '..') for part in name.split('/')), 'Unsafe source archive entry')
        source_manifest = json.loads(archive.read('SOURCE-MANIFEST.json'))
        source_manifest_core_revision = source_manifest.get('core_revision')
        require(source_manifest['application_revision'] == revision and source_manifest['core_revision'] == lock['revision'] and
                source_manifest['version'] == version and source_manifest['build_number'] == build and
                source_manifest['core_dependencies_vendored'] is True, 'Corresponding-source provenance mismatch')
        require(source_manifest['release_artifact_manifest']['sha256'] == sha256(directory/'artifact-manifest.json'), 'Source is bound to a different client manifest')
        require(set(names) == {row['path'] for row in source_manifest['files']} | {'README-SOURCE.md', 'SOURCE-MANIFEST.json'}, 'Source entry inventory mismatch')
        require(len(names) == len(source_manifest['files'])+2, 'Duplicate source inventory records')
        tree = {}
        for entry in git('ls-tree', '-r', '-z', revision).split('\0'):
            if entry:
                identity, path = entry.split('\t', 1)
                mode, kind, blob = identity.split()
                require(kind == 'blob', 'Submodules are not supported in release source')
                tree['termlink/'+path] = blob
        require({row['path'] for row in source_manifest['files'] if row['path'].startswith('termlink/')} == set(tree), 'Application source differs from selected Git tree')
        for row in source_manifest['files']:
            info = archive.getinfo(row['path'])
            require(info.file_size == row['size'] and info.file_size < 512*1024*1024, 'Invalid source file size')
            require((info.external_attr >> 16) & 0o170000 == 0o100000, 'Source entries must be regular files')
            content = archive.read(info)
            require(hashlib.sha256(content).hexdigest() == row['sha256'], f'Source content changed: {row["path"]}')
            if row['path'] in tree:
                blob = hashlib.sha1(b'blob '+str(len(content)).encode()+b'\0'+content).hexdigest()
                require(blob == tree[row['path']], f'Application source is not from the selected commit: {row["path"]}')
        for row in lock['artifacts']:
            require(hashlib.sha256(archive.read('termlink/'+row['path'])).hexdigest() == row['sha256'], 'Archived native library differs from lock')
    if with_macos:
        require(source_manifest_core_revision == mac_lock_revision,
                'Corresponding source core revision differs from terminal-core.macos.lock.json')
        for rid in ('osx-arm64', 'osx-x64'):
            validate_macos_evidence(directory, version, build, revision, mac_lock_revision, mac_signer, rid, mac_policy)
    return metadata


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        return None


class GitHub:
    def __init__(self, repository):
        require(re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository), 'Invalid repository')
        self.base = 'https://api.github.com/repos/'+repository
        self.token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
        require(self.token, 'Set GH_TOKEN or GITHUB_TOKEN through the credential manager or Actions environment')

    def request(self, path, method='GET', value=None, data=None, content_type='application/json'):
        url = self.base+path
        body = json.dumps(value).encode() if value is not None else data
        request = Request(url, data=body, method=method, headers={
            'Authorization': 'Bearer '+self.token, 'Accept': 'application/vnd.github+json',
            'Content-Type': content_type, 'User-Agent': 'TermLink-Release', 'X-GitHub-Api-Version': '2026-03-10'})
        try:
            with build_opener(NoRedirect).open(request, timeout=120) as response:
                result = response.read()
            return json.loads(result) if result else None
        except HTTPError as error:
            raise RuntimeError(f'GitHub {method} {path.split("?")[0]} failed: HTTP {error.code}') from None

    def download(self, asset_id, destination):
        request = Request(self.base+'/releases/assets/'+str(asset_id), headers={
            'Authorization': 'Bearer '+self.token, 'Accept': 'application/octet-stream', 'User-Agent': 'TermLink-Release'})
        try:
            response = build_opener(NoRedirect).open(request, timeout=120)
        except HTTPError as error:
            require(error.code in (301, 302, 303, 307, 308), 'Release download request failed')
            target = error.headers['Location']
            parsed = urlparse(target)
            require(parsed.scheme == 'https' and parsed.hostname in ('release-assets.githubusercontent.com', 'objects.githubusercontent.com'), 'Unexpected release asset host')
            # Never forward the API token to a signed asset redirect.
            response = build_opener(NoRedirect).open(Request(target), timeout=120)
        with response, destination.open('wb') as output:
            shutil.copyfileobj(response, output, length=1024*1024)

    def require_checks(self, revision):
        response = self.request('/actions/workflows/checks.yml/runs?'+urlencode({'head_sha': revision, 'per_page': 1}))
        require(response['workflow_runs'], 'No repository checks found for release source')
        run = response['workflow_runs'][0]
        require(run['head_sha'] == revision and run['status'] == 'completed' and run['conclusion'] == 'success', 'Latest repository checks have not passed')

    def require_tag(self, tag, revision):
        ref = self.request('/git/ref/tags/'+tag)
        obj = ref['object']
        if obj['type'] == 'tag':
            obj = self.request('/git/tags/'+obj['sha'])['object']
        require(obj['type'] == 'commit' and obj['sha'] == revision, 'Release tag points at a different source commit')


def fetch_core_source(args, api):
    lock = read_json(ROOT/'terminal-core.lock.json')
    source = read_json(ROOT/'release/github-client-core-source.json')
    require(source['schema_version'] == 1 and not lock['source_dirty'] and
            source['core_revision'] == lock['revision'], 'Core source input does not match the clean native lock')
    asset_id = source['github_asset_id']
    expected_digest = 'sha256:'+source['archive_sha256']
    require(type(asset_id) is int and asset_id > 0, 'Invalid core source input asset identity')
    asset = api.request(f'/releases/assets/{asset_id}')
    require(asset['id'] == asset_id and asset['state'] == 'uploaded' and asset.get('digest') == expected_digest and
            asset['name'] == f"TermLink-{source['archive_version']}-{source['archive_build']}-core-source-input.zip",
            'Core source input metadata differs from the pinned archive')
    args.directory.mkdir(parents=True, exist_ok=True)
    target = args.directory/'core-source.zip'
    api.download(asset_id, target)
    require('sha256:'+sha256(target) == expected_digest, 'Core source input download digest mismatch')
    print(json.dumps(dict(core_revision=lock['revision'], sha256=source['archive_sha256'])))


def prepare_bundle(directory, with_macos=False):
    manifest = read_json(directory/'artifact-manifest.json')
    version, build, revision = manifest['version'], manifest['build_number'], manifest['source_revision']
    require(not git('status', '--porcelain'), 'Commit source and documentation before staging a release')
    require(revision == git('rev-parse', 'HEAD'), 'Stage the clients from their exact build source checkout')
    artifact_types = artifact_types_from_manifest(manifest)
    names = expected_names(version, build, artifact_types)
    if with_macos:
        names |= macos_asset_names(version, build)
    names -= {'SHA256SUMS'}
    checksum_text = ''.join(f'{sha256(directory/name)}  {name}\n' for name in sorted(names))
    (directory/'SHA256SUMS').write_text(checksum_text, encoding='utf-8', newline='\n')
    metadata = dict(schema_version=1, version=version, build_number=build, source_revision=revision,
                    core_revision=manifest['core_revision'], artifact_types=sorted(artifact_types),
                    signers=read_json(ROOT/'release/github-release-policy.json'),
                    macos=bool(with_macos),
                    files=[dict(name=name, size=(directory/name).stat().st_size, sha256=sha256(directory/name))
                           for name in sorted(names)])
    if with_macos:
        policy, signer = read_macos_policy(revision)
        metadata['macos_signer_sha1'] = signer
        if policy.get('signer_sha256'):
            metadata['macos_signer_sha256'] = str(policy['signer_sha256']).replace(':', '').replace('-', '').lower()
        if policy.get('bundle_id'):
            metadata['macos_bundle_id'] = policy['bundle_id']
    write_json(directory/'github-release.json', metadata)
    validate_bundle(directory, revision, with_macos=with_macos)
    return metadata


def stage_bundle(directory, api, metadata, notes, allow_published=False):
    version, build, revision = metadata['version'], metadata['build_number'], metadata['source_revision']
    artifact_types = metadata['artifact_types']
    api.require_checks(revision)
    tag = 'v'+version
    api.require_tag(tag, revision)
    releases = api.request('/releases?per_page=100')
    if allow_published:
        for row in releases:
            previous = re.fullmatch(r'v(\d+)\.(\d+)\.(\d+)', row['tag_name'])
            if not previous or row['tag_name'] == tag or row['draft'] or row['prerelease']:
                continue
            previous_build = re.search(r'\(build (\d+)\)$', row.get('name') or '')
            require(tuple(map(int, version.split('.'))) > tuple(map(int, previous.groups())) and
                    (not previous_build or build > int(previous_build[1])),
                    'Client publication would move the stable version or build backwards')
    existing = [row for row in releases if row['tag_name'] == tag]
    require(len(existing) <= 1, 'Ambiguous release identity')
    if existing:
        release = existing[0]
        require((release['draft'] or allow_published) and release['target_commitish'] == revision, 'Release already exists for a different publication')
        require(release['body'] == notes, 'Release notes differ from existing release')
    else:
        release = api.request('/releases', 'POST', dict(tag_name=tag, target_commitish=revision,
            name=f'TermLink {version} (build {build})', body=notes, draft=True, prerelease=False))
    assets = {row['name']: row for row in api.request(f'/releases/{release["id"]}/assets?per_page=100')}
    upload_names = expected_names(version, build, artifact_types) | {'github-release.json'}
    if metadata.get('macos'):
        upload_names |= macos_asset_names(version, build)
    require(set(assets) <= upload_names, 'Unexpected existing draft attachments; preserve draft and inspect manually')
    for name in sorted(upload_names):
        path = directory/name
        if name in assets:
            require(assets[name]['size'] == path.stat().st_size and assets[name].get('digest') == 'sha256:'+sha256(path), 'Existing draft asset differs; refusing replacement')
            continue
        require(release['draft'], 'Published release attachments cannot be repaired by uploading files')
        # Stream the binary body instead of duplicating large Setup files in memory.
        upload_url = release['upload_url'].split('{', 1)[0]+'?'+urlencode({'name': name})
        require(upload_url.startswith('https://uploads.github.com/repos/'), 'Unexpected upload API host')
        with path.open('rb') as body:
            request = Request(upload_url, data=body, method='POST', headers={
                'Authorization': 'Bearer '+api.token, 'Content-Type': 'application/octet-stream',
                'Content-Length': str(path.stat().st_size), 'User-Agent': 'TermLink-Release'})
            with build_opener(NoRedirect).open(request, timeout=600) as response:
                uploaded = json.load(response)
        require(uploaded.get('digest') == 'sha256:'+sha256(path), 'Uploaded asset digest mismatch')
        print('Uploaded '+name, flush=True)
    return release['id']


def stage(args, api):
    directory = args.directory.resolve()
    metadata = prepare_bundle(directory, with_macos=getattr(args, 'with_macos', False))
    release_id = stage_bundle(directory, api, metadata, args.notes.read_text(encoding='utf-8'))
    revision = metadata['source_revision']
    inputs = dict(release_id=str(release_id), revision=revision, metadata_sha256=sha256(directory/'github-release.json'))
    write_json(directory/'github-dispatch.json', inputs)
    previous_runs = {run['id'] for run in api.request('/actions/workflows/github-release.yml/runs?event=workflow_dispatch&per_page=20')['workflow_runs']}
    api.request('/actions/workflows/github-release.yml/dispatches', 'POST', dict(ref='main', inputs=inputs))
    print('Waiting for the pinned GitHub verification run.', flush=True)
    deadline = time.monotonic()+1800
    while time.monotonic() < deadline:
        runs = api.request('/actions/workflows/github-release.yml/runs?event=workflow_dispatch&per_page=20')['workflow_runs']
        matches = [run for run in runs if run['id'] not in previous_runs and
                   run['display_title'] == f'Verify release {release_id} {inputs["metadata_sha256"]}']
        if matches and matches[0]['status'] == 'completed':
            require(matches[0]['conclusion'] == 'success', 'GitHub verification failed; preserve the draft and retry after fixing the pipeline')
            args.release_id, args.revision = release_id, revision
            args.metadata_sha256, args.verification_run = inputs['metadata_sha256'], matches[0]['id']
            args.verify_only = False
            publish(args, api)
            return
        time.sleep(10)
    raise RuntimeError('GitHub verification is still pending; preserve the draft and use publish --verification-run after it succeeds')


def receive(args, api):
    directory = args.directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    release = api.request(f'/releases/{args.release_id}')
    assets = api.request(f'/releases/{args.release_id}/assets?per_page=100')
    indexed = {row['name']: row for row in assets}
    require(len(indexed) == len(assets) and 'github-release.json' in indexed, 'Release attachment inventory is invalid')
    api.download(indexed['github-release.json']['id'], directory/'github-release.json')
    require(sha256(directory/'github-release.json') == args.metadata_sha256, 'Release metadata digest mismatch')
    metadata = read_json(directory/'github-release.json')
    metadata_types = frozenset(metadata.get('artifact_types', ('apk', 'setup')))
    metadata_macos = bool(metadata.get('macos', False))
    if 'artifact_types' not in metadata:
        metadata_types = frozenset({'apk'} | ({'setup'} if f'TermLink-Setup-{metadata["version"]}.{metadata["build_number"]}-x64.exe' in indexed else set()))
    expected_remote = expected_names(metadata['version'], metadata['build_number'], metadata_types) | {'github-release.json'}
    if metadata_macos:
        expected_remote |= macos_asset_names(metadata['version'], metadata['build_number'])
    require(set(indexed) == expected_remote, 'Unexpected remote attachments')
    require(release['tag_name'] == 'v'+metadata['version'] and release['target_commitish'] == args.revision, 'Draft source identity mismatch')
    api.require_tag(release['tag_name'], args.revision)
    for name, asset in indexed.items():
        require(asset['state'] == 'uploaded', 'Incomplete release asset')
        if name != 'github-release.json':
            api.download(asset['id'], directory/name)
    validate_bundle(directory, args.revision, args.metadata_sha256, with_macos=metadata_macos)
    print(json.dumps(dict(verified=True, version=metadata['version'], build_number=metadata['build_number'])))


def publish(args, api):
    metadata = validate_bundle(args.directory.resolve(), args.revision, args.metadata_sha256,
                                with_macos=getattr(args, 'with_macos', False))
    if args.verify_only:
        evidence = read_json(args.directory/'ci-verification.json')
        require(evidence['verified'] is True and evidence['source_revision'] == args.revision and
                evidence['version'] == metadata['version'] and evidence['build_number'] == metadata['build_number'], 'Native signature verification has not passed')
        print(json.dumps(dict(native_signatures_verified=True, release_id=args.release_id,
                              revision=args.revision, metadata_sha256=args.metadata_sha256)))
        return
    require(args.verification_run, 'A successful GitHub verification run is required before publication')
    run = api.request(f'/actions/runs/{args.verification_run}')
    require(run['status'] == 'completed' and run['conclusion'] == 'success' and
            run['path'] == '.github/workflows/github-release.yml' and run['event'] == 'workflow_dispatch' and
            run['head_branch'] == 'main' and
            run['display_title'] == f'Verify release {args.release_id} {args.metadata_sha256}', 'Verification run does not approve this exact release metadata')
    publish_bundle(api, metadata, args.revision, args.release_id, args.metadata_sha256)


def publish_bundle(api, metadata, revision, release_id, metadata_sha256):
    api.require_checks(revision)
    api.require_tag('v'+metadata['version'], revision)
    release = api.request(f'/releases/{release_id}')
    require(release['tag_name'] == 'v'+metadata['version'] and release['target_commitish'] == revision, 'Release identity changed')
    remote = api.request(f'/releases/{release_id}/assets?per_page=100')
    records = metadata['files']+[dict(name='github-release.json', sha256=metadata_sha256)]
    require(len(remote) == len(records), 'Remote attachments changed during verification')
    for row in records:
        matches = [asset for asset in remote if asset['name'] == row['name']]
        require(len(matches) == 1 and matches[0]['state'] == 'uploaded' and matches[0].get('digest') == 'sha256:'+row['sha256'], 'Remote asset changed during verification')
    if release['draft']:
        release = api.request(f'/releases/{release_id}', 'PATCH', dict(draft=False, make_latest='true',
            tag_name='v'+metadata['version'], target_commitish=revision))
    require(not release['draft'] and not release['prerelease'] and release['tag_name'] == 'v'+metadata['version'] and
            release['target_commitish'] == revision, 'Release was not published at the expected tag and source')
    print(json.dumps(dict(published=True, url=release['html_url'], version=metadata['version'], build_number=metadata['build_number'])))


def publish_from_tag(args, api):
    directory = args.directory.resolve()
    metadata = validate_bundle(directory, args.revision, with_macos=getattr(args, 'with_macos', False))
    require(f"v{metadata['version']}" == args.tag and metadata['source_revision'] == args.revision,
            'Signed batch does not match the release tag')
    require(artifact_types_from_manifest(read_json(directory/'artifact-manifest.json')) == {'apk', 'setup'},
            'A client tag must deliver both Android and Windows')
    evidence = read_json(directory/'ci-verification.json')
    require(evidence['verified'] is True and evidence['source_revision'] == args.revision and
            evidence['version'] == metadata['version'] and evidence['build_number'] == metadata['build_number'] and
            evidence['manifest_sha256'] == sha256(directory/'artifact-manifest.json'),
            'Signed batch verification evidence is missing or does not match')
    if getattr(args, 'with_macos', False):
        require(metadata.get('macos') is True, 'MacOS assets were requested but release metadata is not macOS-enabled')
    notes = (f"Android APK and Windows x64 Setup for TermLink {metadata['version']} "
             f"(build {metadata['build_number']}).\n\n"
             "The release includes matching GPL-3.0-only corresponding source and SHA-256 checksums.\n")
    if getattr(args, 'with_macos', False):
        notes += 'Self-signed macOS arm64 and Intel ZIPs are included; notarization is not provided.\n'
    release_id = stage_bundle(directory, api, metadata, notes, allow_published=True)
    publish_bundle(api, metadata, args.revision, release_id, sha256(directory/'github-release.json'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=['stage', 'receive', 'publish', 'publish-tag', 'verify-bundle', 'fetch-core-source'])
    parser.add_argument('--repository', default=os.environ.get('GITHUB_REPOSITORY', 'myucxy/TermLink'))
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--notes', type=Path)
    parser.add_argument('--release-id', type=int)
    parser.add_argument('--revision')
    parser.add_argument('--tag')
    parser.add_argument('--metadata-sha256')
    parser.add_argument('--verify-only', action='store_true')
    parser.add_argument('--verification-run', type=int)
    parser.add_argument('--with-macos', action='store_true',
                        help='Require and publish the two self-signed macOS ZIPs and verification evidence')
    args = parser.parse_args()
    if args.phase == 'verify-bundle':
        metadata = prepare_bundle(args.directory.resolve(), with_macos=args.with_macos)
        print(json.dumps(dict(verified=True, version=metadata['version'], build_number=metadata['build_number'])))
        return
    if args.phase == 'fetch-core-source':
        fetch_core_source(args, GitHub(args.repository))
        return
    if args.phase == 'stage':
        require(args.notes and args.notes.is_file(), 'Release notes file is required')
    elif args.phase == 'publish-tag':
        require(args.tag and args.revision and re.fullmatch('[0-9a-f]{40}', args.revision), 'Invalid client tag identity')
    else:
        require(args.release_id and args.release_id > 0 and args.revision and re.fullmatch('[0-9a-f]{40}', args.revision) and
                args.metadata_sha256 and re.fullmatch('[0-9a-f]{64}', args.metadata_sha256), 'Invalid dispatch identity')
    {'stage': stage, 'receive': receive, 'publish': publish, 'publish-tag': publish_from_tag}[args.phase](args, GitHub(args.repository))


if __name__ == '__main__':
    main()
