# Writing a retriever

A retriever is a small web service, in its own container, that brings dumps to Romdex. It can do either or both
of these:

- **search:** offer dumps of a game someone looks for under **Search**;
- **list:** hand over what arrived by itself, such as files dropped into a folder.

Either way, it writes the files into a folder Romdex gives it, and Romdex works out what they are from their
checksums. A plugin never has to identify anything, and Romdex files nothing it can't verify, whatever the plugin says.

The plugins in the repository are short examples to start from:

- `plugins/retriever-inbox` (Python) lists and searches a folder;
- `plugins/retriever-dumps` lists what dumping tools write.

You're responsible for what your plugin fetches. Romdex comes with none that fetch commercial games.

## The contract (version 1.0)

Bodies are JSON, with camelCase names. Romdex sends the instance's settings with every call, so a plugin keeps no
setup of its own; the only state it keeps is the fetches it's running.

| Call | Body | Answer |
|---|---|---|
| `GET /v1/manifest` | | the manifest |
| `POST /v1/health` | `{settings}` | `{status, message}` |
| `POST /v1/checks/{id}` | `{settings}` | `{passed, message}`, for each check the manifest offers |
| `POST /v1/search` | `{settings, target, limit}` | `{candidates}`, with the `search` capability |
| `POST /v1/list` | `{settings}` | `{candidates}`, with the `list` capability |
| `POST /v1/fetch` | `{settings, jobId, target, candidate, stagingDirectory}` | a fetch status, as soon as the fetch is accepted (202) |
| `GET /v1/fetch/{jobId}` | | a fetch status |
| `DELETE /v1/fetch/{jobId}` | | 204, with the `cancel` capability |

An error is `{"error": "…", "code": "…"}` with a 4xx or 5xx status. The message is shown to people, so say what's
wrong in words. `code` is optional; `source-unavailable` (503) says the plugin's source, such as its folder, isn't
there.

### The manifest

```json
{
  "id": "example.retrieval.folder",
  "name": "My folder",
  "version": "1.0.0",
  "type": "retrieval",
  "contractVersion": "1.0",
  "description": "One sentence, shown when it's added.",
  "homepage": "https://…",
  "capabilities": ["search", "list", "cancel"],
  "settingsSchema": { "type": "object", "properties": { … } },
  "access": { "network": ["example.org"], "files": ["staging (write)"] },
  "checks": [{ "id": "login", "title": "Test login", "description": "…" }]
}
```

`settingsSchema` is a JSON Schema object. Romdex draws a form from it under **Settings → Plugins**, with each
property's `title`, `description` and `default`. `access` says what the plugin reaches, for people deciding whether to
run it.

`health.status` is `healthy`, `degraded` or `unhealthy`.

### The target

Search and fetch get the game Romdex wants, with every dump its DAT lists, the one the user prefers first:

```json
{
  "system": "Nintendo - Game Boy",
  "title": "Pixel Pond",
  "dumps": [
    {
      "name": "Pixel Pond (USA)",
      "regions": ["USA"],
      "serial": "DMG-XXXX-USA",
      "files": [{ "name": "Pixel Pond (USA).gb", "size": 32768, "crc32": "…", "md5": "…", "sha1": "…" }]
    }
  ],
  "dumpName": null
}
```

Any of the dumps will do, unless `dumpName` names the one wanted. A listed candidate is fetched with no target.

### Candidates

```json
{
  "candidateId": "anything the plugin needs to fetch it",
  "name": "Pixel Pond (USA)",
  "source": "Where it's from, for people",
  "dumpName": "Pixel Pond (USA)",
  "url": "https://…",
  "format": "zip",
  "size": 32768,
  "verified": true,
  "score": 0.9,
  "notes": "A dumper's log, or anything else that helps whoever reviews it"
}
```

Only `candidateId`, `name` and `source` are needed.

- `candidateId` is opaque to Romdex and comes back unchanged to `/v1/fetch`, so put everything the fetch needs in it.
- `verified` says the source's own checksums match the target's.
- `score` is from 0 to 1.
- `notes` is plain text.

Romdex stages each listed candidate once, by its ID. A plugin that keeps something in place after staging it, and
lists it again, should give it a new ID only when it changes.

### Fetching

Romdex creates `stagingDirectory`, relative to the staging volume (`/staging`), and makes it writable by anyone. Write
the files there, then answer status calls until the fetch is done:

```json
{ "jobId": "…", "state": "completed", "progress": 1, "files": [{ "path": "Pixel Pond (USA).gb", "size": 32768 }] }
```

`state` is `queued`, `running`, `completed`, `failed` (with `error`) or `cancelled`. `progress`, from 0 to 1, is
shown while it runs. `files` are relative to `stagingDirectory`.

- Refuse a `stagingDirectory` that would leave the staging volume.
- Make the files readable and writable by others, since the core and the igir worker run as other users.
- If the plugin restarts, a status call for a job it doesn't know gets a 404. Romdex marks the arrival failed, and
  **Retry** fetches it again.

Archives can stay archives: Romdex reads zips and 7z archives, CHDs and RVZs. A disc should keep its cue sheet with its
tracks, in one folder.

## Adding it

Run the container on the same network as Romdex, then add it under **Settings → Plugins → Retrievers → Another plugin**,
by its address, such as `http://my-plugin:8080`. Mount the staging volume into it at `/staging`.

Next: [Configuration](configuration.md).
