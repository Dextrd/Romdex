import io
import json
import os
import time
import zipfile
import zlib
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app import main
from app.inbox import Inbox

# Made-up games: none of this is anyone's real dump.
ROCKET = bytes(range(256)) * 64
HEADER = b"NES\x1a" + bytes(12)
TRACK_1 = b"\x01" * 4096
TRACK_2 = b"\x02" * 8192


def made(path: Path, data: bytes | str, age: float = 600) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data.encode() if isinstance(data, str) else data)
    then = time.time() - age
    os.utime(path, (then, then))
    return path


def zipped(*files: tuple[str, bytes]) -> bytes:
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, "w") as z:
        for name, data in files:
            z.writestr(name, data)
    return memory.getvalue()


def target(title: str, dump: str, *files: tuple[str, bytes]) -> dict:
    return {"system": "Made-up Console", "title": title, "dumps": [{"name": dump, "regions": ["USA"], "files": [
        {"name": n, "size": len(d), "crc32": f"{zlib.crc32(d):08x}"} for n, d in files]}]}


@pytest.fixture
def root(tmp_path) -> Path:
    (tmp_path / "staging").mkdir()
    (tmp_path / "inbox").mkdir()
    return tmp_path / "inbox"


@pytest.fixture
def client(monkeypatch, tmp_path, root):
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(main, "inbox", Inbox(str(root)))
    return TestClient(main.app)


def wait(client, job_id):
    for _ in range(200):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


def test_each_thing_dropped_in_is_listed_once_it_has_settled(client, root):
    made(root / "Rocket Pal (USA).nes", ROCKET)
    made(root / "Disc Game" / "Disc Game (Track 1).bin", TRACK_1)
    made(root / "Disc Game" / "Disc Game (Track 2).bin", TRACK_2)
    made(root / "Disc Game" / "dump.log", "Dumped with a made-up drive.\nErrors: 0")
    made(root / "Copying.nes", ROCKET, age=0)
    made(root / ".hidden", "x")
    made(root / "stray.log", "A log on its own")

    listed = client.post("/v1/list", json={"settings": {}}).json()["candidates"]
    assert sorted(c["name"] for c in listed) == ["Disc Game", "Rocket Pal (USA)"]
    disc = next(c for c in listed if c["name"] == "Disc Game")
    assert disc["size"] == len(TRACK_1) + len(TRACK_2) + len("Dumped with a made-up drive.\nErrors: 0")
    assert disc["format"] == "bin"
    assert "Errors: 0" in disc["notes"]

    # With no wait, what's still being copied is offered too.
    assert len(client.post("/v1/list", json={"settings": {"settleSeconds": 0}}).json()["candidates"]) == 3


def test_a_search_matches_by_checksum_inside_zips_and_past_headers(client, root):
    made(root / "renamed.zip", zipped(("whatever.nes", ROCKET)))
    made(root / "headered.nes", HEADER + ROCKET)
    made(root / "Rocket Pal (Europe).nes", b"\x00" * len(ROCKET))  # the same name, but not the same game
    made(root / "Something Else.nes", b"\xff" * 100)

    found = client.post("/v1/search", json={"settings": {}, "target": target("Rocket Pal", "Rocket Pal (USA)", ("Rocket Pal (USA).nes", ROCKET))}).json()["candidates"]
    verified = [c for c in found if c.get("verified")]
    assert sorted(c["name"] for c in verified) == ["headered", "renamed"]
    assert all(c["score"] == 1.0 and c["dumpName"] == "Rocket Pal (USA)" for c in verified)
    guessed = [c for c in found if not c.get("verified")]
    assert [c["name"] for c in guessed] == ["Rocket Pal (Europe)"]
    assert guessed[0]["score"] < 1
    assert found.index(guessed[0]) > found.index(verified[-1])


def test_a_fetch_moves_the_item_to_staging_whole(client, root, tmp_path):
    made(root / "Disc Game" / "Disc Game (Track 1).bin", TRACK_1)
    made(root / "Disc Game" / "sub" / "Disc Game (Track 2).bin", TRACK_2)
    candidate = client.post("/v1/list", json={"settings": {}}).json()["candidates"][0]

    body = {"settings": {}, "jobId": "j", "candidate": candidate, "stagingDirectory": "downloads/j"}
    assert client.post("/v1/fetch", json=body).status_code == 202
    status = wait(client, "j")
    assert status["state"] == "completed"
    assert sorted(f["path"] for f in status["files"]) == ["Disc Game/Disc Game (Track 1).bin", "Disc Game/sub/Disc Game (Track 2).bin"]
    assert (tmp_path / "staging" / "downloads" / "j" / "Disc Game" / "sub" / "Disc Game (Track 2).bin").read_bytes() == TRACK_2
    assert not (root / "Disc Game").exists()
    assert client.post("/v1/list", json={"settings": {}}).json()["candidates"] == []


def test_originals_can_be_kept(client, root, tmp_path):
    made(root / "Rocket Pal (USA).nes", ROCKET)
    candidate = client.post("/v1/list", json={"settings": {}}).json()["candidates"][0]
    body = {"settings": {"keepOriginals": True}, "jobId": "k", "candidate": candidate, "stagingDirectory": "k"}
    client.post("/v1/fetch", json=body)
    assert wait(client, "k")["state"] == "completed"
    assert (root / "Rocket Pal (USA).nes").exists()
    assert (tmp_path / "staging" / "k" / "Rocket Pal (USA).nes").read_bytes() == ROCKET


def test_what_has_gone_or_is_outside_the_inbox_fails_the_fetch(client, root):
    gone = {"candidateId": json.dumps({"p": "gone.nes", "s": 1, "m": 0}), "name": "gone", "source": "Inbox"}
    client.post("/v1/fetch", json={"settings": {}, "jobId": "g", "candidate": gone, "stagingDirectory": "g"})
    assert "no longer in the inbox" in wait(client, "g")["error"]

    outside = {"candidateId": json.dumps({"p": "../staging", "s": 1, "m": 0}), "name": "x", "source": "Inbox"}
    client.post("/v1/fetch", json={"settings": {}, "jobId": "o", "candidate": outside, "stagingDirectory": "o"})
    assert wait(client, "o")["state"] == "failed"

    escape = client.post("/v1/fetch", json={"settings": {}, "jobId": "e", "candidate": gone, "stagingDirectory": "../e"})
    assert escape.status_code == 400 and escape.json()["code"] == "invalid-staging-directory"


def test_the_manifest_and_health_follow_the_contract(client, root, monkeypatch, tmp_path):
    manifest = client.get("/v1/manifest").json()
    assert manifest["type"] == "retrieval" and manifest["contractVersion"] == "1.0"
    assert set(manifest["capabilities"]) == {"list", "search", "cancel"}
    assert client.post("/v1/health", json={"settings": {}}).json()["status"] == "healthy"
    assert client.get("/v1/fetch/nope").status_code == 404

    monkeypatch.setattr(main, "inbox", Inbox(str(tmp_path / "missing")))
    assert client.post("/v1/health", json={"settings": {}}).json()["status"] == "unhealthy"
    assert client.post("/v1/list", json={"settings": {}}).status_code == 503
