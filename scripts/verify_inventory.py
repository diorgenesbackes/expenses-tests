"""Fail if an original class or case disappears during the test split."""
import json
from pathlib import Path
import re

TESTS = Path(__file__).resolve().parents[1]
manifest = json.loads((TESTS / 'inventory.json').read_text())
counts = {}
for entry in manifest['classes']:
    folder = TESTS.parent / 'expenses-service/Expenses.UnitTests' if entry['destination'] == 'unit' else TESTS / entry['destination']
    source = (folder / (entry['class'] + '.cs')).read_text()
    actual = len(re.findall(r'\[(?:Fact|PostgresFact|CognitoFact|RegistrationCognitoFact|InlineData)(?:\(|\])', source))
    if actual != entry['cases']:
        raise SystemExit(f"Inventory mismatch for {entry['class']}: expected {entry['cases']}, found {actual}")
    counts[entry['destination']] = counts.get(entry['destination'], 0) + actual
if sum(counts.values()) != manifest['originalCases']: raise SystemExit('Original inventory total changed.')
print(json.dumps(counts))
