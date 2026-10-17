"""Stage locally signed clients; let GitHub Actions verify and publish the same bytes."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
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
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')


def git(*arguments):
    return subprocess.check_output(['git', '-C', str(ROOT), *arguments], text=True).strip()


def expected_names(version, build):
    require(isinstance(version, str) and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid release version')
    require(type(build) is int and 1 <= build <= 65535, 'Invalid release build')
    return {f'TermLink-Android-{version}-{build}.apk',
            f'TermLink-Setup-{version}.{build}-x64.exe',
            f'TermLink-{version}-{build}-source.zip',
            f'TermLink-{version}-{build}-source.zip.sha256.json',
            'artifact-manifest.json', 'SHA256SUMS'}


def validate_bundle(directory, revision, digest=None):
    require(re.fullmatch('[0-9a-f]{40}', revision), 'Expected a full source commit')
    metadata_path = directory/'github-release.json'
    if digest:
        require(sha256(metadata_path) == digest, 'Release metadata digest changed')
    metadata = read_json(metadata_path)
    version, build = metadata['version'], metadata['build_number']
    require(metadata['schema_version'] == 1 and re.fullmatch(r'\d+\.\d+\.\d+', version), 'Invalid release identity')
    require(type(build) is int and 1 <= build <= 65535, 'Invalid release build')
    require(metadata['source_revision'] == revision == git('rev-parse', 'HEAD'), 'Release source differs from checkout')
    require(metadata['signers'] == read_json(ROOT/'release/github-release-policy.json'), 'Unexpected signing identities')
    records = metadata['files']
    require(len(records) == len(expected_names(version, build)), 'Unexpected release file count')
    require({row['name'] for row in records} == expected_names(version, build), 'Unexpected release filenames')
    for row in records:
        path = directory/row['name']
        require(path.is_file() and not path.is_symlink(), f'Missing regular release file: {row["name"]}')
        require(path.stat().st_size == row['size'] and sha256(path) == row['sha256'], f'Release file changed: {row["name"]}')
    manifest = read_json(directory/'artifact-manifest.json')
    require(manifest['release_mode'] == 'production' and manifest['version'] == version and
            manifest['build_number'] == build and manifest['source_revision'] == revision, 'Client manifest identity mismatch')
    lock = read_json(ROOT/'terminal-core.lock.json')
    require(not lock['source_dirty'] and manifest['core_revision'] == metadata['core_revision'] == lock['revision'], 'Core revision mismatch')
    require(len(manifest['artifacts']) == 2 and {row['type'] for row in manifest['artifacts']} == {'apk', 'setup'}, 'Expected one APK and one Windows Setup')
    for row in manifest['artifacts']:
        name = PurePosixPath(row['path']).name
        require(row['path'] == f'artifacts/release/{version}-{build}/{name}', 'Unsafe client artifact path')
        record = next((item for item in records if item['name'] == name), None)
        require(record and row['sha256'] == record['sha256'] and row['size'] == record['size'] and row['version'] == version, 'Client hashes differ from release metadata')
    source = directory/f'TermLink-{version}-{build}-source.zip'
    sidecar = read_json(source.with_name(source.name+'.sha256.json'))
    require(sidecar['archive'] == source.name and sidecar['sha256'] == sha256(source), 'Corresponding-source digest mismatch')
    with zipfile.ZipFile(source) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), 'Duplicate corresponding-source entries')
        for name in names:
            require(not name.startswith('/') and '\\' not in name and ':' not in name and
                    all(part not in ('', '.', '..') for part in name.split('/')), 'Unsafe source archive entry')
        source_manifest = json.loads(archive.read('SOURCE-MANIFEST.json'))
        require(source_manifest['application_revision'] == revision and source_manifest['core_revision'] == lock['revision'] and
                source_manifest['version'] == version and source_manifest['build_number'] == build and
                source_manifest['core_dependencies_vendored'] is True, 'Corresponding-source provenance mismatch')
        require(source_manifest['release_artifact_manifest']['sha256'] == sha256(directory/'artifact-manifest.json'), 'Source is bound to a different client manifest')
        require(set(names) == {row['path'] for row in source_manifest['files']} | {'README-SOURCE.md', 'SOURCE-MANIFEST.json'}, 'Source entry inventory mismatch')
        require(len(names) == len(source_manifest['files'])+2, 'Duplicate source inventory records')
        for row in source_manifest['files']:
            info = archive.getinfo(row['path'])
            require(info.file_size == row['size'] and info.file_size < 512*1024*1024, 'Invalid source file size')
            require((info.external_attr >> 16) & 0o170000 == 0o100000, 'Source entries must be regular files')
            content = archive.read(info)
            require(hashlib.sha256(content).hexdigest() == row['sha256'], f'Source content changed: {row["path"]}')
        for row in lock['artifacts']:
            require(hashlib.sha256(archive.read('termlink/'+row['path'])).hexdigest() == row['sha256'], 'Archived native library differs from lock')
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


def stage(args, api):
    directory = args.directory.resolve()
    manifest = read_json(directory/'artifact-manifest.json')
    version, build, revision = manifest['version'], manifest['build_number'], manifest['source_revision']
    require(not git('status', '--porcelain'), 'Commit source and documentation before staging a release')
    names = expected_names(version, build)-{'SHA256SUMS'}
    checksum_text = ''.join(f'{sha256(directory/name)}  {name}\n' for name in sorted(names))
    (directory/'SHA256SUMS').write_text(checksum_text, encoding='utf-8')
    metadata = dict(schema_version=1, version=version, build_number=build, source_revision=revision,
                    core_revision=manifest['core_revision'], signers=read_json(ROOT/'release/github-release-policy.json'),
                    files=[dict(name=name, size=(directory/name).stat().st_size, sha256=sha256(directory/name))
                           for name in sorted(expected_names(version, build))])
    write_json(directory/'github-release.json', metadata)
    validate_bundle(directory, revision)
    api.require_checks(revision)
    tag = 'v'+version
    api.require_tag(tag, revision)
    releases = api.request('/releases?per_page=100')
    existing = [row for row in releases if row['tag_name'] == tag]
    require(len(existing) <= 1, 'Ambiguous release identity')
    if existing:
        release = existing[0]
        require(release['draft'] and release['target_commitish'] == revision, 'Only the same unpublished draft can be resumed')
        require(release['body'] == args.notes.read_text(encoding='utf-8'), 'Release notes differ from existing draft')
    else:
        release = api.request('/releases', 'POST', dict(tag_name=tag, target_commitish=revision,
            name=f'TermLink {version} (build {build})', body=args.notes.read_text(encoding='utf-8'), draft=True, prerelease=False))
    assets = {row['name']: row for row in api.request(f'/releases/{release["id"]}/assets?per_page=100')}
    upload_names = expected_names(version, build) | {'github-release.json'}
    require(set(assets) <= upload_names, 'Unexpected existing draft attachments; preserve draft and inspect manually')
    for name in sorted(upload_names):
        path = directory/name
        if name in assets:
            require(assets[name]['size'] == path.stat().st_size and assets[name].get('digest') == 'sha256:'+sha256(path), 'Existing draft asset differs; refusing replacement')
            continue
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
    inputs = dict(release_id=str(release['id']), revision=revision, metadata_sha256=sha256(directory/'github-release.json'))
    write_json(directory/'github-dispatch.json', inputs)
    api.request('/actions/workflows/github-release.yml/dispatches', 'POST', dict(ref='main', inputs=inputs))
    print(json.dumps(dict(draft_release_id=release['id'], tag=tag, workflow_dispatched=True)))


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
    require(set(indexed) == expected_names(metadata['version'], metadata['build_number']) | {'github-release.json'}, 'Unexpected remote attachments')
    require(release['tag_name'] == 'v'+metadata['version'] and release['target_commitish'] == args.revision, 'Draft source identity mismatch')
    api.require_tag(release['tag_name'], args.revision)
    for name, asset in indexed.items():
        require(asset['state'] == 'uploaded', 'Incomplete release asset')
        if name != 'github-release.json':
            api.download(asset['id'], directory/name)
    validate_bundle(directory, args.revision, args.metadata_sha256)
    print(json.dumps(dict(verified=True, version=metadata['version'], build_number=metadata['build_number'])))


def publish(args, api):
    metadata = validate_bundle(args.directory.resolve(), args.revision, args.metadata_sha256)
    evidence = read_json(args.directory/'ci-verification.json')
    require(evidence['verified'] is True and evidence['source_revision'] == args.revision and
            evidence['version'] == metadata['version'] and evidence['build_number'] == metadata['build_number'], 'Native signature verification has not passed')
    api.require_checks(args.revision)
    api.require_tag('v'+metadata['version'], args.revision)
    release = api.request(f'/releases/{args.release_id}')
    require(release['tag_name'] == 'v'+metadata['version'] and release['target_commitish'] == args.revision, 'Release identity changed')
    remote = api.request(f'/releases/{args.release_id}/assets?per_page=100')
    records = metadata['files']+[dict(name='github-release.json', sha256=args.metadata_sha256)]
    require(len(remote) == len(records), 'Remote attachments changed during verification')
    for row in records:
        matches = [asset for asset in remote if asset['name'] == row['name']]
        require(len(matches) == 1 and matches[0]['state'] == 'uploaded' and matches[0].get('digest') == 'sha256:'+row['sha256'], 'Remote asset changed during verification')
    if release['draft']:
        release = api.request(f'/releases/{args.release_id}', 'PATCH', dict(draft=False, make_latest='true'))
    require(not release['draft'] and not release['prerelease'], 'Release was not published')
    print(json.dumps(dict(published=True, url=release['html_url'], version=metadata['version'], build_number=metadata['build_number'])))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=['stage', 'receive', 'publish'])
    parser.add_argument('--repository', default=os.environ.get('GITHUB_REPOSITORY', 'myucxy/TermLink'))
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--notes', type=Path)
    parser.add_argument('--release-id', type=int)
    parser.add_argument('--revision')
    parser.add_argument('--metadata-sha256')
    args = parser.parse_args()
    if args.phase == 'stage':
        require(args.notes and args.notes.is_file(), 'Release notes file is required')
    else:
        require(args.release_id and args.release_id > 0 and args.revision and re.fullmatch('[0-9a-f]{40}', args.revision) and
                args.metadata_sha256 and re.fullmatch('[0-9a-f]{64}', args.metadata_sha256), 'Invalid dispatch identity')
    {'stage': stage, 'receive': receive, 'publish': publish}[args.phase](args, GitHub(args.repository))


if __name__ == '__main__':
    main()
