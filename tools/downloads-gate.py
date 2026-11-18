#!/usr/bin/env python3
"""The Phase 2 gate: a new dump of each system files itself, with no hand edits.

Make the made-up library and its arrivals, and start Romdex on them with the igir worker and the inbox plugin:

    tools/make-gate-library.py gate
    ROMS_PATH=./gate/roms DATS_PATH=./gate/dats INBOX_PATH=./gate/inbox \\
        docker compose -f docker-compose.yml -f docker-compose.build.yml up --build

Go through setup, add the inbox folder under Settings → Plugins, and make an API key that can do everything. Then:

    tools/downloads-gate.py gate --romdex http://localhost:8788 --api-key rdx_...

It updates the catalogue and scans the library, has the PlayStation's discs stored as CHD (so one conversion is
checked too; --no-convert leaves them as they come), copies each arrival into the inbox, and waits for Romdex to take
them. Each must be filed as the dump gate/arrivals.csv says, and verified in the library. Exits 1 if any isn't,
saying why.
"""

import argparse
import csv
import json
import os
import shutil
import sys
import time
import urllib.error
import urllib.request


class Romdex:
    def __init__(self, url, key):
        self.url = url.rstrip("/") + "/api/v1"
        self.key = key

    def call(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.url + path, data=data, method=method,
                                         headers={"X-Api-Key": self.key, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request) as response:
                text = response.read()
                return json.loads(text) if text else None
        except urllib.error.HTTPError as e:
            raise SystemExit(f"{method} {path}: {e.code} {e.read().decode(errors='replace')}")

    def run(self, method, path):
        """Starts a background task and waits for it to finish."""
        task = self.call(method, path)
        while task["state"] in ("queued", "running"):
            time.sleep(1)
            task = self.call("GET", f"/tasks/{task['id']}")
        if task["state"] != "succeeded":
            raise SystemExit(f"{task.get('title') or task['kind']} {task['state']}: {task.get('error')}")


def main():
    parser = argparse.ArgumentParser(description="Check a new dump of each system files itself.")
    parser.add_argument("gate", help="the folder tools/make-gate-library.py made")
    parser.add_argument("--romdex", required=True, help="Romdex's address, such as http://localhost:8788")
    parser.add_argument("--api-key", required=True, help="an API key that can do everything")
    parser.add_argument("--inbox", help="the inbox plugin's folder (default: inbox in the gate folder)")
    parser.add_argument("--no-convert", action="store_true", help="leave the PlayStation's discs as they come")
    parser.add_argument("--timeout", type=int, default=600, help="seconds to wait for every arrival (default 600)")
    args = parser.parse_args()

    romdex = Romdex(args.romdex, args.api_key)
    inbox = args.inbox or os.path.join(args.gate, "inbox")
    with open(os.path.join(args.gate, "arrivals.csv"), newline="", encoding="utf-8") as f:
        arrivals = list(csv.DictReader(f))

    print("Updating the catalogue and scanning the library")
    romdex.run("POST", "/catalogue/refresh")
    romdex.run("POST", "/library/scan")
    systems = {s["name"]: s for s in romdex.call("GET", "/systems")}
    if not args.no_convert:
        ps = systems["Sony - PlayStation"]
        filing = romdex.call("GET", f"/systems/{ps['id']}/filing")
        romdex.call("PUT", f"/systems/{ps['id']}/filing", {**filing, "storage": "chd"})
        print("The PlayStation's discs will be stored as CHD")
    if not any(p.get("type") == "retrieval" and p.get("enabled", True) for p in romdex.call("GET", "/plugins")):
        raise SystemExit("There's no retriever set up: add the inbox folder under Settings → Plugins.")

    before = {i["id"] for i in romdex.call("GET", "/downloads?take=500")["items"]}
    os.makedirs(inbox, exist_ok=True)
    for arrival in arrivals:
        source = os.path.join(args.gate, "arrivals", arrival["Item"])
        target = os.path.join(inbox, arrival["Item"])
        (shutil.copytree if os.path.isdir(source) else shutil.copy2)(source, target)
        # The plugin waits for what's dropped in to settle; these are whole already.
        for folder, _, names in os.walk(target) if os.path.isdir(target) else [(os.path.dirname(target), [], [arrival["Item"]])]:
            for name in names:
                os.utime(os.path.join(folder, name), (time.time() - 3600, time.time() - 3600))
    print(f"Dropped {len(arrivals)} arrivals into {inbox}")

    deadline = time.time() + args.timeout
    while True:
        romdex.call("POST", "/downloads/check")
        items = [i for i in romdex.call("GET", "/downloads?take=500")["items"] if i["id"] not in before]
        settled = [i for i in items if i["state"] not in ("fetching", "queued", "working")]
        if len(settled) >= len(arrivals) and len(settled) == len(items):
            break
        if time.time() > deadline:
            raise SystemExit(f"Gave up waiting: {len(settled)} of {len(arrivals)} arrivals settled.")
        time.sleep(3)

    failures = 0
    for arrival in arrivals:
        item = next((i for i in items if i["name"] in (arrival["Item"], os.path.splitext(arrival["Item"])[0])), None)
        filed = [d for d in item["dumps"] if d["dump"]["name"] == arrival["Game Name"] and d["filed"]] if item else []
        if item and item["state"] == "filed" and filed:
            print(f"  ok    {arrival['DAT Name']}: {arrival['Game Name']} → {', '.join(filed[0]['filed'])}")
        else:
            failures += 1
            why = "never arrived" if item is None else f"{item['state']}: {item.get('message')}"
            print(f"  FAIL  {arrival['DAT Name']}: {arrival['Game Name']} ({arrival['Item']}) {why}")

    # Filed means checked in the library; this checks again from the outside.
    for system in romdex.call("GET", "/systems"):
        print(f"  {system['name']}: {system['owned']} owned, {system['partial']} partial, {system['bad']} bad")
    print("Every arrival filed itself." if failures == 0 else f"{failures} of {len(arrivals)} arrivals weren't filed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
