#!/usr/bin/env bash
# Runs POST /segments/evaluate and fails if accuracy falls below MIN_ACCURACY.
# Used by CI to catch prompt regressions before deployment.
#
# Usage:
#   bash scripts/check-accuracy.sh
#
# Environment variables:
#   API_URL       Base URL of the running API   (default: http://localhost:5100)
#   MIN_ACCURACY  Minimum passing accuracy %     (default: 80)

set -euo pipefail

API_URL="${API_URL:-http://localhost:5100}"
MIN_ACCURACY="${MIN_ACCURACY:-80}"

echo "==> Running evaluation suite against $API_URL"
echo "    Minimum accuracy threshold: ${MIN_ACCURACY}%"
echo ""

response=$(curl -sf -X POST \
  -H "Content-Type: application/json" \
  "$API_URL/segments/evaluate")

if [ -z "$response" ]; then
  echo "ERROR: Empty response from $API_URL/segments/evaluate"
  exit 1
fi

accuracy=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin)['accuracyPercent'])")
passed=$(echo "$response"   | python3 -c "import sys,json; print(json.load(sys.stdin)['passed'])")
total=$(echo "$response"    | python3 -c "import sys,json; print(json.load(sys.stdin)['total'])")
failed=$(echo "$response"   | python3 -c "import sys,json; print(json.load(sys.stdin)['failed'])")

echo "Results: ${passed}/${total} passed (${accuracy}%), ${failed} failed"
echo ""

# Print individual failures
echo "$response" | python3 - <<'EOF'
import sys, json
data = json.load(sys.stdin)
failures = [r for r in data['results'] if not r['passed']]
if failures:
    print("Failed cases:")
    for r in failures:
        print(f"  [{r['id']}] {r['description']}")
        print(f"    Reason: {r['failReason']}")
        if r['generatedQuery']:
            print(f"    Query:  {r['generatedQuery']}")
        print()
EOF

# Compare accuracy against threshold (python handles float comparison cleanly)
if python3 -c "import sys; sys.exit(0 if $accuracy >= $MIN_ACCURACY else 1)"; then
  echo "PASS: ${accuracy}% meets the ${MIN_ACCURACY}% threshold"
  exit 0
else
  echo "FAIL: ${accuracy}% is below the ${MIN_ACCURACY}% threshold"
  exit 1
fi
