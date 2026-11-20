# Changelog

What changed in each release of Romdex. Versions follow [semantic versioning](https://semver.org).

## [1.0.0] - 2026-11-18

The first release. Romdex knows what every file in your game library is, by its hash against the No-Intro and Redump
DATs, and what's missing after your 1G1R preferences. It brings new dumps in from a folder or your dumping tools,
files them with igir, plays games in the browser with EmulatorJS, and opens them in RomM. It installs from one
compose file: see [installing](docs/guide/installing.md).

### New

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
- Search: every game the DATs list, in the library or not, by system and by whether you have it. A game on several
  systems is one result, owned if you have it on any, and its page lists each system's version. Names count as the
  same whatever their punctuation, and titles with the same IGDB ID from a metadata plugin are one game too.
- Grid and list layouts for the Library's games, a system's and Search, with tiles coloured for owned and missing,
  and short system names, such as PS2 and GC, in lists.
- A game's page has what it is, from a new Wikipedia plugin and Hasheous: what it's about, its release dates, who
  made it, its genres and credits, review scores with the reviewers' logos, its age rating, and links to IGDB and
  other sites. A new libretro thumbnails plugin adds its title screen and a screenshot. New TheGamesDB and
  ScreenScraper plugins look games up with your own API key or developer ID, for covers, details, ratings and
  screenshots. What's found shows as it comes in, plugins are asked all at once, and reviews, screenshots and the
  other games of its series (from Wikidata, on any system the catalogue has them) are rows that scroll sideways. A
  switch at the top moves between the game's systems, green where you have a copy, and reviews show only the scores
  given for the system you're on. Its dumps open to show their
  files and checksums, and show where they came out and in what languages at a glance.
- Settings → Appearance chooses whose age ratings games' pages show: ESRB's, PEGI's, USK's, CERO's or ACB's, or
  by the browser's language.
- Retool's clone lists, fetched with Redump's DATs or read from the DAT folder, make one title of a game released
  under other names, such as Crash Twinsanity and Crash Bandicoot 5.
- The retrieval contract gains search and fetch, as Melodex's has, for retrievers that bring dumps in.
- `tools/compare-igir.py`, to check Romdex's report against igir's.
- Downloads: dumps arrive through retrievers, are identified by hash and filed in the library, and whatever can't be
  verified waits on the new Downloads page to be accepted, rejected or retried. They show on the Activity page too.
- The igir worker, the only thing that writes the library: it files each dump with igir, and makes and reads CHDs
  (chdman) and RVZs (DolphinTool). It's GPL, so it's a container of its own.
- The library's layout, under Settings: system folders named as RomM, ES-DE and EmuDeck, Batocera, RetroDECK or MiSTer
  name them, or as the DATs are; a games folder, such as `roms`; and a BIOS folder, by system or all together.
  **Match my library** works it out from where your games are. Everything Downloads files follows it.
- Formatting the library: a preview of every game that would move to where the layout puts it, renamed as the DAT
  names it, then the moves, once confirmed, for the whole library or one system. Folders left empty, or with only
  `.DS_Store` or `Thumbs.db` in them, go; files that match nothing stay where they are.
- Each system's filing, on its page: its own layout or folder, the storage format (CHD and RVZ checked against the DAT
  before the original goes), and what to do with another dump of a game you have.
- **Get** on Search and on a game's page asks the plugins that search, and fetches what you choose.
- Review's **Send to Downloads** takes a file that matches nothing out of the library, to be identified again or rejected.
- Three retrievers, each in a repository of its own (`dextrd/romdex-retriever-<name>`) with its own releases and
  image, as Melodex 2.0's are: the inbox folder, which also searches; dumping tools, which takes finished dumps from
  disc rippers' and cartridge dumpers' output with their logs; and Prowlarr, which searches your torrent indexers and
  downloads just the game's files with your qBittorrent, with the same settings as Melodex's. The compose file is just
  Romdex: each retriever's README has the lines to add, and the guide's Plugins page lists them.
- The retrieval contract gains `list`, for plugins that hand over what arrived by themselves, and candidates gain notes.
- Guide pages: downloads and filing, and writing a retriever.
- Play in the browser: **Play** on a game's page opens it in EmulatorJS, a container of its own, through a link
  that's good for 12 hours. A disc of several files goes to it as one zip. A system's page lists the BIOS files its
  emulator looks for, each known by its checksum, and whether you have them. Every dump in a DAT of BIOS files, such as
  Redump's, counts as a BIOS, whether or not the DAT marks it as one.
- Plugins for titles and covers, a kind of their own (`metadata`, beside `index`, `retrieval` and `connector`): they
  look up the games you have by checksum, and only change how those are shown, never what exists. The Hasheous one
  finds covers and the names people know games by. The games you have are looked up ahead; any other, such as one in
  Search, the first time it's shown. Every game has a cover's place, in lists, Search and its page, that shimmers
  while its cover is looked up and shows a placeholder when there's none.
- Settings → Plugins and the plugins guide say what each kind of plugin does, and what's lost without one.
- The RomM connector: each dump filed is passed to RomM, which scans only that system, and **Open in RomM** shows on a
  game's page. Telling RomM runs beside the task queue, so a slow scan holds up nothing else.
- `tools/play-gate.py` and `tools/romm-gate.compose.yml`, to check playing and RomM on a gate install, with
  homebrew games. The gate's made-up DATs now carry real system names, so they map to emulators and RomM's folders.
- The top bar has **Scan**, with **Update the catalogue** in its menu, as Melodex has; the pages are in Melodex's
  order, and Search filters by owned, all and missing in the library's colours.
- Download events: `download.filed`, `download.needs_review` and `download.failed`, on `GET /events` and to webhooks,
  beside `task.completed` and `task.failed`.
- The [client guide](docs/client-guide.md), for apps that use Romdex's API, and an OpenAPI document in which every
  operation is named, described and grouped.
- Published images for amd64 and arm64 on the GitHub Container Registry: `romdex`, `romdex-igir-worker`,
  and `romdex-emulatorjs`.
