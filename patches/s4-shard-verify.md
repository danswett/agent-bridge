# S4 verify-only gate: the workflow step and the catalog flip (patch for Quality)

Part of #4104. For **#4332** (`shard-verify`, stacked on #4324 / #4320 / #4317). Applies on top of #4332's head (`git apply --check patches/s4-shard-verify.patch` is clean there), so apply it after #4332 and its stack have landed. I do not push workflow files.

- `.github/workflows/ci.yml`: one step in `product`, after `dotnet build AgentBridge.slnx`: **Verify the test shard plan**, `if: env.DOTNET_TARGET != '' && matrix.os == 'ubuntu-latest'`, bash, `timeout-minutes: 5`. It runs `agent-bridge-quality.dll shard-verify --plan quality/test-shards.json --max-seconds 120` with every `tests/*/bin/Release/net10.0/*.Tests.dll`. The job has no secrets and no write token; nothing is a `workflow_run` job.
- `quality/catalog.yml`: suite `shard-verify` from `planned` to `exists`.
- Job name for required checks: `product (ubuntu-latest)`; `quality-gate` already needs `product`, so no new merge-rule or main_green name.
- Checked: `agent-bridge-quality check` ("The catalog is complete.") on both the unpatched head and the patched tree; the real run over this repo's 16 test assemblies passes (9,164 methods, 3.4 s).
- Note for the gate's reach: the step runs when a test project is selected (`DOTNET_TARGET` set). A change that only edits `quality/test-shards.json` should select `product`; worth a look when you apply it.