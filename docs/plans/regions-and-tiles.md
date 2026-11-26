# Plan: region tiers, region dots and tiles

Agreed while designing Review. Built: region tiers, region dots and requests; tiles are next.

## Region tiers

Each system's region preferences (and the default for every system) become three tiers, replacing today's ordered
region list and the "only listed regions" switch:

- **Wanted**: regions you want a copy of. A general fetch gets one dump for each wanted region the game came out in,
  each its own download, so a failure is reported per region (on Downloads and under Review → Couldn't fetch). The
  order matters only for which copy is the main one where there's one choice to make, such as Play or Open in RomM.
- **Fallback**: used only when a game has no dump in any wanted region. Tried in order; the first that gets a valid dump
  wins.
- **Never**: never fetched, even when it's the only dump there is. A game that came out only in Never regions isn't
  counted as missing.

Rules:

1. Regions a game never came out in don't count against it.
2. A dump covers every region it lists: `(USA, Europe)` is fetched once for both; `(World)` covers every region.
3. One dump a region: within a region, the latest revision, then your languages, as 1G1R picks today.
4. Regions not listed (Korea, Brazil and so on) sit at the bottom of Fallback until moved to Never.
5. Never only governs what Romdex fetches: a Never-region dump you drop in the inbox yourself is filed.
6. The fetch fallback is ranked by these tiers. Today only the preferred release comes first and the rest are by name.

Migration: the first region of today's list becomes Wanted and the rest Fallback, in order; with "only listed regions"
on, every other region goes to Never. First-run setup asks which regions you want.

## Region dots

A small dot beside each owned game, as Melodex's quality dots, using Melodex's dot styles, with hover text saying what
it means (such as "Wanted: Europe, USA; you have Europe"):

| Dot | Means |
|---|---|
| Gold (shiny, a touch bigger) | The best your tiers allow: every wanted region the game came out in, or, where it came out in none, the highest fallback that exists |
| Green | At least one wanted region, but not all |
| Amber | Only fallback regions, while a wanted one exists |
| Red | Only Never regions: for libraries from before Romdex, or after a change of mind |
| None | Not owned |

- A game's status keeps meaning whether you have a good copy at all (owned, missing, bad, and partly owned for missing
  discs); the dot says whether it's the copy you want. "Get everything missing" also fetches for green and amber games,
  to fill in wanted regions.
- On a game's page, each dump is marked wanted, fallback or never, so a red game shows which copy you have and which
  you'd want, with Get beside it.
- System pages can filter by dot ("everything amber and red").

## Tiles

- No more red and green tiles. An owned game's cover is in full colour; a missing game's is faded and desaturated.
- No banners on tiles.
- The region dot sits under the tile, beside the title, not on the cover.

## Requests: trying offers in turn, as Melodex does

Built: `RequestService`. The notes below were the plan; it's at parity now, except that Romdex has no "Get everything
missing" for a whole system yet, so filling in wanted regions is per game, with Get.

Was not yet at parity with Melodex. Today, Get (and Review's Replace, Upgrade, Get and Couldn't fetch → Try again) makes
**one attempt**: it fetches the best offer not turned away before. If that offer turns out to be another game, or fails,
it waits under Review → Couldn't fetch, and each Try again moves on to the next offer.

Melodex instead keeps going by itself: a request tries its candidates one after another, in ranked order, until one is
kept, and only lands in Couldn't fetch once every one has failed, listing why each was turned away
(`RequestPipeline.RunAsync` and `CandidateRanking` in Melodex).

To match it, Romdex needs a request: what was asked for (a title, or one dump), with its attempts, each a download.
Couldn't fetch then lists requests rather than downloads, with each attempt's reason, and Try again searches afresh.
The region tiers above need the same thing (one request per wanted region, each reported on its own), so build it there.
The ranking (`RetrievalSearch.Rank`) and the record of what was turned away (`RetrievalSearch.TurnedAwayAsync`) carry
over as they are.
