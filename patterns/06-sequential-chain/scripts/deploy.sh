#!/usr/bin/env bash
# Zip-deploys the Logic Apps Standard workflow content to the app provisioned by infra/main.bicep.
set -euo pipefail

RESOURCE_GROUP="${1:?Usage: deploy.sh <resource-group>}"
LOGIC_APP_NAME=$(az deployment group show -g "$RESOURCE_GROUP" -n main --query properties.outputs.LOGIC_APP_NAME.value -o tsv)

cd "$(dirname "$0")/../workflow"
zip -r ../workflow.zip . -x "*.git*"
cd -

az functionapp deployment source config-zip \
  -g "$RESOURCE_GROUP" \
  -n "$LOGIC_APP_NAME" \
  --src workflow.zip

echo "Deployed workflow content to $LOGIC_APP_NAME"
