"""Record locked dependencies and copy notices for the modules linked into the Go server."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def json_stream(text):
    decoder = json.JSONDecoder()
    while text.strip():
        text = text.lstrip()
        row, count = decoder.raw_decode(text)
        yield row
        text = text[count:]


def collect(go):
    server = ROOT/'server'
    environment = dict(os.environ, GOTOOLCHAIN='local', GOOS='linux', CGO_ENABLED='0')
    modules = {}
    for architecture in ['amd64', 'arm64']:
        environment['GOARCH'] = architecture
        result = subprocess.run([go, 'list', '-mod=readonly', '-deps', '-json', './cmd/termlink-server'],
                                cwd=server, env=environment, check=True, capture_output=True, encoding='utf-8')
        for package in json_stream(result.stdout):
            module = package.get('Module')
            if module and not module.get('Main'):
                assert not module.get('Replace'), 'Replaced modules need a separate source review'
                modules[module['Path']] = module
    go_root = subprocess.check_output([go, 'env', 'GOROOT'], text=True).strip()
    go_version = subprocess.check_output([go, 'version'], text=True).split()[2]
    notices = ['TermLink Server third-party license bundle',
               'Generated from the packages linked by ./cmd/termlink-server; original texts retained.',
               f'Go standard library and runtime ({go_version})',
               (Path(go_root)/'LICENSE').read_text(encoding='utf-8')]
    inventory = []
    for name, module in sorted(modules.items()):
        source = Path(module['Dir'])
        files = sorted(p for p in source.iterdir() if p.is_file() and
                       re.match(r'(?i)^(license|licence|copying|notice|copyright)(\.|$)', p.name))
        if not files:
            raise ValueError(f'Missing third-party notice: {name}')
        records = []
        for file in files:
            data = file.read_bytes()
            records.append(dict(name=file.name, sha256=digest(data)))
            notices.extend(['\n'+'='*72, f'{name}@{module["Version"]} — {file.name}',
                            data.decode('utf-8-sig').replace('\r\n', '\n')])
        inventory.append(dict(module=name, version=module['Version'],
                              source=f'https://pkg.go.dev/{name}@{module["Version"]}', notices=records))
    npm = []
    lock = json.loads((ROOT/'ai-web/package-lock.json').read_text(encoding='utf-8'))
    for name, row in sorted(lock['packages'].items()):
        if name and not row.get('dev'):
            npm.append(dict(package=name.removeprefix('node_modules/'), version=row['version'],
                            license=row.get('license'), integrity=row.get('integrity')))
    android = set()
    for file in (ROOT/'android').rglob('build.gradle.kts'):
        if 'build' in file.parts:
            continue
        text = file.read_text(encoding='utf-8-sig')
        for coordinate in re.findall(r'(?:api|implementation)\((?:platform\()?"([^"\n]+:[^"\n]+:[^"\n]+)"', text):
            android.add(coordinate)
    central = {}
    for file in [ROOT/'pc-client/Directory.Packages.props']:
        for package in ET.parse(file).iter('PackageVersion'):
            central[package.attrib['Include']] = package.attrib['Version']
    dotnet = set()
    for folder in ['pc-client/src', 'win-agent/src']:
        for file in (ROOT/folder).rglob('*.csproj'):
            for package in ET.parse(file).iter('PackageReference'):
                name = package.attrib['Include']
                dotnet.add((name, package.attrib.get('Version', central.get(name, 'see project lock'))))
    rust = {}
    lock_text = (ROOT/'network-core/Cargo.lock').read_text(encoding='utf-8')
    for package in lock_text.split('[[package]]')[1:]:
        name = re.search(r'(?m)^name = "([^"]+)"', package)
        version = re.search(r'(?m)^version = "([^"]+)"', package)
        checksum = re.search(r'(?m)^checksum = "([^"]+)"', package)
        if name and version and checksum:
            rust[name[1]] = dict(version=version[1], checksum=checksum[1])
    data = dict(schema_version=1,
                scope='Go linked modules and copied notices; locked AI Web runtime packages; declared Android/.NET dependencies; network-core Rust lock. Declaration inventories are not a completed transitive license audit.',
                inputs={p: digest((ROOT/p).read_bytes()) for p in
                        ['server/go.mod', 'server/go.sum', 'ai-web/package-lock.json', 'network-core/Cargo.lock']},
                go_runtime=go_version, go_targets=['linux/amd64', 'linux/arm64'], go_server=inventory, ai_web_runtime=npm,
                android_declared_runtime=sorted(android),
                dotnet_declared_packages=[dict(package=n, version=v) for n, v in sorted(dotnet)],
                network_core_registry_crates=rust)
    return data, '\n\n'.join(notices).replace('\r\n', '\n').rstrip()+'\n'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--go', default='go')
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    data, notices = collect(args.go)
    outputs = {ROOT/'docs/licenses/dependency-inventory.json': json.dumps(data, ensure_ascii=False, indent=2)+'\n',
               ROOT/'server/licenses/THIRD_PARTY_NOTICES.txt': notices}
    for path, text in outputs.items():
        if args.verify:
            assert path.is_file() and path.read_text(encoding='utf-8') == text, f'Outdated dependency inventory: {path}'
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8', newline='\n')
    print(f'Dependency inventory {"verified" if args.verify else "updated"}: '
          f'{len(data["go_server"])} linked Go modules, {len(data["ai_web_runtime"])} AI Web runtime packages')


if __name__ == '__main__':
    main()
