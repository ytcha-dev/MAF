#!/usr/bin/env bash
# Reproducible end-to-end demo of the personal read-only pipeline:
#   real MAF CLI + official Workflow + real OpenCode 1.19.0 server,
#   with a local scripted OpenAI-compatible endpoint standing in for all three models.
# Needs: .NET 10 SDK, Node.js/npm. Uses only 127.0.0.1 ports 4096 (OpenCode) and 4100 (fake LLM).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="${MAF_DEMO_DIR:-$repo/TestResults/pipeline-demo}"
fixture="$work/fixture"
mkdir -p "$fixture/src" "$work/opencode"

printf 'public static class Calculator\n{\n    public static int Add(int a, int b) => a + b;\n}\n' > "$fixture/src/Calculator.cs"
cp "$here/opencode.fake.json" "$fixture/opencode.json"
[ -x "$work/opencode/node_modules/.bin/opencode" ] || (cd "$work/opencode" && npm init -y >/dev/null && npm install --silent opencode-ai@1.19.0)

cleanup() { kill "${llm_pid:-}" "${oc_pid:-}" 2>/dev/null || true; }
trap cleanup EXIT
FIXTURE="$fixture" node "$here/fake-llm.js" 2> "$work/fake-llm.log" & llm_pid=$!
(cd "$fixture" && exec "$work/opencode/node_modules/.bin/opencode" serve --hostname 127.0.0.1 --port 4096) > "$work/opencode.log" 2>&1 & oc_pid=$!
for _ in $(seq 1 60); do curl -sf -m 2 http://127.0.0.1:4096/global/health >/dev/null && break; sleep 1; done

dotnet build "$repo/src/MAF.Host" -v q >/dev/null
set +e
AI_BASE_URL=http://127.0.0.1:4100/v1 AI_API_KEY=unused AI_MODEL=pca AI_REVIEWER_MODEL=reviewer \
OPENCODE_BASE_URL=http://127.0.0.1:4096 OPENCODE_ALLOW_LOOPBACK_HTTP=1 \
  dotnet run --no-build --project "$repo/src/MAF.Host" -- --project "$fixture" \
  --pipeline "Read src/Calculator.cs and explain what Add does, citing lines." --stage-timeout 90 "$@"
code=$?
set -e
echo "exit=$code"
echo "--- model calls seen by the fake endpoint (role, streaming, tools offered, tool results) ---"
cat "$work/fake-llm.log"
exit $code
