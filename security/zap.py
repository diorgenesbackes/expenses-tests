"""Scoped local ZAP API scan. Full raw reports and authenticated configuration are ephemeral."""
import http.cookiejar
import json
import re
import ssl
import shutil
from pathlib import Path
import subprocess
import urllib.request
import urllib.parse
import uuid

IMAGE = 'ghcr.io/zaproxy/zaproxy:2.17.0@sha256:781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef'


def scan(lab, env, state, output, *, active=False):
    from database import run
    base = env['LAB_BASE_URL']
    if not re.fullmatch(r'https://localhost:\d+', base): raise RuntimeError('ZAP requires the owned loopback laboratory.')
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar),
        urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=env['NODE_EXTRA_CA_CERTS'])))
    def request(path, body=None, headers=None):
        req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body else None,
                                     headers={'Content-Type': 'application/json'} | (headers or {}))
        with opener.open(req, timeout=10) as response:
            return json.load(response)
    email = 'zap-' + uuid.uuid4().hex + '@example.test'
    credentials = {'email': email, 'password': 'ScannerTest123!'}
    request('/api/v1/identity/users', credentials)
    token = request('/api/v1/identity/csrf')['token']
    request('/api/v1/identity/sessions', credentials, {'X-CSRF-Token': token})
    if request('/api/test/session-protected') != {'authenticated': True}: raise RuntimeError('Authenticated preflight failed.')
    specification = request('/api/test/openapi/v1.json')
    # Session-mutating endpoints are exercised by dedicated API tests; scanning them would revoke
    # the scanner's own credentials and silently turn the protected scan into an anonymous scan.
    allowed = ['/test/session-protected', '/health', '/health/db', '/v1/identity/users']
    specification['paths'] = {path: value for path, value in specification['paths'].items() if path in allowed}
    specification['servers'] = [{'url': base + '/api'}]
    directory = state / 'zap'; directory.mkdir()
    shutil.copyfile(Path(__file__).with_name('zap_hooks.py'), directory / 'hooks.py')
    (directory / 'openapi.json').write_text(json.dumps(specification))
    cookies = '; '.join(cookie.name + '=' + cookie.value for cookie in jar)
    configuration = ['autoupdate.checkOnStart=false', 'ascan.maxScanDurationInMins=3', 'ascan.threadPerHost=1']
    for index, (name, value) in enumerate([('Cookie', cookies), ('X-CSRF-Token', token)]):
        prefix = f'replacer.full_list({index}).'
        configuration += [prefix + 'description=local-auth', prefix + 'enabled=true', prefix + 'matchtype=REQ_HEADER',
                          prefix + 'matchstr=' + name, prefix + 'regex=false', prefix + 'replacement=' + value,
                          prefix + 'url=' + re.escape(base).replace('\\', '\\\\') + '/api/.*']
    (directory / 'auth.properties').write_text('\n'.join(configuration) + '\n')
    proxy_ip = run(['docker', 'run', '--rm', '--entrypoint', 'getent', 'grafana/k6:2.3.0', 'ahostsv4', 'host.docker.internal']).split()[0]
    container = 'expenses-zap-' + lab['id']
    command = ['docker', 'run', '--rm', '--name', container, '--label', 'expenses.tests.scanner=' + lab['id'],
               '--cpus', '1', '--memory', '2g', '--user', '0', '--add-host', 'localhost:' + proxy_ip,
               '-v', str(directory) + ':/zap/wrk:rw', IMAGE, 'zap-api-scan.py', '-t', '/zap/wrk/openapi.json',
               '--hook', '/zap/wrk/hooks.py', '-f', 'openapi', '-J', 'raw.json', '-T', '3', '-s', '-z', '-configfile /zap/wrk/auth.properties']
    if not active: command.append('-S')
    try:
        # Output can contain request details. It stays in the private temporary directory only.
        with (directory / 'scanner.log').open('w') as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=300)
        raw = json.loads((directory / 'raw.json').read_text())
        alerts = []
        for site in raw.get('site', []):
            for alert in site.get('alerts', []):
                clean = {key: alert.get(key) for key in ['pluginid', 'name', 'riskcode', 'confidence', 'count', 'solution', 'reference']}
                clean['locations'] = [{'path': urllib.parse.urlsplit(item.get('uri', '')).path, 'method': item.get('method')}
                                      for item in alert.get('instances', [])]
                alerts.append(clean)
        evidence = json.loads((directory / 'auth-evidence.json').read_text())
        report = {'scannerAuthentication': evidence, 'mode': 'active' if active else 'passive', 'image': IMAGE, 'exitCode': result.returncode,
                  'scope': allowed, 'authenticatedBeforeAndAfter': request('/api/test/session-protected') == {'authenticated': True},
                  'alerts': alerts, 'status': 'passed' if result.returncode == 0 else 'findings' if result.returncode in (1, 2) else 'failed'}
        (output / 'zap.json').write_text(json.dumps(report, indent=2) + '\n')
        if result.returncode != 0 or not report['authenticatedBeforeAndAfter'] or evidence['authenticatedRequests'] == 0:
            raise RuntimeError(f'ZAP requires review (exit {result.returncode}); see the redacted zap.json report.')
        return report
    finally:
        ids = run(['docker', 'ps', '-aq', '--filter', 'label=expenses.tests.scanner=' + lab['id']]).split()
        for identifier in ids: run(['docker', 'rm', '-f', identifier])
