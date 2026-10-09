"""Source identity without reading ignored private configuration or generated artifacts."""
import hashlib
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def source_identity():
    result = {}
    for project in ['expenses-service', 'expenses-ui', 'expenses-tests', 'expenses-liquibase']:
        directory = ROOT / project
        files = subprocess.check_output(['rg', '--files', '--hidden', '--no-require-git', '-g', '!.git', '-g', '!dist', '-g', '!reports', '-g', '!.env*', '-g', '!*.local.properties'], cwd=directory, text=True).splitlines()
        digest = hashlib.sha256()
        for name in sorted(files):
            path = directory / name
            if path.is_symlink() or path.suffix not in ['.cs', '.csproj', '.sln', '.json', '.py', '.js', '.mjs', '.ts', '.tsx', '.yaml', '.yml', '.sql']: continue
            digest.update(name.encode()); digest.update(b'\0'); digest.update(path.read_bytes()); digest.update(b'\0')
        revision = subprocess.run(['git', 'rev-parse', '--verify', 'HEAD'], cwd=directory, text=True, capture_output=True)
        dirty = subprocess.run(['git', 'status', '--porcelain'], cwd=directory, text=True, capture_output=True)
        result[project] = {'sourceSha256': digest.hexdigest(), 'gitHead': revision.stdout.strip() if revision.returncode == 0 else None,
                           'hasLocalChanges': bool(dirty.stdout) if dirty.returncode == 0 else None}
    return result
