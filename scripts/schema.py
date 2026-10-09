"""Complete Liquibase lifecycle, using only a private disposable container."""
from database import TESTS, database, migrate, query as sql


def main():
    with database() as lab:
        def query(statement):
            return sql(lab, statement)

        def liquibase(*args):
            migrate(lab['env'], *args)

        liquibase('validate')
        liquibase('update-count', '--count=8')
        query("INSERT INTO identity.users(cognito_sub,email) VALUES ('upgrade-sentinel','upgrade@example.test')")
        liquibase('update')
        assert query('SELECT count(*) FROM public.databasechangelog') == '10'
        assert query("SELECT count(*) FROM identity.users WHERE cognito_sub='upgrade-sentinel'") == '1'
        print('PASS: upgrade from sprint-1 preserves existing profiles (10 changeSets).', flush=True)
        liquibase('update')
        assert query('SELECT count(*) FROM public.databasechangelog') == '10'
        print('PASS: repeated update does not reapply changeSets.', flush=True)
        query((TESTS / 'integration/schema/schema_checks.sql').read_text())
        query((TESTS / 'integration/schema/registration_checks.sql').read_text())
        query((TESTS / 'integration/schema/session_checks.sql').read_text())
        print('PASS: relational integrity, lifecycle and catalog checks.', flush=True)
        liquibase('rollback-count', '--count=2')
        assert query("SELECT count(*) FROM pg_tables WHERE schemaname IN ('identity','household','finance','income')") == '33'
        assert query("SELECT count(*) FROM identity.users WHERE cognito_sub='upgrade-sentinel'") == '1'
        liquibase('update')
        print('PASS: sprint-2/3 rollback/reapply preserves sprint-1 and existing users.', flush=True)
        liquibase('rollback-count', '--count=10')
        assert query("SELECT count(*) FROM pg_namespace WHERE nspname IN ('identity','household','finance','income')") == '0'
        assert query('SELECT count(*) FROM public.databasechangelog') == '0'
        print('PASS: complete rollback removes the four application schemas.', flush=True)
        liquibase('update')
        assert query("SELECT count(*) FROM pg_tables WHERE schemaname IN ('identity','household','finance','income')") == '36'
        assert query('SELECT count(*) FROM public.databasechangelog') == '10'
        query((TESTS / 'integration/schema/registration_checks.sql').read_text())
        query((TESTS / 'integration/schema/session_checks.sql').read_text())
        print('PASS: update after rollback recreates all 36 tables.', flush=True)


if __name__ == '__main__':
    main()
