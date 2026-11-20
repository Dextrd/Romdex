# Romdex

Romdex manages your game library: it matches every file by its hash against the No-Intro and Redump DATs you give it,
so it knows what each one is and whether it's a good dump. It's part of the dextrd family, alongside Melodex for music.

It brings new dumps in, from a folder you drop them into or your dumping tools' output, each checked against the
DATs and filed by igir. It plays games in the browser with EmulatorJS, finds covers with Hasheous, and opens games in
RomM. Everything it does is in a versioned API, with webhooks, for other apps.

## Running it

Romdex runs in Docker. In a folder of its own:

```bash
mkdir -p romdex/inbox && cd romdex
curl -fsSLO https://raw.githubusercontent.com/dextrd/Romdex/main/docker-compose.yml
printf 'ROMS_PATH=/path/to/roms\nDATS_PATH=/path/to/dats\n' > .env
docker compose up -d
```

Then open http://localhost:8788, and setup walks you through the rest. See the [guide](docs/guide/README.md), and
[installing](docs/guide/installing.md) for every setting. Apps that use Romdex's API: see the
[client guide](docs/client-guide.md).

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
- `emulatorjs`: EmulatorJS, served by nginx, for playing in the browser.

The retrievers live in repositories of their own, `dextrd/romdex-retriever-<name>`. The sandbox runs their published
images, or builds one from a checkout of its repository beside this one (or in `PLUGINS_DIR`).

To try the whole stack built from this checkout, over a made-up library, with docker or podman:

```bash
sandbox/sandbox.sh up
```

It's on http://localhost:8789. The user is `admin`; the password is generated into `sandbox/.env` on first run.
`sandbox/sandbox.sh arrive` drops a new dump of each system into its inbox, for Romdex to file; `build` rebuilds the
images after a change, and `down` or `reset` throws it away. `sandbox/sandbox.sh` on its own lists the rest.

## Licence

MIT. Romdex never bundles GPL tools such as igir or EmulatorJS: they run in their own containers. Nothing here points
at sources of commercial games; you bring your own dumps.
