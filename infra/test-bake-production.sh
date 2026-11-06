#!/usr/bin/env bash
# infra/bake-production.sh against a stand-in az, curl and gh, and a sleep that returns at once:
# nothing here reaches Azure, production or GitHub, and the 10-minute bake takes a moment. Run by
# relay.yml's infra job (#577).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"

# next <name>: the next line of $work/<name>.seq on each call, repeating the last once they run out.
cat > "$work/bin/next" <<'EOF'
#!/usr/bin/env bash
f="$SEQ_DIR/$1.seq" c="$SEQ_DIR/$1.count"
n=$(( $(cat "$c" 2> /dev/null || echo 0) + 1 )); echo "$n" > "$c"
total=$(wc -l < "$f"); [ "$n" -le "$total" ] || n=$total
sed -n "${n}p" "$f"
EOF
# /healthz: each line of health.seq is a body, or FAIL for no answer.
cat > "$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$CURL_LOG"
line=$(next health)
[ "$line" != FAIL ] || exit 22
printf '%s' "$line"
EOF
# az: the app's fields from AZ_*, the revisions rollback-target.sh lists from AZ_REVISIONS, and
# each line of restarts.seq the replicas' restart counts (space-separated), NONE for no replica or
# FAIL for an error.
cat > "$work/bin/az" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$AZ_LOG"
case "$*" in
  "config set"*) ;;
  "containerapp replica list"*)
    line=$(next restarts)
    [ "$line" != FAIL ] || exit 1
    [ "$line" = NONE ] || tr ' ' '\n' <<< "$line" ;;
  "containerapp revision list"*) printf '%b' "$AZ_REVISIONS" ;;
  *"ingress.fqdn"*) echo "$AZ_FQDN" ;;
  *"latestRevisionName"*) echo "$AZ_REVISION" ;;
  *"containers[0].image"*) [ "$AZ_IMAGE" != FAIL ] || exit 1; echo "$AZ_IMAGE" ;;
  *"Relay__Version"*) echo "$AZ_VERSION" ;;
  *) exit 1 ;;
esac
EOF
cat > "$work/bin/sleep" <<'EOF'
#!/usr/bin/env bash
EOF
# gh issue create: keeps its arguments and the body it was given.
cat > "$work/bin/gh" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$SEQ_DIR/gh.args"
cat > "$SEQ_DIR/gh.body"
echo https://github.com/o/r/issues/1
EOF
chmod +x "$work/bin/"*
export PATH="$work/bin:$PATH" SEQ_DIR="$work" CURL_LOG="$work/curl.log" AZ_LOG="$work/az.log"
export RESOURCE_GROUP=rg PRODUCTION_APP=app REGISTRY=reg IMAGE=relay

new=sha256:$(printf 'a%.0s' {1..64})
old=sha256:$(printf 'b%.0s' {1..64})
v1=$(printf '1%.0s' {1..40})
v2=$(printf '2%.0s' {1..40})
failures=0 errors=''

health() { printf '{"ok":true,"online":%s,"version":"%s"}' "$1" "${2:-$v1}"; }
# healths <online>...: one /healthz answer per sample, at v1.
healths() { : > "$work/health.seq"; for o in "$@"; do health "$o" >> "$work/health.seq"; echo >> "$work/health.seq"; done; }
restarts() { printf '%s\n' "$@" > "$work/restarts.seq"; }
repeat() { local i; for ((i = 0; i < $2; i++)); do printf '%s ' "$1"; done; }

reset() {
  rm -f "$work"/*.count "$work/gh.args" "$work/gh.body"
  : > "$work/out"; : > "$work/summary"; : > "$CURL_LOG"; : > "$AZ_LOG"
  healths 10; restarts 0
  export AZ_FQDN=relay.example AZ_REVISION=app--new AZ_IMAGE="reg.azurecr.io/relay@$new" AZ_VERSION="$v1"
  export EXPECT_IMAGE="reg.azurecr.io/relay@$new" EXPECT_VERSION="$v1"
  export BASELINE_ONLINE=10 BASELINE_REVISION=app--old BASELINE_RESTARTS=0
  export AZ_REVISIONS="reg.azurecr.io/relay@$old\t$v2\nreg.azurecr.io/relay@$new\t$v1\n"
  export BAD_IMAGE="reg.azurecr.io/relay@$new" PREVIOUS_DIGEST="$old"
}

# bake <args>: runs the script; the checks after it read what it did, and check names the case.
bake() {
  code=0 errors=''
  GITHUB_OUTPUT="$work/out" GITHUB_STEP_SUMMARY="$work/summary" "$here/bake-production.sh" "$@" > "$work/stdout" 2>&1 || code=$?
}
miss() { errors="$errors; $1"; }
exits() { [ "$code" -eq "$1" ] || miss "exit $code, wanted $1"; }
has_output() { grep -qxF -- "$1" "$work/out" || miss "no output $1"; }
says() { grep -qF -- "$1" "$work/summary" || miss "summary lacks '$1'"; }
reads() { local n; n=$(wc -l < "$CURL_LOG"); [ "$n" -eq "$1" ] || miss "/healthz read $n times, wanted $1"; }
never_shows() { ! grep -qF -- "$1" "$work/out" "$work/summary" "$work/stdout" || miss "'$1' reached the output"; }
check() {
  if [ -z "$errors" ]; then echo "ok: $1"; else
    echo "FAIL: $1 ($errors)"; sed 's/^/    /' "$work/stdout" "$work/summary"; failures=$((failures + 1))
  fi
}

# --- watch: the cases #577 names
reset; bake watch; exits 0; has_output verdict=passed; reads 40; says "**Passed.**"
check "a build that keeps its devices, version and replica for 10 minutes passes"

reset; healths $(repeat 1 11) 8; bake watch; exits 0; has_output verdict=passed
check "a slow recovery passes if 80% of the devices are back by minute 3"

reset; healths $(repeat 1 11) 7; bake watch; exits 1; has_output verdict=failed; reads 12; says "under 8"
check "a recovery short of 80% at minute 3 fails"

reset; healths $(repeat 10 19) 5; bake watch; exits 1; reads 20
check "devices dropping away after minute 3 fail the bake"

reset; restarts 0 0 0 0 1; bake watch; exits 1; reads 5; says "had restarted"
check "a crash (the revision restarts) fails the bake at once"

reset; { health 10; echo; health 10; echo; health 10 '<b>x</b>'; echo; } > "$work/health.seq"; bake watch
exits 1; reads 3; says "another version"; never_shows '<b>x</b>'
check "another version answering fails the bake, and what it said is not repeated"

# --- watch: nothing unreadable counts as healthy
reset; printf '%s\n' "$(health 10)" FAIL > "$work/health.seq"; bake watch; exits 1; reads 2; says "unreadable"
check "a /healthz that stops answering fails the bake"

for body in '{"ok":true,"version":"'"$v1"'"}' '{"ok":true,"online":"10","version":"'"$v1"'"}' \
  '{"ok":true,"online":-1,"version":"'"$v1"'"}' '{"ok":true,"online":9.5,"version":"'"$v1"'"}' \
  '{"ok":false,"online":10,"version":"'"$v1"'"}' '{"ok":true,"online":10}' 'not json'; do
  reset; echo "$body" > "$work/health.seq"; bake watch; exits 1; reads 1
  check "a /healthz answer without a whole count, ok and a version fails the bake: $body"
done

reset; restarts FAIL; bake watch; exits 1; says "no restart count"
check "a restart count Azure won't give fails the bake"

reset; restarts NONE; bake watch; exits 1
check "a revision with no replica fails the bake rather than counting as no restarts"

reset; restarts 1; bake watch; exits 1; reads 1
check "a new revision that restarted before the bake began fails it"

reset; export BASELINE_REVISION=app--new BASELINE_RESTARTS=2; restarts 2; bake watch; exits 0
check "an update that kept the revision is judged from that revision's restarts before the roll"

reset; export BASELINE_REVISION=app--new BASELINE_RESTARTS=2; restarts 2 3; bake watch; exits 1; reads 2
check "and fails as soon as that revision restarts again"

reset; export BASELINE_RESTARTS=none; restarts 0; bake watch; exits 0
check "a rollback whose broken revision gave no restart count still bakes a new revision"

reset; export BASELINE_RESTARTS=none; restarts 0 1; bake watch; exits 1; reads 2
check "and still fails it the moment that new revision restarts"

reset; export BASELINE_RESTARTS=none BASELINE_REVISION=app--new; bake watch; exits 1; reads 0
check "an unchanged revision whose restarts before the roll are unknown fails the bake"

# --- watch: the online floor
reset; export BASELINE_ONLINE=2; healths 0; bake watch; exits 0; says "under 3, so not compared"
check "a pre-roll count under 3 is not compared"

reset; export BASELINE_ONLINE=none; healths 0; bake watch; exits 0; says "unknown, so not compared"
grep -qF "checks the version and restarts" "$work/stdout" || miss "no warning that online is not compared"
check "an unknown pre-roll count is not compared, and the log and summary say so"

reset; export BASELINE_ONLINE=none; { health 0; echo; health 0 "$v2"; echo; } > "$work/health.seq"; bake watch
exits 1; reads 2
check "without a pre-roll count the bake still fails another version"

for value in '' 'ten' '-1' '10 '; do
  reset; export BASELINE_ONLINE="$value"; bake watch; exits 1; reads 0
  check "a missing or malformed pre-roll count fails the bake before it starts: '$value'"
done

# --- watch: it bakes production, and the image this job rolled
reset; export AZ_IMAGE="reg.azurecr.io/relay@$old"; bake watch; exits 1; reads 0; says "another image"
check "production running another digest than the one rolled fails the bake before it reads /healthz"

reset; export AZ_VERSION="$v2"; bake watch; exits 1; reads 0
check "production given another version than the one rolled fails the bake"

for host in 'relay.example/x' 'relay.example:8443' 'user@relay.example' '' 'localhost'; do
  reset; export AZ_FQDN="$host"; bake watch; exits 1; reads 0
  check "an ingress name that is not a plain host name is never asked: '$host'"
done

reset; export EXPECT_IMAGE="reg.azurecr.io/relay:$v1"; bake watch; exits 1; reads 0
check "an image to bake given by tag fails the bake"

reset; export EXPECT_VERSION='main'; bake watch; exits 1; reads 0
check "a version to bake that is not a commit SHA fails the bake"

reset; bake watch; never_shows relay.example; check "the bake's output never names the host it asked"

# --- baseline
reset; export AZ_IMAGE="reg.azurecr.io/relay@$old" AZ_REVISION=app--old; restarts '1 1'; healths 9; bake baseline
exits 0; has_output online=9; has_output revision=app--old; has_output restarts=2; has_output "digest=$old"
check "the baseline records production's online count, revision, restarts and the digest it runs"

reset; export AZ_IMAGE="reg.azurecr.io/relay:$v1"; bake baseline; exits 0; has_output digest=; says "cannot roll back"
check "an image deployed by tag is no rollback target"

reset; export AZ_IMAGE="other.azurecr.io/relay@$old"; bake baseline; exits 0; has_output digest=
check "an image from another registry is no rollback target"

reset; echo FAIL > "$work/health.seq"; bake baseline; exits 1
check "a deploy is refused before the roll when production gives no count to compare with"

reset; echo FAIL > "$work/health.seq"; bake baseline --rollback; exits 0; has_output online=none
check "a rollback records an unknown count instead, since its production is what's broken"

reset; restarts FAIL; bake baseline; exits 1
check "a deploy is refused before the roll when Azure gives no restart count"

reset; restarts NONE; bake baseline --rollback; exits 0; has_output restarts=none
check "a rollback records an unknown restart count when the broken revision has no replica"

reset; export AZ_REVISION='app--x;y'; bake baseline; exits 1
check "a revision name outside the expected pattern is refused"

# --- revert: after a failed roll, smoke test or bake, only an applied roll is rolled back
az_listed() { grep -q 'revision list' "$AZ_LOG"; }

reset; bake revert; exits 0; has_output applied=true
has_output "image=reg.azurecr.io/relay@$old"; has_output "version=$v2"
check "after a failed smoke test or bake, production running the new image goes back to the digest before, through rollback-target.sh"

reset; export AZ_IMAGE="reg.azurecr.io/relay@$old"; bake revert; exits 0; has_output applied=false; says "never applied"
! az_listed || miss "looked for a rollback target"; ! grep -q '^image=' "$work/out" || miss "gave a rollback image"
check "a roll that never applied is not rolled back"

reset; export PREVIOUS_DIGEST=''; bake revert; exits 1; has_output applied=true
check "an applied roll with no digest to go back to fails, so the incident says production may still run it"

reset; export PREVIOUS_DIGEST="$new"; bake revert; exits 1; ! az_listed || miss "looked for a rollback target"
check "a roll of the image production already ran is not rolled back onto itself"

reset; export AZ_IMAGE="other.azurecr.io/relay@$old"; bake revert; exits 1; ! az_listed || miss "looked for a rollback target"
check "production running neither image is left alone"

reset; export AZ_IMAGE=FAIL; bake revert; exits 1; ! az_listed || miss "looked for a rollback target"
check "an image Azure won't give is not guessed at"

reset; export AZ_REVISIONS="reg.azurecr.io/relay@$new\t$v1\n"; bake revert; exits 1; ! grep -q '^image=' "$work/out" || miss "gave a rollback image"
check "a digest no production revision ran is refused by rollback-target.sh"

for value in 'sha256:1' "$old x" "reg.azurecr.io/relay@$old"; do
  reset; export PREVIOUS_DIGEST="$value"; bake revert; exits 1; ! az_listed || miss "looked for a rollback target"
  check "a malformed digest to go back to is refused: '$value'"
done

reset; export BAD_IMAGE="reg.azurecr.io/relay:$v1"; bake revert; exits 1
check "a rolled image given by tag is refused"

# --- incident
incident() {
  reset
  export GITHUB_REPOSITORY=o/r RUN_URL=https://github.com/o/r/actions/runs/7 BAKE_JOB=production
  export BAKE_IMAGE="reg.azurecr.io/relay@$new" BAKE_VERSION="$v1" BAKE_REASON="at 3:00, online was 2, under 8 (80% of 10 before the roll)"
  export BAKE_FAILED_AT="2026-11-04 13:00 PT" PREVIOUS_DIGEST="$old" REVERT_VERSION="$v2"
  export ROLL_OUTCOME=success SMOKE_OUTCOME=success BAKE_OUTCOME=failure
  export APPLIED=true RECOVERY=success RECOVERY_ROLL=success RECOVERY_SMOKE=success
}
body_has() { grep -qF -- "$1" "$work/gh.body" || miss "issue lacks '$1'"; }
body_lacks() { ! grep -qF -- "$1" "$work/gh.body" || miss "issue has '$1'"; }

incident; bake incident; exits 0
body_has "# Incident: relay 1111111 failed after its roll"; body_has "**Status:** mitigated"; body_has "**Severity:** 2"
body_has "rolled production back to \`$old\` ($v2)"; body_has "https://github.com/o/r/actions/runs/7"
body_has "| 2026-11-04 13:00 PT | The bake failed: at 3:00, online was 2"; body_lacks "Who and what was affected"
grep -qxF -- area:ops "$work/gh.args" || miss "not labelled area:ops"
check "a bake that rolled back opens a mitigated incident from ops/templates/incident.md"

incident; export SMOKE_OUTCOME=failure BAKE_OUTCOME=skipped BAKE_REASON='' BAKE_FAILED_AT=''; bake incident; exits 0
body_has "| The smoke test after the roll failed |"; body_has "**Status:** mitigated"; body_lacks "(unreadable)"
check "a smoke test that failed after the roll and rolled back opens a mitigated incident too"

incident; export RECOVERY_SMOKE=failure; bake incident; exits 0
body_has "**Status:** investigating"; body_has "production may still run the failed build"; body_has "smoke test: failure"
body_has "Recovery target: \`$old\`"; body_has "rollback_to=$old"; body_has "**Do not roll back to \`$new\`**"
body_lacks "rollback_to=$new"; body_has "; not \`$new\`, the failed build |"
check "a rollback whose smoke test failed names the digest from before the deploy as the way back, never the failed one"

incident; export RECOVERY_ROLL=failure RECOVERY_SMOKE=skipped; bake incident; exits 0
body_has "**Status:** investigating"; body_has "roll: failure"; body_has "Recovery target: \`$old\`"
body_has "**Do not roll back to \`$new\`**"; body_lacks "rollback_to=$new"
check "a rollback whose roll failed names the digest from before the deploy as the way back, never the failed one"

for value in '' 'sha256:1' "reg.azurecr.io/relay@$old" "$new"; do
  incident; export RECOVERY_ROLL=failure RECOVERY_SMOKE=skipped PREVIOUS_DIGEST="$value"; bake incident; exits 0
  body_has "No verified recovery target"; body_lacks "Recovery target:"; body_lacks "rollback_to="
  body_has "**Do not roll back to \`$new\`**"
  check "a digest from before the deploy that is missing, malformed or the failed one is never offered: '$value'"
done

incident; export RECOVERY=failure RECOVERY_ROLL=skipped RECOVERY_SMOKE=skipped; bake incident; exits 0
body_has "**Status:** investigating"; body_has "target: failure"
check "a rollback refused its target says production may still run the failed build"

incident; export BAKE_JOB=rollback APPLIED='' RECOVERY=skipped RECOVERY_ROLL=skipped RECOVERY_SMOKE=skipped; bake incident; exits 0
body_has "Nothing further was done automatically"
check "a dispatched rollback that failed after its roll opens an incident and does nothing more"

incident; export BAKE_REASON=$'x\n## Injected' BAKE_IMAGE='reg/relay@sha256:1 [link](https://example.com)'; bake incident; exits 0
body_lacks "Injected"; body_lacks "example.com"; body_has "(unreadable)"
check "a value outside its pattern reaches the issue as (unreadable)"

if [ "$failures" -ne 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
