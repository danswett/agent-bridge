# A bad relay deploy

**Alerts:** A-BAKE (the bake rolled back on its own, once DEP-1 ships), or any relay alert
within an hour of a production deploy.

**Rule: roll back first, diagnose after.** Rolling back returns production to an image it ran
before the deploy, and costs each device one reconnect.

## Roll back (Owner approves)

1. Find the digest the deploy replaced. The production job's summary shows it under
   "roll back". Or list what production has run:
   ```powershell
   az containerapp revision list --subscription $sub -g $rg -n $app --all --query "[].{created: properties.createdTime, image: properties.template.containers[0].image, version: properties.template.containers[0].env[?name=='Relay__Version'].value | [0]}" -o table
   ```
2. **First, clear the `relay-production` concurrency group.** The rollback job shares it (with
   `cancel-in-progress: false`) with the production job. A run waiting at the gate holds the
   group, and one more run can be queued behind it, not yet at the gate. A rollback started now
   would wait behind both, and the only approval you'd be shown is a newer build's, which may
   still have the bug. Cancel the waiting **and** the queued runs, and repeat until both lists
   are empty:
   ```powershell
   gh run list -R danswett/agent-bridge -w relay.yml --status waiting
   gh run list -R danswett/agent-bridge -w relay.yml --status pending
   gh run cancel <id>    # for each one, then list again
   ```
3. Run the rollback (docs/relay-deploy.md "Rollback"):
   ```powershell
   gh workflow run relay.yml -R danswett/agent-bridge --ref main -f rollback_to=sha256:<digest>
   ```
   **Owner:** approve the `production` environment **only for the run whose job is
   `rollback`**. `infra/rollback-target.sh` refuses any digest production hasn't run, and the
   smoke test runs after.
4. Confirm: `pwsh ops/tools/relay-probe.ps1 -ExpectVersion <old sha>`.

## Keep it from coming back

- Revert the PR on `main`, or land a fix, before the next relay deploy is approved. Each
  merge touching the relay offers itself to production.
- Note the bad digest in the incident issue.

## Find the cause

- What did staging's smoke test cover that production's didn't? Production's smoke runs
  without a token (no presence, echo or blobs) until the canary (SYN-1) exists.
- Compare the two revisions' `warnings` and `connections` in
  [queries/relay.kql](../queries/relay.kql).
- If the bake should have caught it and didn't, tune DEP-1's checks.
