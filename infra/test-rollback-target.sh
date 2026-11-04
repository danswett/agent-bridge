#!/usr/bin/env bash
# infra/rollback-target.sh and roll-production.sh's rollback hint, against a stand-in az: nothing
# here signs in to or changes Azure. Run by relay.yml's infra job (#418).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"

# The stand-in prints $AZ_REVISIONS for `revision list` and $AZ_IMAGE / $AZ_VERSION for
# `containerapp show`, and records every other call.
cat > "$work/bin/az" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$AZ_LOG"
case "$*" in
  "config set"*) ;;
  "containerapp revision list"*) printf '%b' "$AZ_REVISIONS" ;;
  *"tags."*) echo infra ;;
  *"containers[0].image"*) echo "$AZ_IMAGE" ;;
  *"Relay__Version"*) echo "$AZ_VERSION" ;;
  *"ingress.fqdn"*) echo relay.example ;;
  "containerapp update"*) echo rev-new ;;
esac
EOF
chmod +x "$work/bin/az"
export PATH="$work/bin:$PATH" AZ_LOG="$work/az.log"
export RESOURCE_GROUP=rg PRODUCTION_APP=app REGISTRY=reg IMAGE=relay

good=sha256:$(printf 'a%.0s' {1..64})
other=sha256:$(printf 'b%.0s' {1..64})
v1=$(printf '1%.0s' {1..40})
v2=$(printf '2%.0s' {1..40})
failures=0

# run <name> <expected exit> <expected output line or ''> <digest>
run() {
  local name="$1" want="$2" line="$3" digest="$4" code=0
  : > "$work/out"; : > "$AZ_LOG"
  GITHUB_OUTPUT="$work/out" GITHUB_STEP_SUMMARY=/dev/null "$here/rollback-target.sh" "$digest" > "$work/stdout" 2>&1 || code=$?
  if [ "$code" -ne "$want" ] || { [ -n "$line" ] && ! grep -qxF "$line" "$work/out"; } || { [ -z "$line" ] && [ -s "$work/out" ]; }; then
    echo "FAIL: $name (exit $code, wanted $want)"; cat "$work/stdout" "$work/out"; failures=$((failures + 1))
  else
    echo "ok: $name"
  fi
}

export AZ_REVISIONS="reg.azurecr.io/relay@$good\t$v1\nreg.azurecr.io/relay:$v2\t$v2\n"
run "a digest a production revision ran rolls back to that image" 0 "image=reg.azurecr.io/relay@$good" "$good"
run "and production is told the version that revision ran" 0 "version=$v1" "$good"
run "a digest no production revision ran is refused, so an image only staging pushed never reaches production" 1 "" "$other"
# A malformed rollback_to is refused before anything asks Azure.
untouched() {
  if [ -s "$AZ_LOG" ]; then echo "FAIL: $1 still reached Azure"; failures=$((failures + 1)); fi
}
run "a commit SHA is refused: its tag could have been pushed again" 1 "" "$v2"; untouched "a commit SHA"
run "a tag is refused" 1 "" "relay:$v2"; untouched "a tag"
run "a digest in capitals is refused rather than matched loosely" 1 "" "sha256:$(printf 'A%.0s' {1..64})"; untouched "a digest in capitals"
run "an empty rollback_to is refused" 1 "" ""; untouched "an empty rollback_to"
export AZ_REVISIONS="reg.azurecr.io/relay@$good\t$v1\nreg.azurecr.io/relay@$good\t$v2\n"
run "a digest that ran under two versions is refused rather than guessed" 1 "" "$good"
export AZ_REVISIONS="reg.azurecr.io/relay@$good\t\n"
run "a revision with no commit SHA for its version is refused" 1 "" "$good"
export AZ_REVISIONS="other.azurecr.io/relay@$good\t$v1\n"
run "the same digest from another registry is not production's image" 1 "" "$good"

# roll-production.sh: the hint it leaves for the next rollback names a digest, never a SHA.
hint() {
  local name="$1" want="$2"
  : > "$work/summary"
  GITHUB_OUTPUT=/dev/null GITHUB_STEP_SUMMARY="$work/summary" "$here/roll-production.sh" "reg.azurecr.io/relay@$other" "$v2" > /dev/null
  if grep -qF -- "$want" "$work/summary"; then echo "ok: $name"; else echo "FAIL: $name"; cat "$work/summary"; failures=$((failures + 1)); fi
}
AZ_IMAGE="reg.azurecr.io/relay@$good" AZ_VERSION="$v1" hint "production's summary names the digest it replaced as the rollback target" "rollback_to=$good"
AZ_IMAGE="reg.azurecr.io/relay:$v1" AZ_VERSION="$v1" hint "an image deployed by tag is not offered as a rollback target" "was not deployed by digest"
# relay.yml's automatic rollback (#577) replaces the build that failed: never offered as the way back.
AZ_IMAGE="reg.azurecr.io/relay@$good" AZ_VERSION="$v1" REPLACING_FAILED=true hint "an automatic rollback's summary sends a person to the incident, not to the failed build" "the build that failed in this run"
if grep -qF -- "rollback_to=" "$work/summary"; then echo "FAIL: an automatic rollback's summary offers the build it replaced"; failures=$((failures + 1)); else echo "ok: an automatic rollback's summary never offers the build it replaced"; fi

if [ "$failures" -ne 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
