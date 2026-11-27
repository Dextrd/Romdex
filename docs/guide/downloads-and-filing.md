# Downloads and filing

Downloads bring dumps into the library. They arrive through retrievers, such as the inbox folder. Romdex works out
what each one is from its checksums and files it where its system says. Anything it can't verify waits for you under
[Review](review.md).

You're responsible for what you bring in. Romdex checks that a dump is what the DATs say it is; it doesn't know where
it came from, and it comes with no retrievers of its own: you choose which to add.

## How a dump arrives

Each arrival goes through the same steps. **Activity → Fetches** shows them, as Melodex's does: each game you asked for,
with its regions and how many offers it has tried, and what arrived by itself, such as through the inbox folder. Open
a game to step through its attempts, newest first, each with its files' hashes, what each matched, and the
retriever's notes; open one that arrived by itself to see the same. While a game is being fetched, its page shows
**Fetching** in place of **Get**. Only so many fetch at once, as [Settings → Fetching](configuration.md#fetching)
says; the rest wait their turn, and can be moved, paused or cancelled. **Recent** shows what's still
going, or changed in the last hour, in Running, Queued and Completed sections you can close; **Earlier** shows the
rest. The old Downloads page's links go there.

1. **Staged.** The plugin puts its files in the staging volume. The inbox and dumping tools plugins hand over what
   arrives by themselves; Romdex asks them every 5 minutes, or when you press **Check now**. You can also find a game
   under **Search** and press **Get**. That's a request for each [wanted region](getting-started.md#regions) the game
   came out in that you don't have, or one for the best dump there is if it came out in none. Each request asks every
   plugin that searches and fetches the best offer straight away: one the plugin says is the dump you want, then one
   whose checksums its source vouches for, then the one the plugin is surest of, in your plugins' order. If what
   arrives is another game, or fails, the next offer is tried, as Melodex does; anything turned away before is passed
   over.
2. **Identified.** Every file is hashed, inside zips and 7z archives too, with and without a header, and matched
   against the catalogue. What it holds whole are its dumps.
3. **Decided.** A dump you already have isn't filed again. Another dump of a game you have, such as another region,
   is filed or thrown away, as its system says (below).
4. **Converted**, if its system asks for CHD or RVZ. The converted file is checked against the DAT before the original
   is deleted. If it doesn't match, the original is filed instead.
5. **Filed.** The igir worker moves it into the library, named as the DAT names it, and Romdex checks it's there and
   verified.

## What waits for you

An arrival waits for you, under [Review](review.md), when:

- **it matches nothing in the catalogue.** It may be a bad dump, a hack, or from a system you have no DAT for. Reject
  it, or add the DAT, update the catalogue and **Retry**;
- **it's incomplete**, such as a disc missing a track. The message says which files are missing;
- **none of its files could be read**;
- **a request ran out of offers.** Every offer was tried and turned away, each attempt saying why. **Accept** files
  another game that came instead as what it is; **Look again** searches again, passing over what was turned away.

**Reject** throws an arrival's files away. **Retry** identifies it again, or fetches it again if its files are gone.
Open an arrival to see its files' hashes, what each matched, and the plugin's notes, such as a ripper's log.

## The library's layout

Under **Settings → Library**, **Library format** says how the library folder is laid out, so the app you play with finds
everything. Whatever Downloads files goes where it says.

- **Layout:** how each system's folder is named, as an app expects:

  | Layout | Game Boy | PlayStation | GameCube |
  |---|---|---|---|
  | RomM (the default) | `gb` | `psx` | `ngc` |
  | ES-DE and EmuDeck | `gb` | `psx` | `gc` |
  | Batocera | `gb` | `psx` | `gamecube` |
  | RetroDECK | `gb` | `psx` | `gc` |
  | MiSTer | `Gameboy` | `PSX` | (none: `Nintendo - GameCube`) |
  | Named as the DATs are, as RetroArch's playlists are | `Nintendo - Game Boy` | `Sony - PlayStation` | `Nintendo - GameCube` |

  The names are [igir](https://igir.io)'s. A system the layout has no name for gets a folder named as its DAT is.
- **Games folder:** the folder systems' folders are in, inside the library folder, such as `roms`. Empty when they're
  right in it.
- **BIOS folder:** where BIOS files go, such as `bios`, in a folder per system (as RomM expects) or all together (as
  ES-DE, EmuDeck and Batocera do). Empty to keep them with each system's games.

A game of one file goes straight in its system's folder, named as the DAT names it; a game of several files gets a
folder of its own inside it. An archive, CHD or RVZ is named after the dump.

If your library is laid out already, mount its top folder as `ROMS_PATH` and choose **Match my library**: Romdex works
out the layout and folders from where your games are. With an `emulation` folder holding `roms/<system>` and
`bios/<system>`, say, that's games in `roms` and BIOS files in `bios`, by system. Systems whose folders the layout
names otherwise can keep theirs. Nothing changes until you choose **Use this layout**.

## Each system's filing

On a system's page, under **Filing**:

- **Folder:** as the library's layout is (the default), another layout, or a folder of your own naming, inside the
  games folder.
- **Stored as:**
  - **As it arrives**, which is the default;
  - **loose files** or **a zip per game**, for cartridges;
  - **CHD**, for CD and DVD systems;
  - **RVZ**, for GameCube and Wii.
- **Another dump of a game you have:** file it beside the one you have (the default), or throw it away.

These apply to what arrives from then on. Games already in the library move only when you format it.

## Formatting the library you have

**Preview formatting**, under **Settings → Library → Library format**, compares the games in the library with where the layout
puts them, and lists every one that would move, and where. On a system's page, **Check the library** does the same for
its games. Nothing moves until you choose **Move** and confirm.

Formatting moves each game to its system's folder and renames it as the DAT names it. Folders it leaves empty, or with
only what operating systems leave behind (`.DS_Store`, `Thumbs.db`, `desktop.ini`), go; any folder with something else
in it stays. It runs as a task on the Activity page, and the library follows each file as it moves, so nothing is
hashed again. A connected app is asked to scan for them in their new places.

Only games that are whole and match the DATs move. Files that match nothing stay where they are, and so does a game
whose new place is taken, or whose archive holds other games too: the preview says why. Converting what you have to
another storage format isn't part of formatting.

Under [Review](review.md), **Send to Downloads** takes a file that matches nothing, or can't be read, out of the
library and through fetching again. It shows under Activity → Fetches, and can be identified afresh, such as after you add a DAT, or rejected.

## The igir worker

The igir worker is the only part of Romdex that writes the library; the core only reads it. It runs
[igir](https://igir.io) to file each dump, with a DAT of just that dump, and chdman and DolphinTool to make and read
CHDs and RVZs. These are GPL programs, so they run in a container of their own, beside the core.

Next: [Review](review.md).
