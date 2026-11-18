#!/usr/bin/env python3
"""The Phase 3 gate: Game Boy, SNES and PlayStation games play in the browser, and a GameCube game opens in RomM.

Made-up games don't run, so this one takes three freely licensed homebrew games, which you download yourself into
gate/homebrew: µCity 1.3 (ucity.gbc, codeberg.org/SkyLyrac/ucity), EUC Thrills SNES Edition 1.1
(euc-thrills-snes-v1.1.zip, github.com/VibezZzCoder/EUC-Thrills-SNES-Edition) and Tetrade 1.0 (TETRADE_PSX.bin and
.cue, github.com/Logan-Campbell/Tetrade). None of them is in the repo, and gate/ isn't either.

Make the gate library, start Romdex on it with the emulator, then a throwaway RomM beside it:

    tools/make-gate-library.py gate
    ROMS_PATH=./gate/roms DATS_PATH=./gate/dats INBOX_PATH=./gate/inbox \\
        docker compose -f docker-compose.yml -f docker-compose.build.yml up --build -d
    docker compose -f tools/romm-gate.compose.yml up -d

Go through Romdex's setup, add the inbox folder under Settings → Plugins, make an API key that can do everything, then:

    tools/play-gate.py gate --romdex http://localhost:8788 --api-key rdx_...
    tools/downloads-gate.py gate --romdex http://localhost:8788 --api-key rdx_...

play-gate.py makes RomM's admin (the password goes in gate/romm-admin) and a token, and connects Romdex to RomM;
adds the homebrew games to the gate's DATs under their real hashes, and drops them into the inbox. Run it before
downloads-gate.py, so the GameCube game that one files is told to RomM. Run play-gate.py again afterwards with --check:
each homebrew game has to have a player link that loads it, and the GameCube game a link to its page in RomM. The last
step is yours: open each game's page in Romdex, press Play, and see it run.
"""

import argparse
import hashlib
import http.cookiejar
import json
import os
import re
import secrets
import shutil
import sys
import time
import urllib.error
import urllib.request
import zipfile
import zlib
from xml.sax.saxutils import quoteattr

# The games, the system each is in, as its DAT names it, and the files they're made of.
HOMEBREW = [
    ("µCity (World) (v1.3)", "Nintendo - Game Boy Color", "ucity", ["ucity.gbc"]),
    ("EUC Thrills (World) (v1.1)", "Nintendo - Super Nintendo Entertainment System", "euc-thrills", ["euc-thrills-snes.sfc"]),
    ("Tetrade (World) (v1.0)", "Sony - PlayStation", "tetrade", ["TETRADE_PSX.cue", "TETRADE_PSX.bin"]),
]
GAMECUBE = "Ember Isle (Japan)"


class Romdex:
    def __init__(self, url, key):
        self.root = url.rstrip("/")
        self.url = self.root + "/api/v1"
        self.key = key

    def call(self, method, path, body=None, ok=(200, 201, 202, 204)):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.url + path, data=data, method=method,
                                         headers={"X-Api-Key": self.key, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request) as response:
                text = response.read()
                return json.loads(text) if text else None
        except urllib.error.HTTPError as e:
            if e.code in ok:
                return None
            raise SystemExit(f"{method} {path}: {e.code} {e.read().decode(errors='replace')}")

    def run(self, method, path):
        """Starts a background task and waits for it to finish."""
        task = self.call(method, path)
        while task["state"] in ("queued", "running"):
            time.sleep(1)
            task = self.call("GET", f"/tasks/{task['id']}")
        if task["state"] != "succeeded":
            raise SystemExit(f"{task.get('title') or task['kind']} {task['state']}: {task.get('error')}")

    def owned_dump(self, name):
        """The dump of this name the library has whole."""
        for title in self.call("GET", "/titles?have=true&take=1000")["items"]:
            detail = self.call("GET", f"/titles/{title['id']}")
            for dump in detail["dumps"]:
                if dump["name"] == name and dump["status"] == "good":
                    return dump
        return None


def fetch(url):
    """A link anyone can open: the player's are signed, so they need no key."""
    with urllib.request.urlopen(url) as response:
        return response.status, response.read()


def files_of(homebrew, files):
    """The homebrew game's files, out of its download: the SNES game comes in a zip with its manual."""
    out = []
    for name in files:
        path = os.path.join(homebrew, name)
        if not os.path.exists(path) and name.endswith(".sfc"):
            with zipfile.ZipFile(os.path.join(homebrew, "euc-thrills-snes-v1.1.zip")) as z:
                out.append((name, z.read(name)))
            continue
        if not os.path.exists(path):
            raise SystemExit(f"{path} isn't there. Download the homebrew games first: see this script's help.")
        with open(path, "rb") as f:
            out.append((name, f.read()))
    return out


def add_to_dats(dats, homebrew):
    """Adds each game to its system's DAT, writing one if there's none, with a newer version so it's imported again."""
    version = time.strftime("%Y%m%d-%H%M%S")
    for game, system, _, files in HOMEBREW:
        path = os.path.join(dats, f"{system}.dat")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                text = f.read()
        else:
            text = (f'<?xml version="1.0"?>\n<datafile>\n  <header>\n    <name>{system}</name>\n    <description>{system}</description>\n'
                    f'    <version>{version}</version>\n  </header>\n</datafile>\n')
        if f"<game name={quoteattr(game)}>" not in text:
            entry = [f"  <game name={quoteattr(game)}>", f"    <description>{game}</description>"]
            for name, content in files_of(homebrew, files):
                entry.append(f"    <rom name={quoteattr(name)} size=\"{len(content)}\" crc=\"{zlib.crc32(content):08x}\" "
                             f"md5=\"{hashlib.md5(content).hexdigest()}\" sha1=\"{hashlib.sha1(content).hexdigest()}\"/>")
            entry.append("  </game>")
            text = text.replace("</datafile>", "\n".join(entry) + "\n</datafile>")
        text = re.sub(r"<version>[^<]*</version>", f"<version>{version}</version>", text, count=1)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    print(f"Added {len(HOMEBREW)} homebrew games to the DATs in {dats}")


def romm_token(romm, gate):
    """Makes RomM's admin, the first time, and a client token for Romdex. The password is kept in gate/romm-admin."""
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    with opener.open(f"{romm}/api/heartbeat") as response:
        setup = json.loads(response.read())["SYSTEM"]["SHOW_SETUP_WIZARD"]
    keep = os.path.join(gate, "romm-admin")
    if setup:
        password = secrets.token_hex(12)
        csrf = next(c.value for c in jar if c.name == "romm_csrftoken")
        body = json.dumps({"username": "admin", "email": "admin@gate.test", "password": password, "role": "admin"}).encode()
        opener.open(urllib.request.Request(f"{romm}/api/users", data=body, method="POST",
                                           headers={"Content-Type": "application/json", "x-csrftoken": csrf})).read()
        with open(keep, "w") as f:
            f.write(password)
        os.chmod(keep, 0o600)
        print(f"Made RomM's admin; the password is in {keep}")
    with open(keep) as f:
        password = f.read().strip()
    basic = "Basic " + __import__("base64").b64encode(f"admin:{password}".encode()).decode()
    body = json.dumps({"name": "Romdex gate", "scopes": ["roms.read", "platforms.read", "tasks.run"]}).encode()
    request = urllib.request.Request(f"{romm}/api/client-tokens", data=body, method="POST",
                                     headers={"Content-Type": "application/json", "Authorization": basic})
    with urllib.request.urlopen(request) as response:
        return json.loads(response.read())["raw_token"]


def set_up(args, romdex):
    if not any(p["type"] == "connector" for p in romdex.call("GET", "/plugins")):
        token = romm_token(args.romm_public, args.gate)
        romdex.call("POST", "/plugins", {"pluginId": "romdex.connector.romm",
                                         "settings": {"url": args.romm, "token": token, "publicUrl": args.romm_public}})
        print(f"Connected Romdex to RomM at {args.romm}")
    add_to_dats(os.path.join(args.gate, "dats"), args.homebrew)
    print("Updating the catalogue")
    romdex.run("POST", "/catalogue/refresh")

    inbox = args.inbox or os.path.join(args.gate, "inbox")
    for _, _, item, files in HOMEBREW:
        os.makedirs(os.path.join(inbox, item), exist_ok=True)
        for name, content in files_of(args.homebrew, files):
            target = os.path.join(inbox, item, name)
            with open(target, "wb") as f:
                f.write(content)
            # The plugin waits for what's dropped in to settle; these are whole already.
            os.utime(target, (time.time() - 3600, time.time() - 3600))
    romdex.call("POST", "/downloads/check")
    print(f"Dropped {len(HOMEBREW)} homebrew games into {inbox}. Run tools/downloads-gate.py next, then this again with --check.")


def check(args, romdex):
    failures = 0
    deadline = time.time() + args.timeout
    for game, system, _, _ in HOMEBREW:
        while (dump := romdex.owned_dump(game)) is None and time.time() < deadline:
            romdex.call("POST", "/downloads/check")
            time.sleep(3)
        if dump is None:
            print(f"  FAIL  {system}: {game} isn't in the library")
            failures += 1
            continue
        play = romdex.call("GET", f"/play/{dump['id']}")
        if not play.get("playerUrl"):
            print(f"  FAIL  {system}: {game} doesn't play: {play.get('unplayable')}")
            failures += 1
            continue
        player = f"{romdex.root}/{play['playerUrl']}"
        _, page = fetch(player)
        page = page.decode()
        core = re.search(r'EJS_core = "([^"]+)"', page).group(1)
        game_url = json.loads(re.search(r"EJS_gameUrl = (\"[^\"]*\")", page).group(1))
        status, content = fetch(player.rsplit("/", 1)[0] + "/" + game_url)
        _, loader = fetch(f"{romdex.root}/emulator/data/loader.js")
        where = ", ".join(place["path"] for file in dump["files"] for place in file["library"])
        if status == 200 and content and loader:
            print(f"  ok    {system}: {game} plays with {core}, {len(content):,} bytes from {where}")
            print(f"        {player}")
        else:
            print(f"  FAIL  {system}: {game}'s player couldn't load it ({status})")
            failures += 1

    # The GameCube isn't the browser's to play: it opens in RomM, which was told about it when it was filed.
    dump = romdex.owned_dump(GAMECUBE)
    link = None
    while dump is not None and time.time() < deadline:
        link = romdex.call("POST", f"/play/{dump['id']}/app")
        if link.get("url"):
            break
        time.sleep(5)
    if link and link.get("url"):
        status, _ = fetch(link["url"])
        print(f"  ok    Nintendo - GameCube: {GAMECUBE} opens in {link['app']} at {link['url']} ({status})")
    else:
        print(f"  FAIL  Nintendo - GameCube: {GAMECUBE} " + ("isn't in the library" if dump is None else f"has no link: {link and link.get('waiting')}"))
        failures += 1

    print("Every game plays or opens." if failures == 0 else f"{failures} failed.")
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Check games play in the browser and open in RomM.")
    parser.add_argument("gate", help="the folder tools/make-gate-library.py made")
    parser.add_argument("--romdex", required=True, help="Romdex's address, such as http://localhost:8788")
    parser.add_argument("--api-key", required=True, help="an API key that can do everything")
    parser.add_argument("--homebrew", help="where the homebrew games are (default: homebrew in the gate folder)")
    parser.add_argument("--inbox", help="the inbox plugin's folder (default: inbox in the gate folder)")
    parser.add_argument("--romm", default="http://romm:8080", help="where Romdex reaches RomM (default http://romm:8080)")
    parser.add_argument("--romm-public", default="http://localhost:8790", help="where you open RomM (default http://localhost:8790)")
    parser.add_argument("--check", action="store_true", help="check the games, once they've been filed")
    parser.add_argument("--timeout", type=int, default=600, help="seconds to wait for the games to be filed and found (default 600)")
    args = parser.parse_args()
    args.homebrew = args.homebrew or os.path.join(args.gate, "homebrew")
    romdex = Romdex(args.romdex, args.api_key)
    if args.check:
        return check(args, romdex)
    set_up(args, romdex)
    return 0


if __name__ == "__main__":
    sys.exit(main())
