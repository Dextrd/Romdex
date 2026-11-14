# Inbox folder (retriever)

Takes the dumps you drop into a folder. Romdex works out what each one is from its checksums and files it.

Each thing at the top of the inbox is one item:

- a file, such as a cartridge image or a zip;
- a folder, such as a disc's cue sheet and tracks.

Logs and checksum files beside a dump (`.log`, `.txt`, `.nfo`, `.sfv`, `.md5`, `.sha1`, `.dat`) go with it. Romdex
shows them to whoever reviews the item.

## What it does

- **list:** offers everything in the inbox once nothing in it has changed for `settleSeconds`, so a half-copied file
  isn't taken.
- **search:** when you look for a game, it offers what's in the inbox:
  - files whose CRC32 and size match the game are offered as verified. For a zip, these come from the zip's directory.
    For a loose file of 64 MB or less, the file is read, with or without a 16-byte header;
  - other files are offered by how alike their names are, if that's at least `minScore`.
- **fetch:** moves the item into the staging folder (or copies it, with `keepOriginals`).
- **cancel:** stops a fetch that hasn't started.

## Settings

| Setting | Default | |
|---|---|---|
| `keepOriginals` | `false` | Copy instead of move. |
| `settleSeconds` | `30` | How long an item must be unchanged before it's offered. |
| `minScore` | `0.5` | The lowest name match offered by a search. |

## Running it

| Variable | Default | |
|---|---|---|
| `INBOX_ROOT` | `/inbox` | The folder to watch. |
| `STAGING_ROOT` | `/staging` | The staging volume shared with the core. |
| `PARALLEL_FETCHES` | `2` | Fetches run at once. |

It listens on port 8080 and runs as user 1000. The inbox must be writable by that user, unless `keepOriginals` is on.

## Tests

```bash
pip install -r requirements.txt -r requirements-dev.txt
python -m pytest
```
