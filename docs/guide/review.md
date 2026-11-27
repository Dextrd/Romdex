# Review

Romdex knows what a file is by its hash, so nothing under **Review** is a guess at a match. It's the exceptions: files
the DATs don't explain, dumps the library has only part of, and fetches that didn't bring what they were for. Each tab
is one kind of exception, with what can be done about it, and its count shows beside its name. The count in the top
bar's menu, and on the Library page, is all of them together.

Arrivals that wait for you are under the tab their reason belongs to: one that matches nothing under **Matching**, an
incomplete one under **Incomplete**, one none of whose files could be read under **Corrupted**, and one that isn't the
game asked for under **Couldn't fetch**. **Retry** identifies one again, such as after a catalogue update; **Reject**
throws its files away; **Details** opens it under Activity → Fetches, with its files' hashes and what each matched.

Each tab has a filter, and buttons for everything it shows, such as **Defer all**, or **Defer these** once you've
filtered. Deleting a file takes it out of the library through the igir worker and throws it away; Activity → Fetches keeps a
note of it. **Send to Downloads** takes a file out of the library to be identified again, such as after you add a DAT.

## Matching

Files that match nothing in the catalogue, and aren't named as anything in it is: homebrew, hacks, translations, or
dumps from a system you have no DAT for.

- **Search** for what it is. Pick the dump it is, and it counts as that dump while no DAT matches it; its game's page
  says it was linked by you. Or keep it as **a version no DAT lists**, such as a translation, with a name you give it:
  it shows on its game's page, but never makes the game owned.
- **Defer** takes it off the list. Each scan still matches it, so a DAT that adds it later finds it.
- **Hide** takes it out of Romdex altogether: it's never matched or listed again, though it stays in your library folder.

Deferred and hidden files are listed below the tab, each with **Put back**.

## Bad dumps

Files named as a dump's file is, whose hashes differ: most likely a bad or modified copy of that dump, or a hack saved
under its name.

- **Approve** counts it as the dump all the same.
- **Replace** fetches a good copy. Once it's filed and verifies, this one is deleted.

## Outdated

Files that matched a dump until a new version of the DAT changed it, such as after a redump. Romdex remembers what
each file matched, and in which version of the DAT, so these aren't mixed up with files that never matched.

- **Upgrade** fetches the dump as the DAT has it now. Once it's filed and verifies, the old file is deleted.
- **Keep** counts the file as that dump all the same.

A dump that has left the DAT altogether can only be searched for, as under Matching.

## Incomplete

Dumps the library has some files of but not all, such as a disc missing a track. Each says which files are missing.

- **Get** fetches the whole dump. Once it's filed and verifies, the part you had is deleted, unless the new one was
  filed over it.
- **Keep as it is** if the missing files are meant to be, such as audio tracks left out to save room. It counts as
  whole while only those are missing; if others go too, it's back here.
- **Delete** takes out what the library has of it, if those files hold nothing else.

A game missing a disc isn't here: each disc is a dump of its own, so the game shows as partly owned instead.

## Corrupted

Files that couldn't be read: damaged, or not what their extension says. Where the file's name is a dump's, **Replace**
fetches it, and the damaged file is deleted once it's filed and verifies. Each scan tries every one again.

What's fetched to replace a file never deletes one that holds another game too, such as an archive of several.

A file that only needs a tool to be read, such as `chdman` for a CHD or `dolphin-tool` for an RVZ, isn't listed: it's
probably fine. A note above the list says how many wait for a tool, and they're read once it's there.

## Couldn't fetch

Requests that ran out of offers. Getting a game tries each offer in turn until one is filed, as Melodex does; only
when every one has been turned away does it wait here, with each attempt and why it was turned away.

- **Accept** files another game that came instead, as what it is, when it's one the DATs know.
- **Look again** searches every retriever again, passing over what was turned away.
- **Dismiss** takes it off this list, throwing away what it kept.

Arrivals no one asked for that failed are here too, with **Try again**, **Accept** and **Dismiss** of their own.

Next: [Playing, covers and apps](playing-and-apps.md).
