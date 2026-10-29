#!/usr/bin/env python3
"""Onboards a disposable Home Assistant for the HA connector's integration tests.

CI only (the ha-integration job in .github/workflows/ci.yml): a Home Assistant container that
was started empty a minute ago, on this runner's loopback, with a Mosquitto container beside it.
It creates the owner account, takes an access token for it, adds the MQTT integration pointed at
the Mosquitto container, and hands the address and token to the tests through GITHUB_ENV.

Refuses any address that is not loopback: this script must never be pointed at a real Home
Assistant, whose owner account already exists and whose broker is in use.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("HA_URL", "http://127.0.0.1:8123").rstrip("/")
BROKER = os.environ.get("MQTT_BROKER_HOST", "mosquitto")
CLIENT_ID = BASE + "/"

host = urllib.parse.urlparse(BASE).hostname
if host not in ("127.0.0.1", "localhost", "::1"):
    sys.exit(f"refusing to onboard {BASE}: only a disposable Home Assistant on loopback")


def call(method, path, body=None, token=None, form=False):
    data = None
    headers = {}
    if body is not None:
        if form:
            data = urllib.parse.urlencode(body).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=30) as response:
        text = response.read().decode()
        return json.loads(text) if text else None


def wait_for_onboarding(deadline):
    while time.time() < deadline:
        try:
            steps = call("GET", "/api/onboarding")
            print("onboarding steps:", steps, flush=True)
            return
        except (urllib.error.URLError, ConnectionError, OSError) as e:
            print("waiting for Home Assistant:", e, flush=True)
            time.sleep(3)
    sys.exit("Home Assistant did not start")


def main():
    wait_for_onboarding(time.time() + 300)
    user = call("POST", "/api/onboarding/users", {
        "client_id": CLIENT_ID, "name": "Agent Bridge CI", "username": "ci",
        "password": "disposable-" + os.urandom(8).hex(), "language": "en",
    })
    token = call("POST", "/auth/token", {
        "grant_type": "authorization_code", "code": user["auth_code"], "client_id": CLIENT_ID,
    }, form=True)["access_token"]
    # The token never reaches the log in clear.
    print("::add-mask::" + token, flush=True)
    for step, body in (("core_config", {}), ("analytics", {}),
                       ("integration", {"client_id": CLIENT_ID, "redirect_uri": BASE + "/?auth_callback=1"})):
        try:
            call("POST", "/api/onboarding/" + step, body, token)
        except urllib.error.HTTPError as e:
            print(f"onboarding step {step}: {e.code} (carrying on)", flush=True)

    flow = call("POST", "/api/config/config_entries/flow", {"handler": "mqtt", "show_advanced_options": False}, token)
    for _ in range(6):
        print("mqtt flow:", {k: flow.get(k) for k in ("type", "step_id", "reason", "errors", "menu_options")}, flush=True)
        kind = flow.get("type")
        if kind == "create_entry":
            break
        if kind == "abort":
            sys.exit("the MQTT config flow aborted: " + str(flow.get("reason")))
        if kind == "menu":
            options = flow.get("menu_options") or []
            choice = next((o for o in options if "broker" in o), options[0])
            flow = call("POST", "/api/config/config_entries/flow/" + flow["flow_id"], {"next_step_id": choice}, token)
            continue
        body = {"broker": BROKER, "port": 1883} if flow.get("step_id") == "broker" else {}
        flow = call("POST", "/api/config/config_entries/flow/" + flow["flow_id"], body, token)
    else:
        sys.exit("the MQTT config flow did not finish")

    deadline = time.time() + 120
    while time.time() < deadline:
        entries = call("GET", "/api/config/config_entries/entry", None, token)
        mqtt = [e for e in entries if e.get("domain") == "mqtt"]
        if mqtt and mqtt[0].get("state") == "loaded":
            break
        print("waiting for MQTT:", [e.get("state") for e in mqtt], flush=True)
        time.sleep(2)
    else:
        sys.exit("the MQTT integration did not load")

    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as env:
        env.write(f"AGENT_BRIDGE_HA_TEST_URL={BASE}\n")
        env.write(f"AGENT_BRIDGE_HA_TEST_TOKEN={token}\n")
        env.write("AGENT_BRIDGE_HA_TEST_DISPOSABLE=1\n")
    print("Home Assistant is onboarded, with MQTT on", BROKER, flush=True)


main()
