#!/usr/bin/env bash
# Wires the Event Grid custom topic to the three deployed Function endpoints.
# Run after `azd up` has finished deploying the Function App code.
set -euo pipefail

TOPIC_NAME=$(azd env get-value EVENTGRID_TOPIC_NAME)
FUNCTION_APP_NAME=$(azd env get-value FUNCTION_APP_NAME)
RESOURCE_GROUP=$(azd env get-value AZURE_RESOURCE_GROUP)

FUNC_KEY=$(az functionapp keys list -g "$RESOURCE_GROUP" -n "$FUNCTION_APP_NAME" --query masterKey -o tsv)
BASE_URL="https://${FUNCTION_APP_NAME}.azurewebsites.net/runtime/webhooks/eventgrid?functionName="

declare -A subs=(
  ["research-agent-sub"]="research_agent:request.created"
  ["factcheck-agent-sub"]="factcheck_agent:research.completed"
  ["synthesis-agent-sub"]="synthesis_agent:research.completed,factcheck.completed"
)

for sub_name in "${!subs[@]}"; do
  entry="${subs[$sub_name]}"
  func_name="${entry%%:*}"
  event_types="${entry#*:}"
  endpoint="${BASE_URL}${func_name}&code=${FUNC_KEY}"

  echo "Creating subscription $sub_name -> $func_name (events: $event_types)"
  az eventgrid event-subscription create \
    --name "$sub_name" \
    --source-resource-id "$(az eventgrid topic show -g "$RESOURCE_GROUP" -n "$TOPIC_NAME" --query id -o tsv)" \
    --endpoint "$endpoint" \
    --included-event-types $(echo "$event_types" | tr ',' ' ')
done

echo "Done. All three agents are now wired to the Event Grid topic."
