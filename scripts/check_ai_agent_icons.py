"""Keep Android product icons aligned with the host's discoverable AI Agents."""
import argparse
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
ENTRY = re.compile(r'^\s*"([a-z0-9-]+)"\s+to\s+AiAgentBrand\("[^"\n]+",\s*R\.drawable\.([a-z0-9_]+)\)', re.M)


def check(discovery, brands, resources):
    agents = set(re.findall(r'new\s+AiAgentInstallation\(\s*"([a-z0-9-]+)"', discovery))
    if not agents:
        return ['No host Agent declarations found; update this checker for the new discovery format.']
    registry = brands.split('internal val aiAgentBrands = mapOf(', 1)
    if len(registry) != 2 or '\n)' not in registry[1]:
        return ['Android Agent brand registry format changed; update this checker.']
    entries = ENTRY.findall(registry[1].split('\n)', 1)[0])
    errors = []
    if len(entries) != len({agent for agent, _ in entries}):
        errors.append('Duplicate Android Agent brand IDs.')
    icons = dict(entries)
    if len(icons.values()) != len(set(icons.values())):
        errors.append('Each Agent must declare its own product icon resource.')
    for agent in sorted(agents):
        if agent not in icons:
            errors.append(f'{agent}: Android product name and icon are missing from aiAgentBrands.')
        elif icons[agent] not in resources:
            errors.append(f'{agent}: packaged icon {icons[agent]} is missing.')
    return errors


def self_test():
    discovery = 'new AiAgentInstallation("fixture", "Fixture")'
    brands = 'internal val aiAgentBrands = mapOf(\n "fixture" to AiAgentBrand("Fixture", R.drawable.fixture),\n)'
    assert not check(discovery, brands, {'fixture'})
    assert check(discovery + '\nnew AiAgentInstallation("added", "Added")', brands, {'fixture'})
    assert check(discovery, brands, set())
    assert check(discovery, brands.replace('\n)', '\n "fixture" to AiAgentBrand("Duplicate", R.drawable.fixture),\n)'), {'fixture'})
    assert check(discovery, brands.replace('\n)', '\n "other" to AiAgentBrand("Other", R.drawable.fixture),\n)'), {'fixture'})
    assert check('', brands, {'fixture'})
    assert check(discovery, '', {'fixture'})
    print('Agent icon checker self-test passed: missing Agent, missing resource and duplicate detection.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    if parser.parse_args().self_test:
        self_test()
    discovery = '\n'.join(p.read_text(encoding='utf-8-sig') for p in (ROOT/'win-agent/src/Common').glob('*Ai*Discovery*.cs'))
    brands = (ROOT/'android/app/src/main/kotlin/dev/termlink/product/AiAgentGroups.kt').read_text(encoding='utf-8-sig')
    resources = {p.stem for directory in (ROOT/'android/app/src/main/res').glob('drawable*') for p in directory.iterdir() if p.is_file()}
    errors = check(discovery, brands, resources)
    for directory in (ROOT/'android/app/src/main/res').glob('drawable*'):
        for path in directory.glob('ai_agent_*.xml'):
            try:
                if ET.parse(path).getroot().tag != 'vector':
                    errors.append(f'{path.name}: expected an Android VectorDrawable.')
            except ET.ParseError:
                errors.append(f'{path.name}: malformed icon XML.')
    if errors:
        raise SystemExit('\n'.join(errors))
    agents = sorted(set(re.findall(r'new\s+AiAgentInstallation\(\s*"([a-z0-9-]+)"', discovery)))
    print('Agent icon coverage passed: ' + ', '.join(agents))


if __name__ == '__main__':
    main()
