# Plugins

Everything Romdex knows about games, and everywhere games come from or go to, is a plugin. Set them up under
**Settings → Plugins**. **Test** on each says whether it's working.

## Catalogue

Index plugins say which systems and dumps exist. Romdex asks them in order; where two have a DAT for the same system,
the first wins.

- **DAT folder** (built in, set up from the start) reads the No-Intro and Redump DATs in your DAT folder.
- **Redump** (built in, off until you add it) downloads Redump's DATs for the systems you list, such as `PSX, GC`,
  and their BIOS DATs, and checks for new ones every week.
- **Hasheous** (built in, off until you add it) looks up the games you have by their checksums, for covers and the
  names people know them by. See [Covers](playing-and-apps.md#covers).

## Retrievers

Retrievers bring dumps in, as Melodex's bring music. One can search for a game you found under
**Search** and fetch it, or hand over files that arrived by themselves. Each runs in its own container and speaks a
small HTTP contract, so anyone can [write one](writing-a-retriever-plugin.md) in any language. Every file is checked by
its hash before it's filed, whatever the plugin says it is. Romdex comes with none that fetch commercial games.

- **Inbox folder** (`retriever-inbox`, in the compose file) takes what you drop into a folder, one dump per file or
  folder. It also finds games in it when you search, by checksum or by name.
- **Dumping tools** (`retriever-dumps`, the `dumps` profile) takes finished dumps from disc rippers' and cartridge
  dumpers' output folders, with their logs. A disc is taken once every track is there.

See [Downloads and filing](downloads-and-filing.md) for what happens to what they bring.

## Connected apps

Connectors tell another app, such as RomM, when the library changes. They're optional; one is used at a time.

- **RomM** (built in) asks RomM to scan what's filed, and adds **Open in RomM** to a game's page. See
  [Opening games in another app](playing-and-apps.md#opening-games-in-another-app).

Next: [Downloads and filing](downloads-and-filing.md).
