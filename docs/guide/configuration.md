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
| `Romdex__HashThreads` | `2` | How many files a scan hashes at once |
| `Romdex__WorkerUrl` | (none) | The igir worker, such as `http://igir-worker:8080`. Downloads need it to file anything |
| `Romdex__DownloadCheckMinutes` | `5` | `0` stops plugins that list what arrived being asked by themselves; how often they are is under [Scheduled tasks](#scheduled-tasks) |
| `Romdex__ScheduledTasks` | `true` | `false` stops [scheduled tasks](#scheduled-tasks) running by themselves |
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

## Fetching

**Settings → Fetching** sets how many games fetch at once: two to start with, up to eight. Each region of a game you
get is a request of its own; the rest wait their turn, in the order asked for, under **Activity → Fetches**. There
each can be moved to the front, up or down, paused, resumed or cancelled, and **Pause all** and **Cancel all** act on
everything shown. Pausing one that's fetching stops that fetch; resumed, it fetches that offer again from the start.

## Scheduled tasks

**Settings → Scheduled tasks** has three schedules, **Daily**, **Weekly** and **Monthly**, each a window between two
times, and below each the tasks it runs: tick a task to put it on that schedule. A task is on one schedule at most, so
ticking it on one takes it off another; one on none only runs when you start it. It's as in Melodex.

- **Scan the library**: hash new and changed files and match them against the DATs.
- **Update the catalogue**: read the DATs again, and what the catalogue plugins know.
- **Retry failed fetches**: look again for what you asked for that ran out of offers, passing over what was turned
  away.

To start with, the daily window is 01:00 to 06:00 and scans and updates the catalogue; the weekly one is Mondays at the
same times, for failed fetches; nothing is monthly.

- **Each window has its own times.** One that ends earlier than it starts runs past midnight: 22:00 to 04:00 ends the
  next morning. One that ends when it starts lasts a whole day.
- **The weekly one runs on a day you pick, the monthly one on a day of the month, 1 to 31.** In a month shorter than
  that, it runs on the month's last day: 31 is the last day of every month.
- **All three are in one time zone**, the server's UTC to start with: **Use this browser's** sets yours.
- **A window runs its tasks once, in order**: scan, catalogue, then failed fetches. The first two are tasks on
  **Activity**, one after another; failed fetches wait for them.
- **Scheduled fetches go a few at a time**, five to start with (Settings → Fetching): as each finishes, the next is looked for in its place. When the
  window closes, nothing more is looked for; the rest wait for the next window, and those under way carry on.
- **The windows take turns.** When two open on the same day, daily goes first, then weekly, then monthly, as long as
  its window is still open.

Below each schedule are when it next runs, and how its last run went, a line for each task. Every task can still be run
straight away: **Scan** and **Update the catalogue** from the Scan menu, and failed fetches from **Review → Couldn't
fetch**, with **Look again for all**.

**Checking for arrivals**, below the schedules, is how often retrievers that hand over what arrives by themselves, such
as the inbox folder, are asked: every 5 minutes to start with, all day rather than in a window.

