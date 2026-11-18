# Changelog

## 0.1.0 (unreleased)

- Romdex's shell, from Melodex: household accounts, API keys and webhooks, background tasks, the activity log, and
  the `/api/v1` API with its reference at `/api/docs`.
- Plugins: index, retrieval and connector contracts, and the built-in DAT folder catalogue, which reads No-Intro and
  Redump DATs, zipped or not.
- The web app: first-run setup, Activity, and Settings.
- The catalogue: DATs imported by system and grouped into titles, updated daily, with IDs that survive a new DAT
  version. The Redump plugin fetches Redump's DATs and BIOS DATs.
- The library: every file hashed once (CRC32, MD5 and SHA-1, with and without a header), inside zips, 7z and CHDs, and
  matched against the catalogue.
- The Library, with Systems and Games views, a system's games, a game's dumps, and Review: what's verified, partial
  and bad. The API also has what's missing after 1G1R preferences, ignoring and choosing a dump, and an igir-shaped
  report per system (`/systems/{id}/report.csv`).
- Search: every game the DATs list, in the library or not, by system and by whether you have it.
- The retrieval contract gains search and fetch, as Melodex's has, for retrievers that bring dumps in.
- `tools/compare-igir.py`, to check Romdex's report against igir's.
- Downloads: dumps arrive through retrievers, are identified by hash and filed in the library, and whatever can't be
  verified waits on the new Downloads page to be accepted, rejected or retried. They show on the Activity page too.
- The igir worker, the only thing that writes the library: it files each dump with igir, in RomM's folders or the
  DAT's, and makes and reads CHDs (chdman) and RVZs (DolphinTool). It's GPL, so it's a container of its own.
- Each system's filing, on its page: the folder layout, the storage format (CHD and RVZ checked against the DAT before
  the original goes), and what to do with another dump of a game you have. **Check the library** reports which of
  its games aren't where Downloads would file them, without moving anything.
- **Get** on Search and on a game's page asks the plugins that search, and fetches what you choose.
- Review's **Send to Downloads** takes a file that matches nothing out of the library, to be identified again or rejected.
- Two retrievers: the inbox folder, which also searches, and dumping tools, which takes finished dumps from disc
  rippers' and cartridge dumpers' output with their logs. The compose file runs the worker and the inbox, with
  dumping tools as the `dumps` profile.
- The retrieval contract gains `list`, for plugins that hand over what arrived by themselves, and candidates gain notes.
- Guide pages: downloads and filing, and writing a retriever.
- Play in the browser: **Play** on a game's page opens it in EmulatorJS, a container of its own, through a link
  that's good for 12 hours. A disc of several files goes to it as one zip. A system's page lists the BIOS files its
  emulator looks for, each known by its checksum, and whether you have them. Every dump in a DAT of BIOS files, such as
  Redump's, counts as a BIOS, whether or not the DAT marks it as one.
- The Hasheous plugin looks up the games you have by checksum, for covers and the names people know them by. Covers
  show on a game's page and in a system's list.
- The RomM connector: each dump filed is passed to RomM, which scans only that system, and **Open in RomM** shows on a
  game's page. Telling RomM runs beside the task queue, so a slow scan holds up nothing else.
- `tools/play-gate.py` and `tools/romm-gate.compose.yml`, to check playing and RomM on a gate install, with
  homebrew games. The gate's made-up DATs now carry real system names, so they map to emulators and RomM's folders.
- The top bar has **Scan**, with **Update the catalogue** in its menu, as Melodex has; the pages are in Melodex's
  order, and Search filters by owned, all and missing in the library's colours.
