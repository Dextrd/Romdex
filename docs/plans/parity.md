# Plan: parity with Melodex

Romdex and Melodex are built on the same framework and should match wherever their jobs allow. Romdex forked from
Melodex at about 1.5; Melodex has since reached **2.0.0**, which is the version matched here. Melodex is settled, so
Romdex is brought up to it by hand first, and the shared foundation is built afterwards from what turns out to be the
same in both.

## How a piece is brought over

1. **Port faithfully.** Keep Melodex's file names, class and method names, CSS classes, routes and API shapes. Change
   only what the domain forces: songs for games, Melodex for Romdex. Two copies that differ only where they must are
   what makes the later extraction mechanical.
2. **Keep what must differ in one place.** Where Romdex needs something of its own, such as a settings section for
   regions or its own scheduled tasks, it goes in one marked spot rather than threaded through the file. Those spots
   become the extension points of the shared foundation.
3. **Use Melodex's words.** Fetch, not download; Activity → Fetches, not a Downloads page; the same names for the same
   things everywhere.
4. **Check it in a browser**, at a computer's width and a phone's, as each piece lands.

When every piece below is done, a script compares each pair of files with the names swapped. Files that come out the
same, or nearly, are what the foundation holds; the rest stay with their app.

## A. The shared framework: match exactly

| | Piece | Melodex | Status |
|---|---|---|---|
| 1 | Settings in sections: your account first, then Library, Web app, Server and the app; no General page | 2.0 (#238) | Done, with Fetching, Scheduled tasks, Notifications and the log's "Keep tasks' lists" in their sections |
| 2 | Scheduled tasks: daily, weekly and monthly windows, each ticking what it runs; the catalogue update its own task, not after a scan | 2.0 (#112, #238) | Done: scan, catalogue update, retry failed fetches; Romdex's arrival check in place of playlist sync. Wishlist and upgrades join with B1 and B4; Retry failed moves to Activity → Fetches with 4, and the scheduled fetches setting to Fetching with 8 |
| 3 | Notifications at the bottom right, and which kinds you see under Settings → Notifications | 1.6 (#178) | Done: long tasks, and fetches asked for here (RequestTracker's notifications; the rest of it comes with 4). Appearance is one panel with a You / Everyone switch, as in 1.6 (#197) |
| 4 | Activity in Running, Queued and Completed sections; Pause all and Cancel all; a Fetches tab of requests and their attempts, replacing the Downloads page | 1.5.2–2.0 (#175, #176, #238) | In four steps. 1, done: tabs, Recent / Earlier, the sections, filter and sort, Pause all and Cancel all for tasks, landing on what a link names; Fetches lists requests and what arrived by itself, and the Downloads page goes there. 2, done: a request's attempts, opened in place one at a time, and the whole RequestTracker, shared by every page, so a game's page shows **Fetching** in place of Get. Not brought over: Melodex's list of every candidate a retriever offered, with **Fetch this**, as Romdex doesn't keep the offers it passed over. 3, done: the request queue: Queued and Paused, moved to the front, up and down, Pause all and Cancel all for fetches too, and how many fetch at once under Settings → Fetching (8). 4, done: a task's own list of what it did (TaskFeed, TaskArchive, TaskRowList), for scans, formats, catalogue updates and cover lookups, and the log's "Keep tasks' lists". Scans use the same list as the rest, where Melodex's have their own (ScanFeed, ScanFiles), as a scan's rows are files and the games they match |
| 5 | Users: editing someone changes what they may do and can set a new password; the rest is theirs on their account | 2.0 (#238) | Done: UserEditor, as Melodex's, without its media server logins; Users and Your account use it. The API already matched |
| 6 | API keys: everyone makes their own; the admin sees other people's and can revoke them; the permission that was API keys and webhooks is Webhooks | 2.0 (#7, #238) | |
| 7 | A Tools page in sections: Import / Export, Maintenance, Formatting (formatting the library moves there) | 2.0 (#23, #237) | |
| 8 | Settings → Fetching: how many fetches at once, and retrying failed ones | 2.0 (#112) | Done with 4's third step: fetches at once (2 to start with, as Melodex's), and scheduled fetches at a time. Melodex's file matching and listen-now settings have no Romdex counterpart |
| 9 | Polish: errors that say what went wrong; shimmers where things load; copy buttons over plain http; red outlines on what can't be undone; no settings button on plugins with nothing to set; sections that open and close and are remembered | 1.6–2.0 | |

## B. Melodex's features, adapted to games

| | Feature | In Romdex | Status |
|---|---|---|---|
| 1 | Wishlist, with a scheduled **Get them all** | Games you'd like; also where "get everything missing" for a system lives | |
| 2 | Stars and ratings | Favourite and rate games | |
| 3 | Listening history | Play history from the browser player: recently and most played | |
| 4 | Upgrades, looked for on a schedule | A newer revision or better dump in the DAT, fetched to replace what you have; Review's Outdated tab is the start of it | |
| 5 | Import / Export on Tools | Export the library as a list; import a list of games into the wishlist | |

## C. Music only: not brought over

Subsonic players, radio, For you and its mixes, discovery plugins and similar artists, albums and songs, gapless
playback, even volume, lyrics, the visualiser and spectra, scrobbling, genres from MusicBrainz, and Listen now.
For you and similar games were considered and left out.

## Found in Melodex along the way

Bugs noticed in Melodex while porting, fixed in Romdex; Melodex gets the fix when the foundation is shared.

- Notifications' icons have no stroke style, so they draw filled rather than in lines (`.toast-glyph svg`).
- The scheduled-task note loses the space before its **Scheduled tasks** link: `<text> </text>` renders nothing (`ScheduleNote.razor`).

## Keeping up

Each Melodex release, read its changelog and add what applies to the tables above. The version matched is at the top.
