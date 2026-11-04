#!/usr/bin/env bash
# What a production rollback may roll onto: an image production itself has run before (#418).
# Usage: infra/rollback-target.sh <sha256:digest>
# Writes image=<registry>/<repository>@<digest> and version=<commit sha> to $GITHUB_OUTPUT.
#
# A rollback used to take a commit SHA and deploy the registry's tag for it. A tag can be pushed
# again, and the staging principal holds AcrPush on the shared registry, so anyone able to run as
# staging could put their own image under an old commit's tag and have the next rollback carry it
# to production, while the approver saw only a familiar SHA. A digest names exact bytes, and the
# digest must be one a production revision has run, which only production's own approved jobs can
# create: so neither a re-pushed tag nor an image only staging pushed can reach production this way.
set -euo pipefail

digest="${1:-}"
: "${RESOURCE_GROUP:?}" "${PRODUCTION_APP:?}" "${REGISTRY:?}" "${IMAGE:?}"
if ! [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "::error::rollback_to must be an image digest, sha256: and 64 lowercase hex digits (the production job's summary lists the one it replaced)."
  exit 1
fi
image="$REGISTRY.azurecr.io/$IMAGE@$digest"

az config set extension.use_dynamic_install=yes_without_prompt >/dev/null 2>&1
# Every revision the app still keeps, inactive ones included (Container Apps keeps up to 100), and
# the version each ran: the version the smoke test then expects production to report.
revisions=$(az containerapp revision list -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --all \
  --query "[].{image: properties.template.containers[0].image, version: properties.template.containers[0].env[?name=='Relay__Version'].value | [0]}" -o tsv)
versions=$(awk -F '\t' -v image="$image" '$1 == image { print $2 }' <<< "$revisions" | sort -u)

if [ -z "$versions" ]; then
  echo "::error::No revision of $PRODUCTION_APP has run $image. A rollback goes only to an image production has run before; anything else goes through the build, staging and production jobs."
  exit 1
fi
if [ "$(wc -l <<< "$versions")" -ne 1 ]; then
  echo "::error::Revisions of $PRODUCTION_APP ran $image under more than one version ($(tr '\n' ' ' <<< "$versions")); refusing to guess which."
  exit 1
fi
if ! [[ "$versions" =~ ^[0-9a-f]{40}$ ]]; then
  echo "::error::The revision that ran $image recorded the version '$versions', not a commit SHA."
  exit 1
fi

{
  echo "image=$image"
  echo "version=$versions"
} >> "${GITHUB_OUTPUT:-/dev/null}"
{
  echo "### Rollback target"
  echo "- \`$image\`, which production ran as $versions"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
