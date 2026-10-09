"""Sequential local acceptance checks. Long load profiles and Cognito are never implicit."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / 'expenses-tests'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--zap', action='store_true', help='Include passive ZAP; findings produce a failed aggregate')
    options = parser.parse_args()
    os.umask(0o077)
    output = TESTS / 'reports' / ('checks-' + uuid.uuid4().hex[:12]); output.mkdir()
    commands = [
        ('inventory', [sys.executable, 'scripts/verify_inventory.py'], TESTS),
        ('unit-backend', ['dotnet', 'test', 'Expenses.sln', '-c', 'Release', '--logger', 'trx;LogFileName=unit.trx', '--results-directory', str(output)], ROOT / 'expenses-service'),
        ('unit-frontend', ['npm', 'test'], ROOT / 'expenses-ui'),
        ('lint-frontend', ['npm', 'run', 'lint'], ROOT / 'expenses-ui'),
        ('typecheck-tests', ['npm', 'run', 'typecheck'], TESTS),
        ('integration', [sys.executable, 'scripts/integration.py'], TESTS),
        ('schema', [sys.executable, 'scripts/schema.py'], TESTS),
        ('api-browser', [sys.executable, 'scripts/lab.py', 'all'], TESTS),
        ('audit', [sys.executable, 'security/audit.py'], TESTS),
    ]
    if options.zap: commands.append(('zap-passive', [sys.executable, 'scripts/lab.py', 'security'], TESTS))
    results = []
    for name, command, cwd in commands:
        print('Running ' + name, flush=True)
        with (output / (name + '.log')).open('w') as log:
            result = subprocess.run(command, cwd=cwd, stdout=log, stderr=subprocess.STDOUT)
        artifacts = [str(Path(line.removeprefix('Report: ')).parent.relative_to(TESTS))
                     for line in (output / (name + '.log')).read_text().splitlines()
                     if line.startswith('Report: ') and line.endswith('summary.json')]
        results.append({'suite': name, 'exitCode': result.returncode, 'reports': artifacts, 'log': str((output / (name + '.log')).relative_to(TESTS))})
        (output / 'summary.json').write_text(json.dumps({'identity': 'simulated', 'externalExecuted': False,
             'status': 'running' if len(results) < len(commands) else 'passed' if all(item['exitCode'] == 0 for item in results) else 'failed', 'results': results}, indent=2) + '\n')
    print('Report: ' + str(output / 'summary.json'))
    return int(any(result['exitCode'] for result in results))


if __name__ == '__main__':
    sys.exit(main())
