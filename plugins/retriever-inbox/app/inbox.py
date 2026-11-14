"""The inbox folder: what's been dropped into it, offered as it settles, and moved to staging when the core asks.

Each thing at the top of the folder is one item: a file, such as a cartridge image or a zip, or a folder, such as a
disc's cue sheet and tracks. The core works out what it is by hash; the inbox only says what's there.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import time
import zipfile
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .contract import Candidate, Job, Target

# Never game data, and things systems leave behind.
SKIPPED = {".ds_store", "thumbs.db", "desktop.ini"}
NOTES = {".log", ".txt", ".nfo", ".md5", ".sha1", ".sfv", ".dat"}
NOTES_LIMIT = 4000
# Files no bigger than this are checksummed to tell whether they're what a search wants.
CHECKSUM_LIMIT = 64 << 20


@dataclass(frozen=True)
class Item:
    path: str  # relative to the inbox
    name: str
    size: int
    changed: float  # the newest modified time of anything in it
    files: tuple[Path, ...]

    @property
    def candidate_id(self) -> str:
        # Changes when the item does, so something replaced is offered again.
        return json.dumps({"p": self.path, "s": self.size, "m": int(self.changed)}, separators=(",", ":"))


class Inbox:
    def __init__(self, root: str):
        self.root = Path(root)

    def items(self, settle_seconds: float) -> list[Item]:
        """What's in the inbox, oldest first, leaving out anything changed in the last settle_seconds: still copying in."""
        found = []
        now = time.time()
        for entry in sorted(self.root.iterdir(), key=lambda p: p.name):
            if entry.name.startswith(".") or entry.name.lower() in SKIPPED:
                continue
            files = tuple(sorted(f for f in ([entry] if entry.is_file() else entry.rglob("*"))
                                 if f.is_file() and not f.name.startswith(".") and f.name.lower() not in SKIPPED))
            # A log or checksum file on its own is nothing to file; it goes with whatever it's put beside.
            if not files or all(f.suffix.lower() in NOTES for f in files):
                continue
            stats = [f.stat() for f in files]
            changed = max(s.st_mtime for s in stats)
            if now - changed < settle_seconds:
                continue
            found.append(Item(entry.name, entry.stem if entry.is_file() else entry.name, sum(s.st_size for s in stats), changed, files))
        return sorted(found, key=lambda i: i.changed)

    def candidate(self, item: Item, score: float | None = None, verified: bool | None = None, dump_name: str | None = None) -> Candidate:
        formats = sorted({f.suffix.lower().lstrip(".") for f in item.files if f.suffix.lower() not in NOTES})
        return Candidate(candidate_id=item.candidate_id, name=item.name, source=f"{item.path} in the inbox", size=item.size,
                         format=",".join(formats) or None, notes=self.notes(item), score=score, verified=verified, dump_name=dump_name)

    def notes(self, item: Item) -> str | None:
        """What a dumper or ripper wrote beside the files, such as its log, for whoever reviews the item."""
        texts = []
        for file in item.files:
            if file.suffix.lower() in NOTES and file.stat().st_size <= 1 << 20:
                text = file.read_text(errors="replace").strip()
                if text:
                    texts.append(f"{file.relative_to(self.root).as_posix()}:\n{text}")
        joined = "\n\n".join(texts)
        return joined[:NOTES_LIMIT] + ("\n…" if len(joined) > NOTES_LIMIT else "") if joined else None

    def search(self, target: Target, limit: int, settings: dict[str, Any]) -> list[Candidate]:
        """Items that look like the target: by checksum where that's cheap, else by name."""
        wanted = [d for d in target.dumps if not target.dump_name or d.name == target.dump_name]
        by_crc = {(f.crc32.lower(), f.size): d.name for d in wanted for f in d.files if f.crc32}
        names = {d.name.lower(): d.name for d in wanted}
        found = []
        for item in self.items(float(settings.get("settleSeconds", 30))):
            dump = next((by_crc[c] for c in self._checksums(item, {s for _, s in by_crc}) if c in by_crc), None)
            if dump:
                found.append(self.candidate(item, 1.0, True, dump))
                continue
            score, best = max(((similarity(item.name, n), n) for n in names.values()), default=(0.0, None))
            title = similarity(item.name, target.title)
            if max(score, title) >= float(settings.get("minScore", 0.5)):
                found.append(self.candidate(item, round(max(score, title * 0.9), 3), None, best if score >= title else None))
        return sorted(found, key=lambda c: c.score or 0, reverse=True)[:limit]

    @staticmethod
    def _checksums(item: Item, sizes: set[int]) -> list[tuple[str, int]]:
        """CRC32 and size of the files in an item that are a size the target has: a zip's from its directory, a loose
        file's by reading it, if it isn't big."""
        found = []
        for file in item.files:
            if file.suffix.lower() == ".zip":
                try:
                    with zipfile.ZipFile(file) as z:
                        found += [(f"{i.CRC:08x}", i.file_size) for i in z.infolist() if i.file_size in sizes]
                except zipfile.BadZipFile:
                    pass
            else:
                size = file.stat().st_size
                # A cartridge with a 16-byte header is listed without it.
                for skip in (0, 16):
                    if size - skip in sizes and size <= CHECKSUM_LIMIT:
                        with file.open("rb") as f:
                            f.seek(skip)
                            found.append((f"{zlib.crc32(f.read()):08x}", size - skip))
        return found

    def fetch(self, job: Job, candidate_id: str, directory: Path, settings: dict[str, Any]) -> list[dict[str, Any]]:
        data = json.loads(candidate_id)
        source = (self.root / data["p"]).resolve()
        if self.root.resolve() not in source.parents:
            raise ValueError("The candidate points outside the inbox.")
        if not source.exists():
            raise FileNotFoundError(f"{data['p']} is no longer in the inbox.")
        target = directory / source.name
        job.check()
        if settings.get("keepOriginals", False):
            shutil.copytree(source, target) if source.is_dir() else shutil.copy2(source, target)
        else:
            shutil.move(source, target)
        files = [target] if target.is_file() else sorted(f for f in target.rglob("*") if f.is_file())
        for f in files:
            # The core and the igir worker run as other users, and need to read and move these.
            os.chmod(f, 0o666)
        for d in [target, *[p for p in target.rglob("*") if p.is_dir()]] if target.is_dir() else []:
            os.chmod(d, 0o777)
        return [{"path": f.relative_to(directory).as_posix(), "size": f.stat().st_size} for f in files]


_WORD = re.compile(r"[a-z0-9]+")


def similarity(a: str, b: str) -> float:
    """How alike two names are, from 0 to 1, by their words: tags in brackets count, case and punctuation don't."""
    wa, wb = set(_WORD.findall(a.lower())), set(_WORD.findall(b.lower()))
    if not wa or not wb:
        return 0.0
    return len(wa & wb) / len(wa | wb)
