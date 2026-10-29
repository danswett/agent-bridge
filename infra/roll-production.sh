#!/usr/bin/env bash
# Rolls production onto one image: the build staging passed, or an earlier one (rollback).
# Usage: infra/roll-production.sh <image ref> <commit sha>
# Writes url=<https://fqdn> to $GITHUB_OUTPUT, and the image it replaced to the job summary,
# which is the rollback target if this one misbehaves.
set -euo pipefail

image="$1"
version="$2"
: "${RESOURCE_GROUP:?}" "${PRODUCTION_APP:?}"
az config set extension.use_dynamic_install=yes_without_prompt >/dev/null 2>&1

# Only an app infra/deploy.ps1 has adopted: it has the vault-backed secrets, probes and drain
# settings this image expects. Rolling onto the hand-made dev app would skip all of that.
managed=$(az containerapp show -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --query "tags.\"agent-bridge-managed\"" -o tsv)
if [ "$managed" != "infra" ]; then
  echo "::error::$PRODUCTION_APP has not been adopted by infra/ (no agent-bridge-managed=infra tag). Run the cutover in docs/relay-deploy.md first."
  exit 1
fi

previous=$(az containerapp show -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --query "properties.template.containers[0].image" -o tsv)
previous_version=$(az containerapp show -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" \
  --query "properties.template.containers[0].env[?name=='Relay__Version'].value | [0]" -o tsv)
{
  echo "### Production"
  echo "- now: \`$image\` ($version)"
  echo "- before: \`$previous\` ($previous_version)"
  echo "- roll back: run this workflow with rollback_to=$previous_version"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"

az containerapp update -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --image "$image" \
  --set-env-vars "Relay__Version=$version" --query properties.latestRevisionName -o tsv
fqdn=$(az containerapp show -g "$RESOURCE_GROUP" -n "$PRODUCTION_APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "url=https://$fqdn" >> "${GITHUB_OUTPUT:-/dev/null}"
