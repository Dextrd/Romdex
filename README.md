# Romdex

Romdex manages your game library: it matches every file by its hash against the No-Intro and Redump DATs you give it,
so it knows what each one is and whether it's a good dump. It's part of the dextrd family, alongside Melodex for music.

Romdex is early. This version verifies the library, and brings new dumps in: from a folder you drop them into, or
your dumping tools' output, each checked against the DATs and filed by igir. It plays games in the browser with
EmulatorJS, finds covers with Hasheous, and opens games in RomM. Tidying the library you have comes next.

## Running it

```bash
ROMS_PATH=/path/to/roms DATS_PATH=/path/to/dats docker compose up -d
```

Then open http://localhost:8788. See the [guide](docs/guide/README.md).

## Building it

Needs the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/Romdex.Core
```

The core serves the web app and the API on http://localhost:5072, with the API reference at `/api/docs`.

The rest are containers of their own, each with its own README and tests:

- `igir-worker` (Node): files dumps with igir, and makes and reads CHDs and RVZs;
- `plugins/retriever-inbox` and `plugins/retriever-dumps` (Python): the retrievers;
- `emulatorjs`: EmulatorJS, served by nginx, for playing in the browser.

## Licence

MIT. Romdex never bundles GPL tools such as igir or EmulatorJS: they run in their own containers. Nothing here points
at sources of commercial games; you bring your own dumps.
