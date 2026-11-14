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
| `POST /v1/file` | `{stagingDirectory, dat, layout, mode}` | `{files, folder}` |
| `POST /v1/folder` | `{datName, layout, files}` | `{folder}` |
| `POST /v1/convert` | `{stagingDirectory, format, source}` | `{file}` |
| `POST /v1/read` | `{root, path}` | `{contents: [{entry, size, crc32, md5, sha1}]}` |
| `POST /v1/stage` | `{path, stagingDirectory}` | `{files}` |

**File.** `stagingDirectory` is relative to `/staging`. `dat` is a Logiqx DAT holding the one dump, named as the
system's own DAT is. `layout` is `romm` (a folder per platform as RomM names them, such as `gb` or `psx`, the folder
igir's `{romm}` token gives; a system igir doesn't know goes in a folder named after the DAT) or `datName` (a folder
named after the DAT). `mode` is `move` (as it is: a loose file stays loose, an archive is moved whole and renamed),
`extract` (loose, out of any archive) or `zip`. A dump of several files gets a folder of its own. `files` are where
igir put them, relative to `/roms`.

igir only looks for headers inside an archive when it writes the files out of it, so the core asks for `zip` or
`extract` for a headered dump that came in an archive.

**Folder.** The folder `layout` gives a system's games, as **File** would choose it, relative to `/roms`. `files` are
file names of one of its games, for a system igir knows only by extension. It writes nothing.

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
