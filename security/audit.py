"""Audit dependencies and source secrets locally; reports are private and redacted."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / 'expenses-tests'


def main():
    os.umask(0o077)
    output = TESTS / 'reports' / ('security-' + uuid.uuid4().hex[:12])
    output.mkdir()
    findings = []
    commands = [
        ('npm-ui', ['npm', 'audit', '--json'], ROOT / 'expenses-ui'),
        ('npm-tests', ['npm', 'audit', '--json'], TESTS),
        ('nuget', ['dotnet', 'list', str(TESTS / 'Expenses.Tests.sln'), 'package', '--vulnerable', '--include-transitive', '--format', 'json'], ROOT),
    ]
    for name, command, cwd in commands:
        result = subprocess.run(command, cwd=cwd, capture_output=True, text=True)
        (output / (name + '.json')).write_text(result.stdout)
        if result.stderr: (output / (name + '.stderr')).write_text(result.stderr)
        try:
            data = json.loads(result.stdout)
            count = data.get('metadata', {}).get('vulnerabilities', {}).get('total', 0) if name.startswith('npm') else sum(
                len(package.get('vulnerabilities', [])) for project in data.get('projects', [])
                for framework in project.get('frameworks', []) for group in ['topLevelPackages', 'transitivePackages']
                for package in framework.get(group, []))
            valid = not data.get('error') and not data.get('errors') and not data.get('problems')
        except ValueError:
            count, valid = 0, False
        findings.append({'tool': name, 'exitCode': result.returncode, 'vulnerabilities': count,
                         'status': 'passed' if valid and count == 0 and result.returncode == 0 else 'failed'})
    if not shutil.which('gitleaks'):
        findings.append({'tool': 'gitleaks', 'status': 'not-executed', 'reason': 'Install pinned Gitleaks 8.30.1.'})
    else:
        version = subprocess.check_output(['gitleaks', 'version'], text=True).strip()
        if version != '8.30.1': raise RuntimeError('Expected Gitleaks 8.30.1; review the pinned version before updating.')
        # Scan a source snapshot, never private configuration, build output or generated test credentials.
        with tempfile.TemporaryDirectory(prefix='expenses-source-audit-') as temporary:
            snapshot = Path(temporary)
            count = 0
            for project in ['expenses-service', 'expenses-ui', 'expenses-tests', 'expenses-liquibase', 'expenses-infrastructure', 'expenses-docs']:
                files = subprocess.check_output(['rg', '--files', '--hidden', '--no-require-git', '-g', '!.git', '-g', '!dist', '-g', '!reports', '-g', '!*.local.properties', '-g', '!.env*'], cwd=ROOT / project, text=True)
                for filename in files.splitlines():
                    source = ROOT / project / filename
                    if source.is_symlink() or source.stat().st_size > 2_000_000: continue
                    target = snapshot / project / filename
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source, target)
                    count += 1
            result = subprocess.run(['gitleaks', 'dir', str(snapshot), '--redact=100', '--config', str(TESTS / 'security/gitleaks.toml'), '--no-banner', '--report-format', 'json', '--report-path', str(output / 'gitleaks.json')], capture_output=True, text=True)
            findings.append({'tool': 'gitleaks', 'version': version, 'filesScanned': count, 'exitCode': result.returncode, 'status': 'passed' if result.returncode == 0 else 'failed'})
    (output / 'summary.json').write_text(json.dumps({'scope': 'local source and dependencies; no active scan or AWS', 'findings': findings}, indent=2) + '\n')
    for finding in findings: print(f"{finding['tool']}: {finding['status']}")
    print(f'Report: {output / "summary.json"}')
    return 0 if all(finding['status'] == 'passed' for finding in findings) else 1


if __name__ == '__main__':
    sys.exit(main())
