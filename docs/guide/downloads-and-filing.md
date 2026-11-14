# Downloads and filing

Downloads bring dumps into the library. They arrive through retrievers, such as the inbox folder. Romdex works out
what each one is from its checksums and files it where its system says. Anything it can't verify waits for you on the
**Downloads** page.

You're responsible for what you bring in. Romdex checks that a dump is what the DATs say it is; it doesn't know where
it came from, and it comes with no plugin that fetches commercial games.

## How a dump arrives

Each arrival goes through the same steps, shown on the Downloads page and on the Activity page while they run:

1. **Staged.** The plugin puts its files in the staging volume. The inbox and dumping tools plugins hand over what
   arrives by themselves; Romdex asks them every 5 minutes, or when you press **Check now**. You can also find a game
   under **Search** and press **Get**. Romdex asks the plugins that search, and you choose what to get.
2. **Identified.** Every file is hashed, inside zips and 7z archives too, with and without a header, and matched
   against the catalogue. What it holds whole are its dumps.
3. **Decided.** A dump you already have isn't filed again. Another dump of a game you have, such as another region,
   is filed, held or thrown away, as its system says (below).
4. **Converted**, if its system asks for CHD or RVZ. The converted file is checked against the DAT before the original
   is deleted. If it doesn't match, the original is filed instead.
5. **Filed.** The igir worker moves it into the library, named as the DAT names it, and Romdex checks it's there and
   verified.

## What waits on the Downloads page

An arrival waits for you when:

- **it matches nothing in the catalogue.** It may be a bad dump, a hack, or from a system you have no DAT for. Reject
  it, or add the DAT, update the catalogue and **Retry**;
- **it's incomplete**, such as a disc missing a track. The message says which files are missing;
- **it isn't what you asked for.** You got it for one game, and it's another;
- **it's an alternative** and its system holds alternatives for review. **Accept** files it.

**Reject** throws an arrival's files away. **Retry** identifies it again, or fetches it again if its files are gone.
Open an arrival to see its files' hashes, what each matched, and the plugin's notes, such as a ripper's log.

## Each system's filing

On a system's page, under **Filing**:

- **Folder:** a folder per platform, named as RomM expects (`gb`, `psx`), which is the default; or a folder named as
  the DAT is, such as `Nintendo - Game Boy`. A game of several files gets a folder of its own inside it.
- **Stored as:**
  - **As it arrives**, which is the default;
  - **loose files** or **a zip per game**, for cartridges;
  - **CHD**, for CD and DVD systems;
  - **RVZ**, for GameCube and Wii.
- **Another dump of a game you have:** file it beside the one you have (the default), hold it for review, or throw it
  away.

These apply to what arrives from then on. Files already in the library stay as they are.

## The library you already have

**Check the library**, under a system's **Filing**, compares the games already in the library with where Downloads
would file them, and lists the ones that would go elsewhere. It moves nothing. Moving them comes in a later version.

Under **Review**, **Send to Downloads** takes a file that matches nothing, or can't be read, out of the library and
through Downloads again. There it can be identified afresh, such as after you add a DAT, or rejected.

## The igir worker

The igir worker is the only part of Romdex that writes the library; the core only reads it. It runs
[igir](https://igir.io) to file each dump, with a DAT of just that dump, and chdman and DolphinTool to make and read
CHDs and RVZs. These are GPL programs, so they run in a container of their own, beside the core.

Next: [Writing a retriever](writing-a-retriever-plugin.md).
