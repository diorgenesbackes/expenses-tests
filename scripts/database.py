"""Disposable PostgreSQL owned by this run; never connects to the development container."""
from contextlib import contextmanager
from pathlib import Path
import json
import os
import secrets
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / 'expenses-tests'
POSTGRES_IMAGE = 'postgres:17.11-alpine'


def run(args, *, cwd=TESTS, env=None, input=None):
    result = subprocess.run(args, cwd=cwd, env=env, input=input, text=True, capture_output=True)
    if result.returncode:
        # Credentials are passed via stdin/environment, never command arguments.
        raise RuntimeError(f'{args[0]} failed:\n{result.stdout}\n{result.stderr}')
    return result.stdout


@contextmanager
def database():
    run_id = uuid.uuid4().hex[:12]
    name = 'expenses-tests-' + run_id
    db = 'expenses_users_check_' + run_id
    password = secrets.token_hex(24)
    state = TESTS / '.local' / run_id
    state.mkdir(parents=True, mode=0o700)
    secret = state / 'postgres.env'
    secret.write_text(f'POSTGRES_USER=runner\nPOSTGRES_PASSWORD={password}\nPOSTGRES_DB={db}\n')
    secret.chmod(0o600)
    container = None
    try:
        container = run(['docker', 'run', '-d', '--name', name, '--label', 'expenses.tests.run=' + run_id,
                         '--env-file', str(secret), '--publish', '127.0.0.1::5432',
                         '--memory', '512m', '--cpus', '1', POSTGRES_IMAGE]).strip()
        for _ in range(60):
            ready = subprocess.run(['docker', 'exec', container, 'pg_isready', '-U', 'runner', '-d', db],
                                   capture_output=True)
            if ready.returncode == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError('Isolated PostgreSQL did not become ready within 60 seconds.')
        bindings = json.loads(run(['docker', 'inspect', container]))[0]['NetworkSettings']['Ports']['5432/tcp']
        port = bindings[0]['HostPort']
        env = {key: value for key, value in os.environ.items()
               if not key.upper().startswith(('CONNECTIONSTRINGS__', 'CONNECTIONSTRINGS:', 'IDENTITY__', 'IDENTITY:'))}
        # Local defaults cannot opt into Cognito by an inherited shell variable.
        env['EXPENSES_RUN_COGNITO_TESTS'] = '0'
        env.update(LIQUIBASE_COMMAND_URL=f'jdbc:postgresql://127.0.0.1:{port}/{db}',
                   LIQUIBASE_COMMAND_USERNAME='runner', LIQUIBASE_COMMAND_PASSWORD=password,
                   EXPENSES_TEST_CONNECTION_STRING=f'Host=127.0.0.1;Port={port};Database={db};Username=runner;Password={password};Timeout=5')
        yield {'id': run_id, 'container': container, 'database': db, 'env': env}
    finally:
        if container:
            label = run(['docker', 'inspect', '--format', '{{index .Config.Labels "expenses.tests.run"}}', container]).strip()
            if label != run_id:
                raise RuntimeError('Cleanup refused: container ownership label differs.')
            run(['docker', 'rm', '-f', '-v', container])
            print(f'Isolated container {name} removed; development database untouched.', flush=True)
        secret.unlink(missing_ok=True)
        state.rmdir()


def migrate(env, *args):
    return run(['liquibase', '--defaults-file=config/liquibase.properties', *(args or ('update',))],
               cwd=ROOT / 'expenses-liquibase', env=env)


def query(lab, sql):
    return run(['docker', 'exec', '-i', lab['container'], 'psql', '-X', '-U', 'runner',
                '-d', lab['database'], '-v', 'ON_ERROR_STOP=1', '-At'], input=sql).strip()
