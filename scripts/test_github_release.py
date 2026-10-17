"""Trust-boundary checks; no real API calls, signing keys or published assets."""
import argparse
import hashlib
import json
import io
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
import zipfile

import github_release as release


class ReleaseChecks(unittest.TestCase):
    def test_publish_retains_identity_when_api_defaults_omitted_fields(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            release.write_json(directory/'ci-verification.json', dict(verified=True,
                source_revision='a'*40, version='0.3.6', build_number=136))
            args = argparse.Namespace(directory=directory, revision='a'*40,
                                      metadata_sha256='b'*64, release_id=12, verify_only=False, verification_run=34)
            api = unittest.mock.Mock()
            def request(path, method='GET', value=None):
                if path == '/actions/runs/34':
                    return dict(status='completed', conclusion='success', path='.github/workflows/github-release.yml',
                        event='workflow_dispatch', head_branch='main', display_title='Verify release 12 '+'b'*64)
                if path.endswith('/assets?per_page=100'):
                    return [dict(name='github-release.json', state='uploaded', digest='sha256:'+'b'*64)]
                if method == 'PATCH':
                    tag = value.get('tag_name', 'untagged')
                    return dict(draft=False, prerelease=False, tag_name=tag,
                        target_commitish=value.get('target_commitish', 'main'),
                        html_url='https://github.com/example/repo/releases/tag/'+tag)
                return dict(draft=True, tag_name='v0.3.6', target_commitish='a'*40)
            api.request.side_effect = request
            with patch.object(release, 'validate_bundle', return_value=dict(version='0.3.6', build_number=136, files=[])):
                output = io.StringIO()
                with redirect_stdout(output):
                    release.publish(args, api)
            self.assertTrue(json.loads(output.getvalue())['url'].endswith('/v0.3.6'))
            api.request.reset_mock()
            api.request.side_effect = lambda *args, **kwargs: dict(status='completed', conclusion='success',
                path='.github/workflows/github-release.yml', event='workflow_dispatch', head_branch='main',
                display_title='Verify release 12 '+'c'*64)
            with patch.object(release, 'validate_bundle', return_value=dict(version='0.3.6', build_number=136, files=[])):
                with self.assertRaises(ValueError):
                    release.publish(args, api)
            self.assertEqual(api.request.call_count, 1)

    def test_release_filenames_reject_path_traversal_and_invalid_build(self):
        for version, build in [('../escape', 136), ('0.3.6', True), ('0.3.6', 65536)]:
            with self.assertRaises(ValueError):
                release.expected_names(version, build)

    def test_native_evidence_is_required_before_publish(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            (directory/'ci-verification.json').write_text(json.dumps(dict(verified=False)))
            api = unittest.mock.Mock()
            args = argparse.Namespace(directory=directory, revision='a'*40, metadata_sha256='b'*64, verify_only=True)
            with patch.object(release, 'validate_bundle', return_value=dict(version='0.3.6', build_number=136)):
                with self.assertRaises(ValueError):
                    release.publish(args, api)
            api.request.assert_not_called()

    def test_download_never_forwards_api_authorization_to_asset_host(self):
        with tempfile.TemporaryDirectory() as root, patch.dict('os.environ', GH_TOKEN='fixture'):
            api = release.GitHub('example/repo')
            redirect = HTTPError(api.base, 302, 'redirect',
                                 {'Location': 'https://release-assets.githubusercontent.com/signed-asset'}, None)
            opener = unittest.mock.Mock()
            response = unittest.mock.MagicMock()
            opener.open.side_effect = [redirect, response]
            with patch.object(release, 'build_opener', return_value=opener), patch.object(release.shutil, 'copyfileobj'):
                api.download(12, Path(root)/'asset')
            first, second = [call.args[0] for call in opener.open.call_args_list]
            self.assertTrue(first.has_header('Authorization'))
            self.assertFalse(second.has_header('Authorization'))

    def test_bundle_rejects_changed_binary_metadata_and_source(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            policy = root/'release/github-release-policy.json'
            policy.parent.mkdir()
            release.write_json(policy, dict(android_signer_sha256='c'*64, windows_signer_sha1='d'*40))
            core = 'e'*40
            library = b'fixture native binary'
            lock = dict(revision=core, source_dirty=False, artifacts=[dict(path='native/library.dll', sha256=hashlib.sha256(library).hexdigest())])
            release.write_json(root/'terminal-core.lock.json', lock)
            directory = root/'artifacts/release/0.3.6-136'
            directory.mkdir(parents=True)
            artifacts = []
            for kind, name in [('apk', 'TermLink-Android-0.3.6-136.apk'), ('setup', 'TermLink-Setup-0.3.6.136-x64.exe')]:
                (directory/name).write_bytes(b'fixture '+kind.encode())
                artifacts.append(dict(path='artifacts/release/0.3.6-136/'+name, type=kind, version='0.3.6',
                                      size=(directory/name).stat().st_size, sha256=release.sha256(directory/name)))
            manifest = dict(version='0.3.6', build_number=136, source_revision='a'*40, core_revision=core,
                            release_mode='production', artifacts=artifacts)
            release.write_json(directory/'artifact-manifest.json', manifest)
            source = directory/'TermLink-0.3.6-136-source.zip'
            with zipfile.ZipFile(source, 'w') as archive:
                row = dict(path='termlink/native/library.dll', size=len(library), sha256=hashlib.sha256(library).hexdigest())
                info = zipfile.ZipInfo(row['path'])
                info.external_attr = 0o100644 << 16
                archive.writestr(info, library)
                archive.writestr('README-SOURCE.md', 'fixture source')
                archive.writestr('SOURCE-MANIFEST.json', json.dumps(dict(application_revision='a'*40,
                    core_revision=core, version='0.3.6', build_number=136, core_dependencies_vendored=True,
                    release_artifact_manifest=dict(sha256=release.sha256(directory/'artifact-manifest.json')), files=[row])))
            release.write_json(source.with_name(source.name+'.sha256.json'), dict(archive=source.name, sha256=release.sha256(source)))
            (directory/'SHA256SUMS').write_text('fixture checksums')
            metadata = dict(schema_version=1, version='0.3.6', build_number=136, source_revision='a'*40,
                            core_revision=core, signers=release.read_json(policy), files=[dict(name=name,
                            size=(directory/name).stat().st_size, sha256=release.sha256(directory/name))
                            for name in sorted(release.expected_names('0.3.6', 136))])
            release.write_json(directory/'github-release.json', metadata)
            def fixture_git(*arguments):
                if arguments[0] == 'show':
                    return (root/arguments[1].split(':', 1)[1]).read_text()
                if arguments[0] == 'ls-tree':
                    blob = hashlib.sha1(b'blob '+str(len(library)).encode()+b'\0'+library).hexdigest()
                    return '100644 blob '+blob+'\tnative/library.dll\0'
                if arguments == ('rev-parse', 'HEAD'):
                    return 'f'*40
                return 'a'*40
            with patch.object(release, 'ROOT', root), patch.object(release, 'git', side_effect=fixture_git):
                self.assertEqual(release.validate_bundle(directory, 'a'*40)['version'], '0.3.6')
                digest = release.sha256(directory/'github-release.json')
                with self.assertRaises(ValueError):
                    release.validate_bundle(directory, 'a'*40, '0'*64)
                (directory/artifacts[0]['path'].split('/')[-1]).write_bytes(b'tampered')
                with self.assertRaises(ValueError):
                    release.validate_bundle(directory, 'a'*40, digest)


if __name__ == '__main__':
    unittest.main()
