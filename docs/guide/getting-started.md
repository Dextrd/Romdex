# Getting started

## Setting up

The first time you open Romdex, it walks you through setting up, full screen, before anything else. The numbered
circles along the bottom show where you are. Once the admin account is made, any step can be gone back to or skipped,
and everything in it can be changed later under **Settings**. Until the admin finishes, nobody else gets in.

1. **People.** First the admin account. The admin may do everything, and only they can change their own account.
   Then add anyone else who'll use Romdex, with only the permissions they need. Everyone signs in, even at home; tick
   **Remember me** to stay signed in on that browser for 30 days.
2. **Games.** Romdex checks it can read your ROM library and counts what's there, then counts the DATs in your DAT
   folder. If either is empty, check `ROMS_PATH` and `DATS_PATH`.
3. **Plugins.** The DAT folder catalogue is set up already. Test it to see which systems it found. See
   [Plugins](plugins.md).
4. **Apps and webhooks.** API keys for other apps, and webhooks to tell them when things happen. Most people can skip
   this.
5. **Look.** How Romdex looks for everyone. Each person can choose their own later, under **Settings → Appearance**.

## Where DATs come from

A DAT lists every verified dump of one system, with the size and checksums of each file. That's how Romdex knows what
a file is without trusting its name.

- **No-Intro** covers cartridge systems, such as the Game Boy and the SNES. Download its DATs from
  [DAT-o-MATIC](https://datomatic.no-intro.org).
- **Redump** covers disc systems, such as the PlayStation and the GameCube. Download its DATs from
  [redump.info](https://redump.info).

Drop them into your DAT folder as they come, zipped or not. Or have them fetched for you: the Redump plugin fetches
Redump's, and the libretro DATs plugin fetches No-Intro's as libretro keeps them for RetroArch. See [Plugins](plugins.md).

When there are two DATs for the same system, from your DAT folder or a plugin, Romdex uses the newest, by the date
each was made. On the same date, the catalogue first in order wins, which is the DAT folder unless you move it.

## Systems

Any system with a DAT works. These Romdex knows by name, so they get short names in lists, such as PS2, folders in each
[library layout](downloads-and-filing.md#the-librarys-layout) and the storage formats that suit them:

| Maker | Systems | DATs from |
|---|---|---|
| Nintendo | NES, Famicom Disk System, SNES, Nintendo 64, 64DD, Game Boy, Game Boy Color, Game Boy Advance, Virtual Boy, DS, DSi, 3DS | No-Intro |
| Nintendo | GameCube, Wii, Wii U | Redump |
| Nintendo | Switch | No-Intro no longer publishes it; a Switch DAT from elsewhere works |
| Sega | Master System, Game Gear, Mega Drive and Genesis, 32X | No-Intro |
| Sega | Mega CD and Sega CD, Saturn, Dreamcast | Redump |
| Sony | PlayStation, PlayStation 2, 3, 4 and 5, PlayStation Portable | Redump |
| Sony | PlayStation Vita, and the PlayStation 3's and Portable's downloads | No-Intro |
| Microsoft | Xbox, Xbox 360, Xbox One, Xbox Series X | Redump |
| NEC | PC Engine and TurboGrafx-16 | No-Intro |
| NEC | PC Engine CD and TurboGrafx-CD, PC-FX | Redump |
| SNK | Neo Geo Pocket and Pocket Color | No-Intro |
| SNK | Neo Geo CD | Redump |
| Bandai | WonderSwan and WonderSwan Color | No-Intro |
| Atari | 2600, 7800, Lynx, Jaguar | No-Intro |
| Others | ColecoVision (No-Intro), 3DO (Redump) | |

Discs that nothing plays from a CHD, such as the Xbox's, the PlayStation 3's and the Wii U's, are kept as they come.
Most cartridge systems up to the DS, and the PlayStation, Mega CD and PSP, play in the browser: see
[Playing, covers and apps](playing-and-apps.md). Nothing plays the PlayStation 4 and 5's or the Xbox One and Series
X's dumps, but Romdex checks and files them like any other.

## Your library

**Library**, the home page, has two views. **Systems** lists every system you have games for; **Games** lists every
game in the library together, to filter and search. **Scan**, at the top, reads your library, and
**Update the catalogue**, in the menu beside it, reads the DATs again (Romdex also does this by itself once a day). A scan hashes each file once; later scans only hash
what's new or changed.

A file counts only when its checksums match a dump in the DAT, whatever it's called. Files can be loose, zipped,
7-Zipped or in CHDs. A disc counts when every track does, and a game of several discs when every disc does.

- **Verified**: every file of a dump of the game matches the DAT.
- **Partial**: you have some discs of it, but not all.
- **Bad**: you have files of it that don't verify, such as a disc with a track missing, or a file named like a dump
  but with other checksums.

A game's page shows what it is, when the Hasheous and Wikipedia plugins are on: what it's about, when it came out,
who made it, what reviewers gave it, its age rating, and links to it on IGDB and other sites; with the libretro
thumbnails plugin, its title screen and a screenshot. Whose age ratings it shows is yours to choose under
**Settings → Appearance**. Below are the dumps you have and the
others the DAT lists, such as other regions and revisions: each shows where it came out and in what languages, and
opens to show its serial, files, checksums and where it is in your library. **Play** plays it in the browser, and
**Open in RomM** opens it there: see [Playing, covers and apps](playing-and-apps.md).

**Search** finds any game the DATs list, whether or not you have it. Words can come in any order, and regions and
versions count too, so *kart japan* finds the Japanese releases of a kart game. A game named just what you searched
for comes first, then the most widely released, counting the regions it came out in on each system, so *sonic* puts
Sonic the Hedgehog before a Japan-only spin-off. Games are named as they're written, *The Legend of Zelda: Majora's
Mask*, where the DATs put the article last and a dash for the colon. A game on more than one system, such
as *Sonic Heroes* on the GameCube and the PlayStation 2, is listed once, with each system it's on, and counts as owned
if you have it on any. Its page has a switch with each system it's on, green where you have a copy: pick one to see
the game there, with the review scores given for that system. Scores given without saying which system they're for
show on every system's page.

The Library's games, a system's and Search can each be a **Grid** of covers or a **List**, as you choose beside them;
the choice is kept for every list in this browser. In the grid, a game's tile is green when you have it and red when
you don't.

**Review** lists what the DATs don't explain: files that match nothing, bad and outdated dumps, incomplete dumps,
files that couldn't be read, and fetches that didn't bring what they were for. See [Review](review.md).

Reading inside a CHD needs `chdman`, and inside an RVZ needs `dolphin-tool`. Until they're there, such files wait to be
read, and Review says how many.

Next: [Users and permissions](users-and-permissions.md).
