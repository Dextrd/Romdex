# Romdex igir worker

The only thing that writes the game library. It runs [igir](https://igir.io), chdman and DolphinTool behind a small
HTTP API that only the Romdex core calls. Those tools are GPL, so they live in this container, apart from the MIT
core; the worker's own code is MIT.

The core identifies everything by hash before the worker sees it, and tells it exactly which dump to file as a DAT of
that one entry, so igir confirms a known target instead of guessing. The worker keeps nothing between calls: the
core's database is the source of truth. Calls that write run one at a time.

## Volumes and settings

| Path or variable | What it is |
|---|---|
| `/staging` (`STAGING_ROOT`) | Shared with the core and the retrievers: what's arrived, by job |
| `/roms` (`LIBRARY_ROOT`) | The game library, which only this container writes |
| `PORT` | Default 8080 |
| `CHDMAN`, `DOLPHIN_TOOL` | The tools, if not on the path |

It runs as user 1000 by default, so files it writes belong to the library's owner; set `user:` in compose otherwise.

## API

All bodies are JSON. Errors come back as `{"error": "…"}`: 400 for a bad request, 422 when igir or a tool couldn't do
it with the files given, 500 for anything else.

| Call | Body | Answer |
|---|---|---|
| `GET /v1/health` | | `{status, message}` |
| `GET /v1/version` | | `{version, igir, chdman, dolphinTool}`; a tool that isn't installed is null |
| `POST /v1/file` | `{stagingDirectory, dat, folder, mode}` | `{files, folder}` |
| `GET /v1/layouts` | | `{layouts}` |
| `POST /v1/folder` | `{datName, layout, files}` | `{folder}` |
| `POST /v1/move` | `{moves: [{from, to}]}` | `{moved: [{from, to}], failed: [{from, to, error}]}` |
| `POST /v1/convert` | `{stagingDirectory, format, source}` | `{file}` |
| `POST /v1/read` | `{root, path}` | `{contents: [{entry, size, crc32, md5, sha1}]}` |
| `POST /v1/stage` | `{path, stagingDirectory}` | `{files}` |

**File.** `stagingDirectory` is relative to `/staging`. `dat` is a Logiqx DAT holding the one dump, named as the
system's own DAT is. `folder` is where it goes, relative to `/roms`, which the core works out with **Folder**. `mode` is `move` (as it is: a loose file stays loose, an archive is moved whole and renamed),
`extract` (loose, out of any archive) or `zip`. A dump of several files gets a folder of its own. `files` are where
igir put them, relative to `/roms`.

igir only looks for headers inside an archive when it writes the files out of it, so the core asks for `zip` or
`extract` for a headered dump that came in an archive.

**Layouts.** The layouts **Folder** knows: `datName` (a folder named after the DAT), and each of igir's console tokens,
such as `romm` (RomM's folders, such as `gb` or `psx`), `es` (ES-DE's, which EmuDeck uses), `batocera`, `retrodeck`
and `mister`.

**Folder.** The folder `layout` gives a system's games, as igir's token for it would: by the DAT's name, or else by
`files`, file names of one of its games, for a system igir knows only by extension. A system the layout has no folder
for gets one named after the DAT. Relative to wherever the core keeps games; it writes nothing.

**Move.** Moves library files (relative to `/roms`) to new places in it, for formatting the library to another layout.
A file whose new place is taken stays where it is and is listed under `failed`, as is one that isn't there. Folders
the moves leave empty, or holding only what operating systems leave behind (`.DS_Store`, `Thumbs.db`, `desktop.ini`),
are removed; any other folder stays.

**Convert.** Makes a CHD (`format: chd`, from a cue sheet, gdi or iso) or an RVZ (`format: rvz`, from an iso) beside
`source` in the staging directory, and keeps the source. The core checks the result against the DAT before it lets the
source go.

**Read.** Hashes what a CHD or RVZ holds, as the DAT lists it: a CHD's tracks as chdman splits them (without the cue
sheet chdman writes, which isn't the original), or an RVZ's disc image (whose size DolphinTool doesn't give, so it's
-1). `root` is `staging` or `library`.

**Stage.** Moves a library file (`path`, relative to `/roms`) into the staging directory, so the core can take it
through Downloads again, and removes the library folders that leaves empty. `files` are relative to the staging
directory.

## Upgrading igir

igir is pinned in `package.json`. Before changing it, run the tests, which file made-up dumps with the real igir:

```bash
npm test
```

The disc tests run only where chdman is installed, as in the image:

```bash
podman build -t romdex-igir-worker:dev . && podman run --rm --entrypoint npm romdex-igir-worker:dev test
```
