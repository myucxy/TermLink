"""Scan committed files only; accept reviewed fixtures only at the exact content hash."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def classify(findings, baseline, snapshot):
    reviewed = {(row['rule'], row['path'], row['line'], row['column']): row['sha256']
                for row in baseline}
    unexpected = []
    for item in findings:
        name = item['File'].replace('\\', '/')
        path = Path(name)
        if path.is_absolute():
            name = path.relative_to(snapshot).as_posix()
        key = (item['RuleID'], name, item['StartLine'], item['StartColumn'])
        source = (snapshot/name).resolve()
        if not source.is_relative_to(snapshot.resolve()):
            raise ValueError('Scanner result escapes the source snapshot')
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        if reviewed.get(key) != digest:
            unexpected.append(f'{name}:{item["StartLine"]}: {item["RuleID"]}')
    return unexpected


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--gitleaks', default='gitleaks')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            p = root/'fixture.txt'
            p.write_text('reviewed test value', encoding='utf-8')
            row = dict(RuleID='test', File='fixture.txt', StartLine=1, StartColumn=1)
            baseline = [dict(rule='test', path='fixture.txt', line=1, column=1,
                             sha256=hashlib.sha256(p.read_bytes()).hexdigest())]
            assert not classify([row], baseline, root)
            p.write_text('changed test value', encoding='utf-8')
            assert classify([row], baseline, root)
        print('Secret baseline checker self-test passed')
    baseline = json.loads((ROOT/'.gitleaks-fixtures.json').read_text(encoding='utf-8'))
    with tempfile.TemporaryDirectory(prefix='termlink-secret-scan-') as directory:
        temp = Path(directory)
        archive, snapshot, report = temp/'tracked.tar', temp/'source', temp/'findings.json'
        subprocess.run(['git', '-C', str(ROOT), 'archive', '--format=tar',
                        f'--output={archive}', 'HEAD'], check=True)
        snapshot.mkdir()
        with tarfile.open(archive) as stream:
            for member in stream.getmembers():
                target = (snapshot/member.name).resolve()
                if not target.is_relative_to(snapshot.resolve()) or member.issym() or member.islnk():
                    raise ValueError('Links or escaping paths are not accepted in scan snapshots')
            stream.extractall(snapshot)
        process = subprocess.run([args.gitleaks, 'dir', str(snapshot), '--redact', '--no-banner',
                                  '--max-archive-depth', '0', '--report-format', 'json',
                                  '--report-path', str(report)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if process.returncode not in (0, 1) or not report.exists():
            raise SystemExit('Gitleaks failed; no successful scan result is available')
        findings = json.loads(report.read_text(encoding='utf-8-sig'))
        unexpected = classify(findings, baseline, snapshot)
        if unexpected:
            raise SystemExit('Unreviewed secret candidates (values withheld):\n'+'\n'.join(unexpected))
        print(f'Secret scan passed: {len(findings)} exact reviewed fixture matches; no unreviewed candidates')


if __name__ == '__main__':
    main()
