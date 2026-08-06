#!/usr/bin/env bash
# Creates the 'knowledge-base' index and uploads the sample docs in data/sample-docs/.
# Requires: az cli logged in, jq. Run after `azd provision` (or `azd up`).
set -euo pipefail

SEARCH_ENDPOINT=$(azd env get-value AZURE_SEARCH_ENDPOINT)
API_VERSION="2024-07-01"
TOKEN=$(az account get-access-token --resource https://search.azure.com --query accessToken -o tsv)

echo "Creating index 'knowledge-base' at $SEARCH_ENDPOINT ..."
curl -s -X PUT "$SEARCH_ENDPOINT/indexes/knowledge-base?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{
    "name": "knowledge-base",
    "fields": [
      { "name": "id", "type": "Edm.String", "key": true, "searchable": false },
      { "name": "title", "type": "Edm.String", "searchable": true, "filterable": true },
      { "name": "content", "type": "Edm.String", "searchable": true }
    ]
  }' > /dev/null

echo "Uploading sample documents ..."
docs="[]"
i=0
for f in ../data/sample-docs/*.md; do
  i=$((i+1))
  title=$(grep -m1 '^title:' "$f" | sed 's/title: *//')
  content=$(tail -n +4 "$f" | tr '\n' ' ')
  docs=$(echo "$docs" | jq --arg id "doc$i" --arg title "$title" --arg content "$content" \
    '. + [{"@search.action":"mergeOrUpload","id":$id,"title":$title,"content":$content}]')
done

curl -s -X POST "$SEARCH_ENDPOINT/indexes/knowledge-base/docs/index?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"value\": $docs}" > /dev/null

echo "Done."
