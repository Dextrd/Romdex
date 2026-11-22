# Configuration

Romdex reads its settings from the environment. In Docker, the image sets the paths already; you'd only change them
when running Romdex outside Docker.

| Variable | Default (outside Docker) | What it is |
| --- | --- | --- |
| `Romdex__DataPath` | `data` | The database, settings and logs |
| `Romdex__LibraryPath` | `roms` | Your game library |
| `Romdex__DatPath` | `dats` | Your DATs |
| `Romdex__StagingPath` | `staging` | Where new dumps wait before they're filed |
| `Romdex__PluginsPath` | `plugins` | Extra in-process plugins, one folder each |
| `Romdex__CatalogueRefreshHours` | `24` | How often the catalogue is updated by itself; `0` turns it off |
| `Romdex__HashThreads` | `2` | How many files a scan hashes at once |
| `Romdex__WorkerUrl` | (none) | The igir worker, such as `http://igir-worker:8080`. Downloads need it to file anything |
| `Romdex__DownloadCheckMinutes` | `5` | How often plugins that list what arrived are asked; `0` turns it off |
| `Romdex__EmulatorUrl` | (none) | The EmulatorJS container, such as `http://emulatorjs:8080`: the first time Romdex starts, it sets up the EmulatorJS player with it. After that, change it under Settings → Plugins |
| `Romdex__ChdmanPath` | `chdman` | The `chdman` that reads CHDs, without a worker |
| `Romdex__DolphinToolPath` | `dolphin-tool` | The `dolphin-tool` that reads RVZs, without a worker |
| `Romdex__ResetLogin` | `false` | Lets you set a new admin password; see [Users and permissions](users-and-permissions.md) |

The compose file has its own, shorter settings:

- `ROMS_PATH` and `DATS_PATH`;
- `PUID` and `PGID`, the user and group that own the library, which the igir worker and the plugins run as (default
  1000);
- `ROMDEX_PORT` (default 8788) and `ROMDEX_VERSION`.

Each retriever's README has its own, such as `INBOX_PATH` for the inbox folder.
