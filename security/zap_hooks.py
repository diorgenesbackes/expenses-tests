"""Record only status counts, never authentication headers or response bodies."""
import json


def zap_pre_shutdown(zap):
    statuses = {}
    start = 0
    while True:
        messages = zap.core.messages(start=start, count=500)
        if not messages: break
        for message in messages:
            line = message['requestHeader'].splitlines()[0]
            if line.startswith('GET ') and '/api/test/session-protected ' in line:
                status = message['responseHeader'].splitlines()[0].split()[1]
                statuses[status] = statuses.get(status, 0) + 1
        start += len(messages)
    with open('/zap/wrk/auth-evidence.json', 'w') as file:
        json.dump({'protectedProbeStatuses': statuses, 'authenticatedRequests': statuses.get('200', 0)}, file)
