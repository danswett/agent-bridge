#!/usr/bin/env bash
# The bake after a production roll (#577, ops/plan.md §6.1). At a deploy every device is told to
# reconnect (1012), so the clearest sign that a build is healthy is that the devices come back.
#
#   infra/bake-production.sh baseline [--rollback]
#     Before the roll: what production runs and serves now. Writes online=, revision=,
#     restarts= and digest= (the running image's digest, the automatic rollback's target; empty
#     when it wasn't deployed by digest from this registry) to $GITHUB_OUTPUT. Without a count
#     from /healthz it refuses, before production is touched: the bake could not judge the
#     deploy. With --rollback (relay.yml's rollback job, whose production is presumably what's
#     broken) it records online=none instead, and the bake then says it skipped that check; and
#     restarts=none when the broken revision gives no count, which the bake then can't use.
#   infra/bake-production.sh watch
#     After the smoke test, with EXPECT_IMAGE, EXPECT_VERSION and the baseline's BASELINE_*:
#     every 15 s for 10 min, /healthz must answer with EXPECT_VERSION, the revision's restart
#     count must not rise, and from minute 3 `online` must be at least 80% of the pre-roll count
#     (not checked when that was under 3, or unknown). Exits 1 at the first miss.
#   infra/bake-production.sh revert
#     After a roll, smoke test or bake in relay.yml's production job failed, with BAD_IMAGE (the
#     image it rolled) and PREVIOUS_DIGEST (the baseline's digest): if production now runs
#     BAD_IMAGE, checks PREVIOUS_DIGEST with rollback-target.sh, which writes image= and version=
#     for the rollback, and writes applied=true. If production still runs the previous image the
#     roll never applied: applied=false, and nothing is rolled back. Anything else refuses.
#   infra/bake-production.sh incident
#     Opens the incident issue (ops/templates/incident.md) for a failure after a roll.
#
# Nothing unreadable counts as healthy: a /healthz that doesn't answer, or answers without a
# whole-number count or the version, or a restart count Azure won't give, fails the bake. And
# everything this writes to the log, the summary, outputs or the issue is a count, a fixed phrase
# or a value first checked against a strict pattern (digest, commit, revision): /healthz's body
# is never echoed, and no address, account or device leaves the job.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
interval=15
samples=40   # 10 min
floor_from=12 # the sample at minute 3
revision_pattern='^[a-z0-9]([a-z0-9-]{0,98}[a-z0-9])?$'
host_pattern='^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'
image_pattern='^[a-z0-9.-]+/[a-z0-9._/-]+@sha256:[0-9a-f]{64}$'
version_pattern='^[0-9a-f]{40}$'

output() { echo "$1" >> "${GITHUB_OUTPUT:-/dev/null}"; }
summary() { echo "$1" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"; }
refuse() { echo "::error::$1"; exit 1; }
pacific() { TZ=America/Los_Angeles date '+%Y-%m-%d %H:%M PT'; }

app_field() {
  az containerapp show -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --query "$1" -o tsv
}

# The app's own ingress name, read with production's principal: the bake asks production, not
# whatever a URL handed to it names.
read_host() {
  local host
  host=$(app_field properties.configuration.ingress.fqdn) || return 1
  [[ "$host" =~ $host_pattern ]] || return 1
  echo "$host"
}

# Sets h_online and h_version from /healthz. HTTPS only, no redirects, a small body; curl's own
# messages are dropped, since they can name an address.
read_health() {
  local body
  h_online='' h_version=''
  body=$(curl -fsS --proto '=https' --max-time 10 --max-filesize 65536 --retry 2 --retry-all-errors --retry-delay 2 \
    "https://$1/healthz" 2> /dev/null) || return 1
  h_online=$(jq -er 'if .ok == true and (.online | type) == "number" and .online >= 0 and .online == (.online | floor)
    then .online else empty end' <<< "$body" 2> /dev/null) || return 1
  [[ "$h_online" =~ ^[0-9]{1,9}$ ]] || return 1
  h_version=$(jq -er '.version | strings' <<< "$body" 2> /dev/null) || return 1
}

# Sets r_total: the restarts of every container of every replica of the revision. No replica at
# all is not zero restarts: the revision isn't running.
read_restarts() {
  local list n
  list=$(az containerapp replica list -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --revision "$1" \
    --query "[].properties.containers[].restartCount" -o tsv) || return 1
  [ -n "$list" ] || return 1
  r_total=0
  while IFS= read -r n; do
    [[ "$n" =~ ^[0-9]{1,6}$ ]] || return 1
    r_total=$((r_total + 10#$n))
  done <<< "$list"
}

baseline() {
  local mode="${1:-}" host revision image prefix digest='' online
  [ -z "$mode" ] || [ "$mode" = --rollback ] || refuse "usage: bake-production.sh baseline [--rollback]"
  : "${RESOURCE_GROUP:?}" "${PRODUCTION_APP:?}" "${REGISTRY:?}" "${IMAGE:?}"
  az config set extension.use_dynamic_install=yes_without_prompt > /dev/null 2>&1

  host=$(read_host) || refuse "Could not read $PRODUCTION_APP's ingress name, or it is not a plain host name."
  revision=$(app_field properties.latestRevisionName) || refuse "Could not read $PRODUCTION_APP's revision."
  [[ "$revision" =~ $revision_pattern ]] || refuse "$PRODUCTION_APP's revision name is not one the bake can use."
  if ! read_restarts "$revision"; then
    # A broken revision may have no replica left to count, and refusing would block the recovery.
    # The bake still counts the new revision's restarts; only an unchanged revision can't be judged.
    [ "$mode" = --rollback ] || refuse "Could not read the restart count of $PRODUCTION_APP's running revision. The bake could not judge this deploy without it, so production was left as it is."
    r_total=none
    echo "::warning::Production's running revision gave no restart count before the rollback; the bake will allow the new revision none."
  fi
  image=$(app_field "properties.template.containers[0].image") || refuse "Could not read the image $PRODUCTION_APP runs."
  prefix="$REGISTRY.azurecr.io/$IMAGE@"
  if [[ "$image" == "$prefix"* ]] && [[ "${image#"$prefix"}" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    digest="${image#"$prefix"}"
  fi

  if read_health "$host"; then
    online="$h_online"
  elif [ "$mode" = --rollback ]; then
    online=none
    echo "::warning::Production's /healthz gave no device count before the rollback, so its bake won't compare online with one. It still checks the version and restarts."
  else
    refuse "Production's /healthz gave no device count before the roll. The bake could not judge this deploy without one, so production was left as it is. To roll forward anyway, see docs/relay-deploy.md, Rollback."
  fi

  output "online=$online"
  output "revision=$revision"
  output "restarts=$r_total"
  output "digest=$digest"
  summary "### Before the roll"
  summary "- online: $online; restarts: $r_total on revision \`$revision\`"
  if [ "$mode" = --rollback ]; then
    summary "- a failure of this rollback is not rolled back automatically"
  elif [ -n "$digest" ]; then
    summary "- a failure once the roll applies rolls back to \`$digest\`"
  else
    summary "- the image it runs was not deployed by digest from this registry, so a failure after the roll cannot roll back to it automatically"
  fi
}

watch() {
  : "${RESOURCE_GROUP:?}" "${PRODUCTION_APP:?}"
  local expect_image="${EXPECT_IMAGE:-}" expect_version="${EXPECT_VERSION:-}" base_online="${BASELINE_ONLINE:-}"
  local base_revision="${BASELINE_REVISION:-}" base_restarts="${BASELINE_RESTARTS:-}"
  local host revision image version allowed floor=0 i t at online restarts match

  # Every miss below is a failed bake (and in the production job, a rollback): a bake that can't
  # tell is not a pass.
  failed() {
    echo "::error::The bake failed: $1"
    summary ""
    summary "**Failed:** $1"
    output "verdict=failed"
    output "reason=$1"
    output "failed_at=$(pacific)"
    exit 1
  }

  [[ "$expect_image" =~ $image_pattern ]] || failed "the image to bake is not a digest reference"
  [[ "$expect_version" =~ $version_pattern ]] || failed "the version to bake is not a commit SHA"
  [[ "$base_online" =~ ^[0-9]{1,9}$ || "$base_online" == none ]] || failed "no pre-roll online count"
  [[ "$base_revision" =~ $revision_pattern ]] || failed "no pre-roll revision"
  [[ "$base_restarts" =~ ^[0-9]{1,6}$ || "$base_restarts" == none ]] || failed "no pre-roll restart count"
  az config set extension.use_dynamic_install=yes_without_prompt > /dev/null 2>&1

  host=$(read_host) || failed "could not read production's ingress name"
  revision=$(app_field properties.latestRevisionName) || failed "could not read production's revision"
  [[ "$revision" =~ $revision_pattern ]] || failed "production's revision name is not one the bake can use"
  image=$(app_field "properties.template.containers[0].image") || failed "could not read the image production runs"
  [ "$image" = "$expect_image" ] || failed "production runs another image than the one this job rolled"
  version=$(app_field "properties.template.containers[0].env[?name=='Relay__Version'].value | [0]") ||
    failed "could not read the version production was given"
  [ "$version" = "$expect_version" ] || failed "production was given another version than the one this job rolled"

  # A new revision starts from no restarts, so one before the bake began, in the smoke test's
  # minutes, counts too. An update that changed nothing keeps the revision, and its count.
  if [ "$revision" != "$base_revision" ]; then
    allowed=0
  elif [ "$base_restarts" = none ]; then
    failed "the revision did not change, and its restarts before the roll are unknown"
  else
    allowed=$((10#$base_restarts))
  fi
  if [ "$base_online" != none ] && [ "$((10#$base_online))" -ge 3 ]; then
    floor=$(((4 * 10#$base_online + 4) / 5)) # 80%, rounded up
  fi

  summary "### Bake: \`${expect_version:0:7}\` on revision \`$revision\`"
  if [ "$base_online" = none ]; then
    echo "::warning::No device count from before the roll: this bake checks the version and restarts, not the devices online."
    summary "Online before the roll: unknown, so not compared."
  elif [ "$floor" -eq 0 ]; then
    summary "Online before the roll: $base_online, under 3, so not compared."
  else
    summary "Online before the roll: $base_online; from minute 3 it must stay at $floor or more."
  fi
  summary "Restarts: no more than $allowed."
  summary ""
  summary "| Time | Online | Restarts | Version |"
  summary "|---|---|---|---|"

  for ((i = 1; i <= samples; i++)); do
    sleep "$interval"
    t=$((i * interval))
    at=$(printf '%d:%02d' $((t / 60)) $((t % 60)))
    if ! read_health "$host"; then
      summary "| $at | | | unreadable |"
      failed "at $at, /healthz gave no device count and version"
    fi
    online="$h_online"
    if [ "$h_version" = "$expect_version" ]; then match=yes; else match=other; fi
    if ! read_restarts "$revision"; then
      summary "| $at | $online | unreadable | $match |"
      failed "at $at, Azure gave no restart count for the revision"
    fi
    restarts="$r_total"
    summary "| $at | $online | $restarts | $match |"
    echo "$at online $online, restarts $restarts, version $match"
    [ "$match" = yes ] || failed "at $at, /healthz answered with another version"
    [ "$restarts" -le "$allowed" ] || failed "at $at, the revision had restarted ($restarts, allowed $allowed)"
    if [ "$floor" -gt 0 ] && [ "$i" -ge "$floor_from" ] && [ "$online" -lt "$floor" ]; then
      failed "at $at, online was $online, under $floor (80% of $base_online before the roll)"
    fi
  done

  summary ""
  summary "**Passed.**"
  output "verdict=passed"
}

# A value for the issue only if it matches its pattern; the issue is public.
checked() {
  if [[ "$1" =~ $2 ]]; then echo "$1"; else echo "(unreadable)"; fi
}

# After relay.yml's production job rolled and then failed (the roll partway, the smoke test or the
# bake): back to the digest production ran before, but only if the roll applied the new image,
# and only through rollback-target.sh's checks. Never before the roll: a baseline that failed
# skips the roll, and with it this.
revert() {
  : "${RESOURCE_GROUP:?}" "${PRODUCTION_APP:?}" "${REGISTRY:?}" "${IMAGE:?}"
  local bad="${BAD_IMAGE:-}" digest="${PREVIOUS_DIGEST:-}" previous='' running
  output "at=$(pacific)"
  [[ "$bad" =~ $image_pattern ]] || refuse "The image this job rolled is not a digest reference; production was left as it is."
  [ -z "$digest" ] || [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || refuse "The digest production ran before the roll is malformed; production was left as it is."
  [ -z "$digest" ] || previous="$REGISTRY.azurecr.io/$IMAGE@$digest"
  az config set extension.use_dynamic_install=yes_without_prompt > /dev/null 2>&1

  running=$(app_field "properties.template.containers[0].image") ||
    refuse "Could not read the image production runs, so could not tell whether the roll applied; production was left as it is."
  [ "$bad" != "$previous" ] || refuse "Production ran this same image before the roll, so rolling back to it would change nothing. Follow ops/runbooks/relay-bad-deploy.md."
  if [ -n "$previous" ] && [ "$running" = "$previous" ]; then
    output "applied=false"
    summary "### Automatic rollback"
    summary "- not needed: the roll never applied, and production still runs \`$digest\`"
    return 0
  fi
  [ "$running" = "$bad" ] || refuse "Production runs neither the image this job rolled nor the one before it; production was left as it is."
  output "applied=true"
  [ -n "$digest" ] || refuse "Production ran no image by digest from this registry before the roll, so there is nothing to roll back to automatically. Follow ops/runbooks/relay-bad-deploy.md."
  "$here/rollback-target.sh" "$digest"
  summary "- rolling back automatically, in this approved job: \`$bad\` failed after its roll"
}

incident() {
  : "${GITHUB_REPOSITORY:?}" "${RUN_URL:?}"
  local outcome='^(success|failure|skipped|cancelled)$'
  local job image version reason failed_at previous applied target roll smoke revert_version run_url
  local what title status impact mitigated rows url
  job=$(checked "${BAKE_JOB:-}" '^(production|rollback)$')
  image=$(checked "${BAKE_IMAGE:-}" "$image_pattern")
  version=$(checked "${BAKE_VERSION:-}" "$version_pattern")
  reason=$(checked "${BAKE_REASON:-it ran out of time or gave no reason}" "^[A-Za-z0-9 .,:;()%/'-]{1,200}\$")
  failed_at="${BAKE_FAILED_AT:-}"
  [ -n "$failed_at" ] || failed_at=$(pacific)
  failed_at=$(checked "$failed_at" '^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2} PT$')
  previous=$(checked "${PREVIOUS_DIGEST:-}" '^sha256:[0-9a-f]{64}$')
  applied="${APPLIED:-}"
  target=$(checked "${RECOVERY:-skipped}" "$outcome")
  roll=$(checked "${RECOVERY_ROLL:-skipped}" "$outcome")
  smoke=$(checked "${RECOVERY_SMOKE:-skipped}" "$outcome")
  revert_version=$(checked "${REVERT_VERSION:-}" "$version_pattern")
  run_url=$(checked "$RUN_URL" '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/[0-9]+$')

  # The first check after the roll that failed; the later ones were skipped.
  if [ "${ROLL_OUTCOME:-}" = failure ]; then what="the roll failed partway"
  elif [ "${SMOKE_OUTCOME:-}" = failure ]; then what="the smoke test after the roll failed"
  elif [ "${BAKE_OUTCOME:-}" = failure ]; then what="the bake failed: $reason"
  else what="a check after the roll failed"; fi
  if [ "$version" = "(unreadable)" ]; then title="relay deploy failed after its roll"; else title="relay ${version:0:7} failed after its roll"; fi

  rows="| $failed_at | ${what^} | relay.yml $job job |"
  mitigated="<time>"
  if [ "$job" = rollback ]; then
    status=investigating
    impact="A rollback that a person ran, to \`$image\` ($version), failed after its roll: $what. Nothing further was done automatically. Follow ops/runbooks/relay-bad-deploy.md."
  elif [ "$applied" = true ] && [ "$target" = success ] && [ "$roll" = success ] && [ "$smoke" = success ]; then
    status=mitigated
    mitigated="$(pacific), automatically"
    impact="Production ran \`$image\` ($version), and $what. The same approved job rolled production back to \`$previous\` ($revert_version), which passed the smoke test. Each device reconnected twice."
    # awk -v turns this \n into a line break; no value here can hold a backslash of its own.
    rows="$rows\\n| $(pacific) | Rolled back automatically to \`$previous\` ($revert_version); smoke test passed | relay.yml production job |"
  else
    status=investigating
    impact="Production was rolled to \`$image\` ($version), and $what. The automatic rollback did not complete (target: $target, roll: $roll, smoke test: $smoke), so **production may still run the failed build**. Follow ops/runbooks/relay-bad-deploy.md now."
    rows="$rows\\n| $(pacific) | Automatic rollback did not complete (target: $target, roll: $roll, smoke test: $smoke) | relay.yml production job |"
  fi

  url=$(awk -v title="$title" -v status="$status" -v started="$failed_at" -v detected="$(pacific), by relay.yml (A-BAKE): $run_url" \
    -v mitigated="$mitigated" -v deploy="$run_url, \`$image\` ($version)" -v impact="$impact" -v rows="$rows" '
    { sub(/\r$/, "") }
    /^# Incident:/ { print "# Incident: " title; next }
    /^\*\*Severity:\*\*/ { print "**Severity:** 2"; next }
    /^\*\*Status:\*\*/ { print "**Status:** " status; next }
    /^\*\*Started:\*\*/ { print "**Started:** " started "   **Detected:** " detected; next }
    /^\*\*Mitigated:\*\*/ { print "**Mitigated:** " mitigated "   **Resolved:** <time>"; next }
    /^- Relay deploy:/ { print "- Relay deploy: " deploy; next }
    /^\| \| \| \|$/ { print rows; next }
    /^## / { skipping = 0 }
    /^## Impact/ { print; print ""; print impact; print ""; skipping = 1; placed = 1; next }
    skipping { next }
    { print }
    END { if (!placed) { print ""; print "## Impact"; print ""; print impact } }
  ' "$here/../ops/templates/incident.md" |
    gh issue create -R "$GITHUB_REPOSITORY" --title "Incident: $title" --label area:ops --body-file -)
  summary "Incident opened: $url"
}

case "${1:-}" in
  baseline) shift; baseline "$@" ;;
  watch) watch ;;
  revert) revert ;;
  incident) incident ;;
  *) refuse "usage: bake-production.sh baseline [--rollback] | watch | revert | incident" ;;
esac