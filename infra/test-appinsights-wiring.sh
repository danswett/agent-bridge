#!/usr/bin/env bash
# Log Analytics wiring (#1305, #1311, #569), checked on the compiled template:
# main.bicep declares the component and hands production (only) its connection string, relay-app
# sets the app setting only when given one, and monitoring.bicep refers to the component instead of
# declaring it (declaring it there made it circular with the app). Both vaults send audit logs
# to the same workspace, even before the apps deploy. Nothing here reaches Azure.
# Run by relay.yml's infra job.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
cd "$here/.."
az bicep build -f infra/main.bicep --stdout | python3 -c '
import json, sys

def walk(t, path="main"):
    res = t.get("resources", {})
    for k, r in (res.items() if isinstance(res, dict) else enumerate(res)):
        r["_key"] = str(k)
        yield path, r
        inner = r.get("properties", {}).get("template")
        if inner:
            yield from walk(inner, path + "/" + str(k))

rows = list(walk(json.load(sys.stdin)))
fail = []

def declared(path_prefix):
    return [r for p, r in rows if p.startswith(path_prefix) and r["type"] == "Microsoft.Insights/components" and not r.get("existing")]

if not [r for p, r in rows if p == "main" and r["type"] == "Microsoft.Insights/components" and not r.get("existing")]:
    fail.append("main.bicep does not declare the Application Insights component")
if declared("main/") and any(p != "main" for p, r in rows if r in declared("main/")):
    fail.append("a module still declares the component (monitoring.bicep must use an existing reference)")

text = json.dumps([r for p, r in rows])
if "APPLICATIONINSIGHTS_CONNECTION_STRING" not in text:
    fail.append("relay-app.bicep never sets APPLICATIONINSIGHTS_CONNECTION_STRING")
elif "empty(parameters(\x27appInsightsConnectionString\x27))" not in text:
    fail.append("relay-app.bicep sets the app setting without checking the string is non-empty")

prod = [r for p, r in rows if r["type"] == "Microsoft.Resources/deployments" and r["_key"] == "productionApp" and "appInsightsConnectionString" in json.dumps(r.get("properties", {}).get("parameters", {}))]
stag = [r for p, r in rows if r["type"] == "Microsoft.Resources/deployments" and r["_key"] == "stagingApp" and "appInsightsConnectionString" in json.dumps(r.get("properties", {}).get("parameters", {}))]
if not prod:
    fail.append("productionApp is not given appInsightsConnectionString")
if stag:
    fail.append("stagingApp must not get a connection string")

for app in ("stagingApp", "productionApp"):
    deployment = next(r for p, r in rows if p == "main" and r["_key"] == app)
    workspace = deployment["properties"]["parameters"].get("workspaceId", {}).get("value")
    if workspace != "[resourceId(\x27Microsoft.OperationalInsights/workspaces\x27, parameters(\x27logAnalyticsName\x27))]":
        fail.append(app + " must receive the stamp workspace id")
    diagnostics = [r for p, r in rows if p == "main/" + app and r["type"] == "Microsoft.Insights/diagnosticSettings"]
    if len(diagnostics) != 1:
        fail.append(app + " must declare one vault diagnostic setting")
        continue
    diagnostic = diagnostics[0]
    if diagnostic.get("scope") != "[format(\x27Microsoft.KeyVault/vaults/{0}\x27, parameters(\x27app\x27).keyVaultName)]" or "condition" in diagnostic:
        fail.append(app + " diagnostics must cover its vault even before app deployment")
    props = diagnostic["properties"]
    if props.get("workspaceId") != "[parameters(\x27workspaceId\x27)]":
        fail.append(app + " vault diagnostics must use the supplied workspace")
    if props.get("logs") != [{"categoryGroup": "audit", "enabled": True}] or props.get("metrics"):
        fail.append(app + " vault diagnostics must enable only audit logs, without metrics")

if fail:
    print("\n".join("FAIL: " + f for f in fail)); sys.exit(1)
print("ok: Application Insights and Key Vault audit wiring")
'
