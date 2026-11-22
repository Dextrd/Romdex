# Plugins

Everything Romdex knows about games, and everywhere games come from or go to, is a plugin. Set them up under
**Settings → Plugins**. **Test** on each says whether it's working.

There are four kinds, and each does one job:

| Kind | Its job | Without one |
| --- | --- | --- |
| [Catalogue](#catalogue) | Says what exists: every verified dump of each system, from DATs | Romdex can't tell what your files are |
| [Titles and covers](#titles-and-covers) | Names, covers and what each game is: when it came out, who made it, its reviews | Games show by their DAT names, without covers or details |
| [Retrievers](#retrievers) | Bring new dumps in | Nothing new comes in through Romdex |
| [Connected apps](#connected-apps) | Tell another app, such as RomM, when the library changes | Nothing else is told |

Only the catalogue decides what you have, what's missing and what's bad. The others never change that.

## Catalogue

Index plugins say which systems and dumps exist, from No-Intro's and Redump's DATs. Your files are matched against
them by checksum. Where two have a DAT for the same system, the newest wins, by the date each was made, however each
writes it; on the same date, the one first in order wins. A headered and a headerless DAT of a system count as the
same system.

- **DAT folder** (built in, set up from the start) reads the No-Intro and Redump DATs in your DAT folder, as Logiqx
  XML or clrmamepro.
- **Redump** (built in, off until you add it) downloads Redump's DATs for the disc systems you tick, from the PlayStation to the PlayStation 5 and Xbox Series X,
  and their BIOS DATs, and checks for new ones every week. It also fetches [Retool's](https://github.com/unexpectedpanda/retool-clonelists-metadata)
  clone lists for them, unless you turn **Retool's clone lists** off.
- **libretro DATs** (built in, off until you add it) downloads No-Intro's DATs for the cartridge systems you tick, from
  the copies libretro keeps for RetroArch on GitHub, and checks for new ones every week. No-Intro doesn't let its own
  DATs be fetched automatically. libretro updates its copies every month or two, so they can trail DAT-o-MATIC's, and
  they leave out No-Intro's parent and clone links, so a game's regions are grouped by name, and Retool's clone lists
  join a game released under different names, unless you turn **Retool's clone lists** off. A newer DAT from
  DAT-o-MATIC dropped into the DAT folder wins over it.

Redump's DATs don't say which of their entries are one game under other names, such as *Crash Twinsanity* and its
Japanese release, *Crash Bandicoot 5*. Retool's clone lists do, and Romdex makes each such game one title. The DAT
folder reads them too: put a list, such as `Sony - PlayStation 2 (Redump).json`, beside its DAT.

## Titles and covers

Metadata plugins look up games by their checksums, for the names people know them by, their covers and where else
they're known, such as IGDB and Wikipedia. Then, by those, what each game is, for its page. They're asked about
games in your library, and any other game shown, and only change how those are shown. The IGDB IDs they find also
make one game of titles on other systems whose names differ. Romdex asks them in order; the first that knows each
thing is used.

- **Hasheous** (built in, off until you add it) looks games up at hasheous.org, for their names, covers, a short
  description (which Hasheous has written by AI, and says so), and links to IGDB, Wikipedia and other sites. See
  [Covers](playing-and-apps.md#covers).
- **Wikipedia** (built in, off until you add it) adds what Wikipedia and Wikidata know to a game's page: the opening
  of its article, when it came out in each region, its developer, publisher, genres, series and credits, the
  scores in its article's review box, its age ratings, and the other games of its series that your catalogue has, on
  any system. It finds games by the article Hasheous links them to, so
  it needs Hasheous.
- **libretro thumbnails** (built in, off until you add it) adds a title screen and a screenshot to a game's page,
  from the pictures RetroArch uses. It finds them by the names No-Intro's and Redump's DATs give games, reading the
  list of what libretro has for a system once a week, so it sends nothing about your library.

- **TheGamesDB** (built in, off until you add it) looks games up at thegamesdb.net, by their checksums or the ID
  Hasheous links them to, for their covers, what they're about, release date, developer, publisher, genres, ESRB
  rating, a title screen and up to six screenshots. It needs an API key of your own: make an account on
  [TheGamesDB's forum](https://forums.thegamesdb.net) and ask for one in its API section. A key allows so many lookups
  a month, which Romdex keeps the answers to; the plugin's health on **Plugins** says how many are left.
- **ScreenScraper** (built in, off until you add it) looks games up at screenscraper.fr, by their checksums and file
  names or the ID Hasheous links them to, for their covers, what they're about, release dates, developer, publisher,
  genres, age ratings, a title screen and a screenshot. ScreenScraper only answers programs it has given a developer
  ID, on [its forum](https://www.screenscraper.fr/forumsujets.php?frub=12&numpage=0), so for now you need one of your
  own. Your screenscraper.fr account is optional: with it you get more lookups a day, and are let in when it's busy.

Hasheous, Wikipedia and libretro thumbnails need no account.

## Retrievers

Retrievers bring dumps in, as Melodex's bring music. One can search for a game you found under
**Search** and fetch it, or hand over files that arrived by themselves. Each runs in its own container and speaks a
small HTTP contract, so anyone can [write one](writing-a-retriever-plugin.md) in any language. Every file is checked by
its hash before it's filed, whatever the plugin says it is.

None is built in. Each retriever has a repository of its own, whose README has the lines to paste under `services:`
in the compose file you run Romdex with, and what else it needs; then `docker compose up -d` starts it.

| Retriever | Brings in | Needs |
| --- | --- | --- |
| [Inbox folder](https://github.com/dextrd/romdex-retriever-inbox) | What you drop into a folder, one dump per file or folder. It also finds games in it when you search, by checksum or by name. | A folder |
| [Dumping tools](https://github.com/dextrd/romdex-retriever-dumps) | Finished dumps from disc rippers' and cartridge dumpers' output folders, with their logs. A disc is taken once every track is there. | Your tools' output folder |
| [Prowlarr](https://github.com/dextrd/romdex-retriever-prowlarr) | What your torrent indexers offer, with your qBittorrent: just the files of the dump you asked for, even from a whole set | Your own Prowlarr and qBittorrent |

Once it's running, add it under **Add**, where each is a template with its **plugin address** filled in, and fill in
its settings. That's the address of the retriever's own container, such as `http://retriever-prowlarr:8080`, not of
the service it uses: Prowlarr's or qBittorrent's address goes in the retriever's settings once it's added. Each
retriever has its own releases: `latest` follows them, or name a version such as `1` to choose when it changes.

You're responsible for the retrievers you run, and for what they fetch.

See [Downloads and filing](downloads-and-filing.md) for what happens to what they bring.

## Connected apps

Connectors tell another app, such as RomM, when the library changes. They're optional; one is used at a time.

- **RomM** (built in) asks RomM to scan what's filed, and adds **Open in RomM** to a game's page. See
  [Opening games in another app](playing-and-apps.md#opening-games-in-another-app).

Next: [Downloads and filing](downloads-and-filing.md).
