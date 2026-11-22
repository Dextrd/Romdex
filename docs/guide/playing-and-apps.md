# Playing, covers and apps

## Playing in the browser

**Play**, on a dump you have in a game's page, opens it in the browser, in a [player](plugins.md#players): EmulatorJS
unless you've added another. Romdex hands the emulator the
library's own file, so a dump plays only once it's verified. Someone needs the Play permission. The link is good for
12 hours, and needs no sign-in, so it can be opened on a TV's browser or a phone.

EmulatorJS is GPL, so it's a container of its own, `emulatorjs` in the compose file, and a plugin under **Settings →
Plugins → Players**, set up for you the first time Romdex starts. Romdex passes its files on, so the games and the
emulator come from the same place and nothing else needs opening. Without a player, games say that none is set up;
with the container stopped, that EmulatorJS isn't working, and why.

It plays the Game Boy, Game Boy Color and Advance, NES and Famicom Disk System, SNES, N64, Virtual Boy, DS, the Sega
consoles up to the 32X and Mega-CD, PlayStation, PSP, PC Engine, Atari, WonderSwan, Neo Geo Pocket and ColecoVision.
Not the GameCube, the PS2 or later: open those in a connected app instead (below). A disc of several files goes to
the emulator together. A game stored as RVZ can't be played in the browser; one in a CHD can.

The browser holds the whole game in memory while it plays. For a CD, a CHD is best: it's smaller, and the browser
doesn't have to unpack it first, as it does a zip, so it needs about twice the game's size rather than several times
it. On a computer or phone with little memory, set the system's storage format to CHD (see
[Downloads and filing](downloads-and-filing.md)).

The emulator draws with WebGL, which most browsers have. If a browser has it turned off, such as one with hardware
acceleration off, the player says so.

### BIOS files

Some systems need their console's BIOS, and some play better with it. A system's page lists what its emulator looks
for: **Ready** when you have one, **Needed** when its games won't play without one, **Optional** when the emulator
manages without. Put BIOS files in the library as you would games. Romdex knows each one by its checksum, so it uses
only a BIOS that's really what the emulator expects, and prefers one from the game's region. Without one, the
PlayStation emulator plays with its own stand-in, which runs most games.

## Covers

Covers come from a plugin for titles and covers. The **Hasheous** one (built in, off until you add it, under
**Settings → Plugins → Titles and covers**) looks up games by their checksums at [hasheous.org](https://hasheous.org),
for a cover and the name people know the game by. It needs no account, and only sends checksums, one a second.

The games you have are looked up as soon as it's added, and as they arrive. Any other game, such as one in Search,
is looked up the first time it's shown; its cover shimmers until then. Covers are kept in Romdex's data folder, and
each game is looked up once; one that isn't found is tried again after a month, and shows a placeholder meanwhile.

## Opening games in another app

A connected app, such as [RomM](https://romm.app), shows your library its own way. Add the **RomM** connector under
**Settings → Plugins**, with RomM's address and an API token made in RomM (or a username and password). RomM needs to
read the same library folder; if it sees it under another name, set **Library folder**. If people open RomM at another
address than Romdex reaches it at, set **Public address**.

Then each dump Romdex files is passed to RomM, which is asked to scan only that system, and **Open in RomM** shows on a
game's page. If RomM hasn't found a game yet, the button asks it to scan again. Romdex works the same without one.

Next: [Writing a retriever](writing-a-retriever-plugin.md).
