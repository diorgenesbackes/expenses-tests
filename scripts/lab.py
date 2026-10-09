"""Owned local HTTPS lab: PostgreSQL container, two Kestrel processes, local identity and proxy."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import socket
import ssl
import sys
import subprocess
import time
import threading
import urllib.request
from provenance import source_identity
from database import ROOT, TESTS, database, migrate, run


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def wait_ready(url, processes, *, headers=None, context=None):
    last_error = None
    for _ in range(100):
        if any(process.poll() is not None for process in processes):
            raise RuntimeError('A laboratory process exited during startup; inspect the private run logs.')
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers or {}), timeout=1, context=context) as response:
                if response.status == 200:
                    return
        except (OSError, ValueError) as error:
            last_error = error
        time.sleep(.2)
    raise RuntimeError('Laboratory readiness timeout: ' + url + ': ' + str(last_error))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('suite', choices=['api', 'e2e', 'all', 'performance', 'security'])
    parser.add_argument('--active', action='store_true', help='Run scoped active ZAP scan in addition to passive checks')
    parser.add_argument('--profile', default='smoke', choices=['smoke', 'baseline', 'load', 'journey', 'spike', 'soak'])
    options = parser.parse_args()
    if not run(['node', '--version']).strip().startswith('v24.'):
        raise SystemExit('Use Node 24 as specified by the project guidelines.')
    os.umask(0o077)
    print(run(['dotnet', 'build', 'environment/Host/Expenses.LabHost.csproj', '-c', 'Release', '--nologo']))
    print(run(['npm', 'run', 'build'], cwd=ROOT / 'expenses-ui'))
    with database() as lab:
        migrate(lab['env'])
        state = TESTS / '.local' / ('network-' + lab['id'])
        state.mkdir(mode=0o700)
        output = TESTS / 'reports' / lab['id']
        output.mkdir(mode=0o700)
        processes = []
        logs = []
        report = {'sourceIdentity': source_identity(), 'runId': lab['id'], 'identity': 'simulated', 'suite': options.suite, 'status': 'failed',
                  'topology': '2 loopback Kestrel processes + HTTPS proxy + shared local identity + disposable PostgreSQL',
                  'machine': platform.platform(), 'apiResourceLimits': 'host processes, no hard cgroup limits',
                  'databaseLimits': {'cpus': 1, 'memory': '512m'}, 'tlsBrowserTrust': 'per-run SPKI pin; no global TLS bypass',
                  'externalExecuted': False, 'processRoles': {}}
        stopped = threading.Event()
        monitor = None
        started = time.time()
        try:
            ca = state / 'ca.pem'; ca_key = state / 'ca.key'; cert = state / 'server.pem'; key = state / 'server.key'
            run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(ca_key), '-out', str(ca),
                 '-days', '2', '-subj', '/CN=Expenses disposable lab CA', '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign'])
            run(['openssl', 'req', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key), '-out', str(state / 'server.csr'), '-subj', '/CN=localhost'])
            (state / 'tls.ext').write_text('subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nauthorityKeyIdentifier=keyid,issuer\n')
            run(['openssl', 'x509', '-req', '-in', str(state / 'server.csr'), '-CA', str(ca), '-CAkey', str(ca_key),
                 '-CAcreateserial', '-out', str(cert), '-days', '2', '-extfile', str(state / 'tls.ext')])
            run(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', str(state / 'identity.key')])
            run(['openssl', 'pkey', '-in', str(state / 'identity.key'), '-pubout', '-out', str(state / 'identity.pub')])
            public = run(['openssl', 'x509', '-in', str(cert), '-pubkey', '-noout'])
            der = subprocess.run(['openssl', 'pkey', '-pubin', '-outform', 'DER'], input=public.encode(), capture_output=True, check=True).stdout
            # Start ports are dynamic. A bind collision fails the run; it never reuses an existing service.
            identity, first, second, proxy = [free_port() for _ in range(4)]
            env = lab['env'].copy()
            env.update(LAB_DATABASE_CONTAINER=lab['container'], LAB_RUN_ID=lab['id'], LAB_IDENTITY_PORT=str(identity), LAB_CONTROL_KEY=secrets.token_hex(32),
                       LAB_PRIVATE_KEY=str(state / 'identity.key'), LAB_PUBLIC_KEY=str(state / 'identity.pub'),
                       LAB_HMAC=base64.b64encode(secrets.token_bytes(32)).decode(), LAB_KEYRING=str(state / 'keyring'),
                       LAB_API_ROOT=str(ROOT / 'expenses-service/Expenses.Api'), LAB_UI_ROOT=str(ROOT / 'expenses-ui/dist'),
                       LAB_HTTPS_PORT=str(proxy), LAB_BACKENDS=f'{first},{second}', LAB_TLS_KEY=str(key), LAB_TLS_CERT=str(cert),
                       LAB_BASE_URL=f'https://localhost:{proxy}', NODE_EXTRA_CA_CERTS=str(ca), SSL_CERT_FILE=str(ca),
                       LAB_TLS_SPKI=base64.b64encode(hashlib.sha256(der).digest()).decode(), ASPNETCORE_ENVIRONMENT='Laboratory', DOTNET_ENVIRONMENT='Laboratory')
            # Never make AWS credentials part of the local lab's process environment.
            env = {name: value for name, value in env.items() if not name.upper().startswith(('AWS_', 'AWS:', 'COGNITO__', 'COGNITO:', 'EXPENSES_COGNITO_'))}
            host = TESTS / 'environment/Host/bin/Release/net10.0/Expenses.LabHost.dll'
            def start(name, command, overrides=None):
                log = (output / (name + '.log')).open('w'); logs.append(log)
                child = subprocess.Popen(command, cwd=TESTS, env=env | (overrides or {}), stdout=log, stderr=subprocess.STDOUT)
                processes.append(child)
                report['processRoles'][name] = child.pid
            start('identity', ['dotnet', str(host), 'identity'])
            wait_ready(f'http://127.0.0.1:{identity}/ready', processes, headers={'X-Lab-Key': env['LAB_CONTROL_KEY']})
            for number, port in enumerate([first, second], 1):
                start('api-' + str(number), ['dotnet', str(host)], {'LAB_API_PORT': str(port)})
                # Forwarded HTTPS is accepted only on loopback; health verifies actual migrated database access.
                wait_ready(f'http://127.0.0.1:{port}/health/db', processes, headers={'X-Forwarded-Proto': 'https'})
            start('proxy', ['node', str(TESTS / 'environment/proxy.mjs')])
            context = ssl.create_default_context(cafile=str(ca))
            wait_ready(env['LAB_BASE_URL'] + '/api/health/db', processes, context=context)
            report['versions'] = {tool: run(args).strip() for tool, args in {
                'dotnet': ['dotnet', '--version'], 'node': ['node', '--version'], 'playwright': ['npx', '--no-install', 'playwright', '--version']}.items()}
            report['postgresImage'] = run(['docker', 'inspect', '--format', '{{.Image}}', lab['container']]).strip()
            print(f'HTTPS lab ready ({lab["id"]}); running {options.suite}, identity=simulated.', flush=True)
            def collect_resources():
                with (output / 'resources.jsonl').open('w') as resource_log:
                    while not stopped.is_set():
                        snapshot = {'time': time.time(), 'processes': run(['ps', '-o', 'pid=,pcpu=,rss=', '-p', ','.join(str(child.pid) for child in processes)]).strip()}
                        containers = [lab['container']]
                        containers += run(['docker', 'ps', '-q', '--filter', 'label=expenses.tests.generator=' + lab['id']]).split()
                        stats = subprocess.run(['docker', 'stats', '--no-stream', '--format', '{{json .}}', *containers], capture_output=True, text=True, timeout=10)
                        snapshot['containers'] = stats.stdout.strip()
                        snapshot['collectionExitCode'] = stats.returncode
                        resource_log.write(json.dumps(snapshot) + '\n'); resource_log.flush()
                        stopped.wait(5)
            if options.suite == 'security':
                sys.path.insert(0, str(TESTS / 'security'))
                from zap import scan
                report['zap'] = scan(lab, env, state, output, active=options.active)
            elif options.suite == 'performance':
                report['profile'] = options.profile
                report['datasetUsersBeforeMeasurement'] = 1000
                report['k6Image'] = 'grafana/k6:2.3.0@sha256:9c2dee7f8ed74d317e4027c06a10f169b625638189de8d4555d0b3486a5aeb34'
                monitor = threading.Thread(target=collect_resources)
                monitor.start()
                image = 'grafana/k6:2.3.0@sha256:9c2dee7f8ed74d317e4027c06a10f169b625638189de8d4555d0b3486a5aeb34'
                proxy_ip = run(['docker', 'run', '--rm', '--entrypoint', 'getent', image, 'ahostsv4', 'host.docker.internal']).split()[0]
                print(run(['docker', 'run', '--rm', '--name', 'expenses-k6-' + lab['id'], '--label', 'expenses.tests.generator=' + lab['id'], '--cpus', '1', '--memory', '512m',
                           '--user', '0', '-v', str(TESTS / 'performance') + ':/scripts:ro',
                           '-v', str(ca) + ':/certs/ca.pem:ro', '-v', str(output) + ':/reports',
                           '-e', 'SSL_CERT_FILE=/certs/ca.pem', '-e', 'LAB_PROXY_IP=' + proxy_ip,
                           '-e', 'LAB_BASE_URL=' + env['LAB_BASE_URL'], '-e', 'LAB_RUN_ID=' + lab['id'],
                           '-e', 'LAB_PROFILE=' + options.profile, image,
                           'run', '/scripts/auth.js', '--summary-export', '/reports/k6.json']))
            else:
                args = ['npx', '--no-install', 'playwright', 'test']
                if options.suite != 'all': args += ['--project=' + ('api' if options.suite == 'api' else 'chromium')]
                print(run(args, env=env))
            report['status'] = 'passed'
        finally:
            generator_ids = run(['docker', 'ps', '-aq', '--filter', 'label=expenses.tests.generator=' + lab['id']]).split()
            for generator in generator_ids: run(['docker', 'rm', '-f', generator])
            stopped.set()
            if monitor: monitor.join(timeout=10)
            for child in reversed(processes):
                if child.poll() is None:
                    child.terminate()
                    try: child.wait(timeout=5)
                    except subprocess.TimeoutExpired: child.kill(); child.wait()
            for log in logs: log.close()
            shutil.rmtree(state)
            report['processesStopped'] = all(child.poll() is not None for child in processes)
            report['durationSeconds'] = round(time.time() - started, 2)
            (output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(f'Report: {output / "summary.json"}')


if __name__ == '__main__':
    main()
