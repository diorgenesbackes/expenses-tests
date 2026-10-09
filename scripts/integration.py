"""Run all 49 local integration cases with a newly migrated PostgreSQL container."""
from pathlib import Path
import json
import platform
import time
import xml.etree.ElementTree as ET
from provenance import source_identity
from database import TESTS, database, migrate, run


def main():
    started = time.time()
    with database() as lab:
        output = TESTS / 'reports' / lab['id']
        output.mkdir(parents=True, mode=0o700)
        report = {'sourceIdentity': source_identity(), 'runId': lab['id'], 'identity': 'simulated', 'kind': 'integration',
                  'machine': platform.platform(), 'expected': 49, 'externalExecuted': False,
                  'status': 'failed'}
        try:
            migrate(lab['env'])
            print('Isolated schema ready; running 49 local integration cases.', flush=True)
            print(run(['dotnet', 'test', 'integration/Expenses.IntegrationTests.csproj', '-c', 'Release',
                       '--logger', 'trx;LogFileName=integration.trx', '--results-directory', str(output)], env=lab['env']))
            tree = ET.parse(output / 'integration.trx')
            counters = tree.find('.//{*}Counters').attrib
            report['counters'] = counters
            if int(counters['passed']) != 49 or int(counters['total']) != 49:
                raise RuntimeError('Expected exactly 49 passing tests, with no skipped cases. Review the inventory before changing this guard.')
            report['status'] = 'passed'
        finally:
            report['durationSeconds'] = round(time.time() - started, 2)
            (output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(f'Report: {output / "summary.json"}')


if __name__ == '__main__':
    main()
