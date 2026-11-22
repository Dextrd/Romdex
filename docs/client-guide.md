# Romdex client guide

How an app works with Romdex: finding what's in a game library and what isn't, bringing dumps in, playing a game or
opening it in another app, and hearing about it when things happen. Everything here is version 1 of the API, at
`/api/v1` on the Romdex server (port 8788 by default).

The [interactive API reference](#the-api-reference), served by Romdex itself, lists every call with its fields. This
guide covers what the reference can't: how the pieces fit together.

## Contents

- [Getting a key](#getting-a-key)
- [Scopes](#scopes)
- [IDs](#ids)
- [Systems, titles and dumps](#systems-titles-and-dumps)
- [The library](#the-library)
- [Downloads](#downloads)
- [Playing](#playing)
- [Events](#events)
- [Webhooks](#webhooks)
- [Errors](#errors)
- [Versioning](#versioning)
- [The API reference](#the-api-reference)

## Getting a key

Apps authenticate with an API key. Someone with the API keys permission makes one under **Settings → API**,
choosing what it may do, and gives it to your app. A key belongs to whoever made it and can never do more than they
may: if their permissions shrink, or they're removed, so does the key. Send it on every call in the `X-Api-Key` header:

```bash
curl -H "X-Api-Key: rdx_..." http://romdex.local:8788/api/v1/systems
```

Where a header can't be set, such as a browser's `EventSource`, pass it as the `apikey` query parameter instead.
Prefer the header: query strings end up in logs.

Keys start with `rdx_`. Romdex keeps only a hash, so a lost key can't be shown again: make a new one and revoke the
old. A revoked key is refused at once. Ask for one key per app, so each can be revoked alone.

The web app itself signs in with a username and password and a session cookie (`/auth/*`). Apps don't use those.
Everyone signs in, even on the local network.

A few calls need no key at all: `GET /health`, `GET /version`, `GET /auth/status`, the signed player links
[below](#playing), and this reference.

## Scopes

Each key has one or more scopes, the same permissions users are given:

| Scope | Allows |
| --- | --- |
| `read` | Browsing the systems, the catalogue and the library, what's arriving, tasks, and following `GET /events` |
| `download` | Fetching, accepting, rejecting and retrying downloads; setting each system's 1G1R preferences; ignoring titles and choosing a dump |
| `play` | Playing games in the browser, and opening them in the connected app |
| `review` | Looking over unknown files and bad dumps, and sending them back through Downloads |
| `files` | Changing how each system's games are filed: folder layout and storage format |
| `scan` | Scanning the library, updating the catalogue, and pausing, cancelling and moving tasks |
| `settings` | Reading and changing settings and plugins, and reading the log |
| `keys` | Making and revoking API keys, and managing webhooks |
| `users` | Adding, changing and removing users |
| `admin` | Everything its owner may do |

Each operation in the reference ends with the scope it needs; anything with no scope of its own needs `admin`. Most
apps want `read`, with `play` to play games or `download` to bring them in.

Calling something your key can't do gives `403`, with the scope it needed:

```json
{ "status": 403, "code": "scope-required", "scope": "download", "title": "Not allowed", "detail": "..." }
```

## IDs

Romdex IDs are GUIDs, and stable: a system, title or dump keeps its ID when its DAT is updated, as long as the DAT
still lists it. They aren't No-Intro's or Redump's own numbers. What identifies a dump everywhere is its files'
hashes, which each dump carries: CRC32, MD5 and SHA-1, as the DAT gives them.

The catalogue has three levels:

- **System**: one DAT's worth, such as *Nintendo - Game Boy* or *Sony - PlayStation*, named as the DAT names it. A
  DAT of only BIOS files, such as Redump's *Sony - PlayStation - BIOS Images*, belongs to its system rather than
  being one of its own.
- **Title**: a game, grouping its releases: every region, revision and disc of it.
- **Dump**: one verified copy the DAT lists, such as *Ember Isle (Japan)*, with its files. A disc's dump is its cue
  sheet and tracks; a game of several discs has a dump per disc.

## Systems, titles and dumps

| Call | What |
| --- | --- |
| `GET /systems` | Every system, with how many of its titles are owned, partial, bad and missing |
| `GET /systems/{id}/titles` | A system's titles, with each one's `status` and the dump 1G1R prefers. Filter by `status` and `q`, page with `skip` and `take` |
| `GET /titles` | Every system's titles together, filtered the same way |
| `GET /titles/{id}` | A title with every dump of it, their files and hashes, and where the library has them |
| `GET /catalogue/search?q=...` | Every game the DATs list, in the library or not, once each with its title on every system it's on (`titles`), owned when any is. Words can come in any order; regions and versions count, so `kart japan` finds Japanese releases |
| `GET /systems/{id}/bios` | A system's BIOS files, and which of them its browser emulator looks for |
| `GET /titles/{id}/cover` | Its cover, from a metadata plugin such as Hasheous, whether the library has the game or not. Looked up the first time it's asked for: `503` with `Retry-After` until then, `404` when there's none. Given as `cover` on every title |
| `GET /titles/{id}/about` | What the game is, from the metadata plugins: a summary, release dates, developers, publishers, genres, credits, review scores and links elsewhere. Looked up the first time it's asked for, which can take a few seconds |
| `POST /titles/covers/status` | For covers that answered `503`: a list of their paths in, and `ready`, `none` or `pending` for each out, so a page asks once for all of them |

A title's `status` is `owned` (the library has a whole dump of it), `partial` (some discs of a release, not all),
`bad` (files of it that don't verify, such as a disc with a track missing) or `missing`. A dump's is `good`,
`incomplete`, `bad` or `missing`. Status is worked out from hashes each time, never from names.

Each system has **1G1R preferences** ("one game, one ROM"): regions and languages in order, and which kinds of dump
to leave out, such as demos. They decide which dump of a title is preferred, and which titles count as missing. They
live at `GET /systems/{id}/preferences`, or `GET /preferences` for everyone's default. `PUT /titles/{id}/preferred`
picks a dump by hand; `POST /titles/{id}/ignore` stops a title being listed as missing.

## The library

| Call | What |
| --- | --- |
| `GET /library` | How many files the library holds, how many match nothing, and when it was last scanned |
| `GET /library/missing` | Titles not owned, across every system, after each system's preferences |
| `GET /library/unknown` | Files that match nothing in the catalogue, and files that couldn't be read |
| `GET /systems/{id}/report.csv` | Every dump in a system's DAT and whether the library has it, in the shape of igir's report |
| `POST /library/scan` | Hash what's new or changed (`scan`). Asking while one is queued returns that one |

Missing means games the DATs list that the library doesn't have: ones you own but haven't dumped, or haven't filed
yet. A scan hashes each file once; later scans only hash what's new or changed. Romdex only reads the library: every
change to it goes through Downloads and the igir worker.

## Downloads

A download is something that arrived through a retriever plugin: a dump dropped into the inbox folder, or taken from
a dumping tool's output. Romdex identifies it by its hashes, whatever it's called, and files it if it's wanted.
Which retrievers there are is up to whoever runs Romdex; Romdex itself comes with none.

| Call | What |
| --- | --- |
| `GET /downloads` | What's arrived, newest first, with how many are `waiting` for someone |
| `GET /downloads/{id}` | One, with its files' hashes and what they matched |
| `POST /titles/{id}/search` | Ask every retriever that searches for dumps of a title (`download`) |
| `POST /downloads/fetch` | Have a retriever fetch one of the candidates it offered (`download`) |
| `POST /downloads/{id}/accept`, `/reject`, `/retry` | Act on one that's held or failed (`download`) |

A download's `state` moves `fetching` → `queued` → `working`, then stops at one of:

| State | Meaning |
| --- | --- |
| `filed` | In the library. `dumps[].filed` says where |
| `needsReview` | Held: it matched nothing, is incomplete, or is another dump of a game the library has. `message` says which |
| `discarded` | Not wanted, such as a copy of something already in the library |
| `rejected` | Someone said no |
| `failed` | Its plugin couldn't fetch it, or it couldn't be filed; `retry` starts it again |

Rather than polling, follow [events](#events): `download.filed`, `download.needs_review` and `download.failed` say
when one stops.

## Playing

`GET /play/{dumpId}` (`play`) says how to play a dump the library has:

```json
{ "dumpId": "...", "titleId": "...", "name": "Ember Isle (Japan)", "playerUrl": "api/v1/play/.../player?exp=...&sig=...",
  "bios": "scph5501.bin", "app": "RomM" }
```

- `playerUrl` opens the game in the browser, in the first player plugin that plays its system (EmulatorJS, built
  in), relative to the server. It needs no key, and stops working after 12 hours: get a fresh one rather than keeping
  it. Open it in a window or frame of its own.
- `unplayable` comes instead when it can't play in the browser, saying why: no player plays the system, or none is set
  up or working, the library doesn't have all of it, or it needs a BIOS the library lacks.
- `bios` names the BIOS the player is given, when the system uses one.
- `app` names the connected app, such as RomM, when there is one.

`POST /play/{dumpId}/app` (`play`) gives the game's page in the connected app as `url`. When the app hasn't found it
yet, `url` is empty and `waiting` says so; the app has been asked to scan, so ask again in a minute. With no app
connected it's `409`.

The player holds the whole game in the browser's memory, so a large disc needs a device with memory to spare. A disc
stored as CHD needs least.

## Events

Romdex announces what happens in two ways: a stream your app reads while it's running, and webhooks for when it
isn't.

### The event stream

`GET /events` is a [server-sent events](https://developer.mozilla.org/docs/Web/API/Server-sent_events) stream
(`read` scope). It sends two sorts of event:

- **Changes**, named `tasks` or `downloads`: something of that kind changed, so reload what you show of it.
- **Happenings**, such as `download.filed`, with an `id` and data: the same events webhooks get, described below.

```text
event: downloads
data: {"type":"downloads","at":"2026-11-18T21:04:11Z"}

id: 01a1f094-6d7e-7c35-9a1e-1f5c2b0a7d44
event: download.filed
data: {"id":"01a1f094-...","type":"download.filed","occurredAt":"2026-11-18T21:04:11Z","data":{...}}
```

A comment line (`: keep-alive`) comes every 15 seconds when nothing else does. The stream doesn't replay
what was missed while disconnected, so reload what you show when you reconnect.

### Event types

Every event has the same envelope:

```json
{ "id": "01a1f094-...", "type": "download.filed", "occurredAt": "2026-11-18T21:04:11Z", "data": { } }
```

| Type | When | `data` |
| --- | --- | --- |
| `download.filed` | A download is in the library | `{ download }` |
| `download.needs_review` | A download is held for someone to look at | `{ download }` |
| `download.failed` | A download failed | `{ download }` |
| `task.completed` | A background task, such as a library scan or catalogue update, finished | `{ task }` |
| `task.failed` | A background task failed | `{ task }` |
| `ping` | Only from **Test** on a webhook | `{ message }` |

`download` is the same object `GET /downloads/{id}` returns, and `task` the same as `GET /tasks/{id}`; a task's `kind`
says what it was, such as `library-scan`. A download held, retried and held again is announced each time. More types
may be added; ignore ones you don't know.

## Webhooks

A webhook is an address Romdex posts events to, set up under **Settings → Webhooks** (or `POST /webhooks`, with
`keys`). Each chooses its events, or takes all of them.

Each delivery is a `POST` of one event's JSON, with these headers:

| Header | Value |
| --- | --- |
| `Romdex-Event` | The event type |
| `Romdex-Delivery` | The delivery's ID |
| `Romdex-Signature` | `t=<unix seconds>,v1=<hex HMAC-SHA256>` |

Answer with any `2xx` within 15 seconds. Anything else, or no answer, is tried again after 30 seconds, 2 minutes,
10 minutes, an hour and 6 hours, then given up. A retry sends the same body, so the event `id` is the same: use it to
ignore repeats. Deliveries can arrive out of order.

### Checking the signature

The signature is HMAC-SHA256, keyed with the webhook's secret (`whsec_...`), of the timestamp, a dot, and the body
exactly as received. Check it before trusting the body, and refuse old timestamps so a captured delivery can't be
replayed:

```python
import hashlib, hmac, time

def verify(secret: str, header: str, body: bytes, tolerance: int = 300) -> bool:
    parts = dict(p.split("=", 1) for p in header.split(","))
    timestamp = int(parts["t"])
    if abs(time.time() - timestamp) > tolerance:
        return False
    expected = hmac.new(secret.encode(), f"{timestamp}.".encode() + body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, parts["v1"])
```

```javascript
import { createHmac, timingSafeEqual } from "node:crypto";

function verify(secret, header, rawBody, tolerance = 300) {
  const parts = Object.fromEntries(header.split(",").map(p => p.split("=", 2)));
  const timestamp = Number(parts.t);
  if (Math.abs(Date.now() / 1000 - timestamp) > tolerance) return false;
  const expected = createHmac("sha256", secret).update(`${timestamp}.${rawBody}`).digest("hex");
  return expected.length === parts.v1.length && timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1));
}
```

```csharp
static bool Verify(string secret, string header, string body, int tolerance = 300)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var timestamp = long.Parse(parts["t"]);
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > tolerance) return false;
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts["v1"]));
}
```

Use the raw body, before any JSON parsing: re-serialising changes the bytes. A new secret (**Make a new secret**, or
`POST /webhooks/{id}/secret`) applies to every delivery from then on, retries included.

## Errors

Errors are [problem details](https://www.rfc-editor.org/rfc/rfc9457) JSON, with a `detail` written to show a person:

```json
{ "status": 409, "title": "Conflict", "detail": "..." }
```

Invalid bodies are `400` with `errors`, field by field:

```json
{ "status": 400, "title": "One or more validation errors occurred.", "errors": { "regions": ["..."] } }
```

| Status | Meaning |
| --- | --- |
| `400` | Something in the body is wrong; see `errors` |
| `401` | No key, or a revoked one. `code` is `invalid-key` for a key Romdex doesn't know, `sign-in-required` for no key, or `setup-required` before Romdex has been set up |
| `403` | The key lacks a scope; see [Scopes](#scopes). A player link whose signature is wrong or out of date is also `403` |
| `404` | No such thing |
| `409` | Not now, or not this: for example, accepting a download that isn't held, or opening a game with no app connected |
| `503` | A catalogue source is busy. Wait for `Retry-After` seconds, then try again |

## Versioning

Everything under `/api/v1` keeps working the same way for the life of v1. Within it, Romdex may add calls, fields,
event types, enum values (such as a new download state) and query parameters, so ignore what you don't know and don't
reject unknown values. Anything that would break a client goes into `/api/v2`, with v1 kept alongside for a while.
`GET /version` gives the server's version, its `apiVersion`, and the plugin contract versions it supports.

## The API reference

Romdex serves an interactive reference at `/api/docs`, where you can try calls with a key, and the OpenAPI document
it's built from at `/openapi/v1.json`, to generate a client from. Both are open to anyone, as there's nothing private
in them. For .NET, `src/Romdex.Client` in the repository is the client the web app itself uses.
