"""Explicit opt-in only: isolated local PostgreSQL with the configured real Cognito pool."""
import os
from database import TESTS, database, migrate, run


def main():
    if os.environ.get('EXPENSES_RUN_COGNITO_TESTS') != '1' or not os.environ.get('EXPENSES_COGNITO_TEST_POOL_ID'):
        raise SystemExit('External suite disabled. Set EXPENSES_RUN_COGNITO_TESTS=1 and EXPENSES_COGNITO_TEST_POOL_ID explicitly after reviewing external/README.md.')
    with database() as lab:
        env = lab['env'] | {'EXPENSES_RUN_COGNITO_TESTS': '1'}
        migrate(env)
        output = TESTS / 'reports' / ('external-' + lab['id'])
        output.mkdir(mode=0o700)
        print(run(['dotnet', 'test', 'external/Expenses.ExternalTests.csproj', '-c', 'Release',
                   '--logger', 'trx;LogFileName=external.trx', '--results-directory', str(output)], env=env))


if __name__ == '__main__':
    main()
