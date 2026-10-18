"""Offline checks for maintained public entry points, not historical evidence paths."""
import argparse
from pathlib import Path
import re
import tempfile
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[1]
ENTRY_POINTS = [
    'README.md', 'README.en.md', 'CONTRIBUTING.md', 'SECURITY.md', 'LICENSE-STATUS.md',
    'docs/README.md', 'docs/RELEASE.md', 'docs/RELEASE_RUNBOOK.md',
    'docs/architecture/CURRENT.md', 'docs/guides/QUICK_START.md',
    'docs/development/BUILDING.md', 'docs/development/REPOSITORY_LAYOUT.md',
    'docs/development/OPEN_SOURCE_READINESS.md', 'docs/development/GITHUB_RELEASE.md',
    'docs/development/ANDROID_ADAPTIVE_SCREENS.md',
    'docs/RELEASE_0.3.6_136.md', 'docs/releases/README.md',
    'docs/archive/README.md', 'docs/media/README.md',
    'android/README.md', 'pc-client/README.md', 'win-agent/README.md',
    'server/README.md', 'ai-web/README.md', 'protocol/README.md',
]


def headings(text):
    counts, result = {}, set()
    for label in re.findall(r'^#{1,6}\s+(.+)$', text, re.M):
        label = re.sub(r'[`*]', '', label).strip().lower()
        slug = re.sub(r'[^\w\- ]', '', label).replace(' ', '-')
        n = counts.get(slug, 0)
        counts[slug] = n + 1
        result.add(slug + (f'-{n}' if n else ''))
    result.update(re.findall(r'\bid=["\']([^"\']+)["\']', text))
    return result


def check(path):
    text = path.read_text(encoding='utf-8-sig')
    text = re.sub(r'^```[^\n]*\n.*?^```\s*$', '', text, flags=re.M | re.S)
    targets = re.findall(r'\]\(([^)]+)\)', text)
    targets += re.findall(r'\b(?:href|src|poster)=["\']([^"\']+)["\']', text)
    errors = []
    for raw in targets:
        target = unquote(raw.strip().split(' "', 1)[0])
        if re.match(r'^[a-zA-Z][a-zA-Z0-9+.-]*:', target):
            continue
        relative, _, anchor = target.partition('#')
        relative = relative.split('?', 1)[0]
        destination = (path.parent / relative).resolve() if relative else path.resolve()
        if not destination.exists():
            errors.append(f'{path}: missing target {raw}')
        elif anchor and destination.suffix.lower() == '.md':
            if anchor not in headings(destination.read_text(encoding='utf-8-sig')):
                errors.append(f'{path}: missing heading {raw}')
    return errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'目标.md').write_text('# 中文标题\n', encoding='utf-8')
            page = root/'README.md'
            page.write_text('[好](%E7%9B%AE%E6%A0%87.md#中文标题)\n```text\n[示例](missing.md)\n```\n', encoding='utf-8')
            assert not check(page)
            page.write_text('[坏](目标.md#不存在)\n<img src="missing.png">', encoding='utf-8')
            assert len(check(page)) == 2
        print('Public documentation checker self-test passed')
    files = [ROOT/p for p in ENTRY_POINTS if (ROOT/p).exists()]
    files += sorted((ROOT/'docs/media/feature-demos').rglob('README*.md'))
    errors = [error for path in files for error in check(path)]
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Public documentation checked: {len(files)} entry points')


if __name__ == '__main__':
    main()
