# Installing

Romdex runs as a few containers: the core (the web app and API), the igir worker (the only thing that writes your
library), EmulatorJS for playing in the browser, and a container for each retriever you add. RomM isn't part of the
stack: Romdex joins the one you have, if you have one.

## With Docker Compose

Everything is in one file, [`docker-compose.yml`](https://github.com/dextrd/Romdex/blob/main/docker-compose.yml),
which pulls the released images. Save it into a folder of its own:

```bash
mkdir -p romdex && cd romdex
curl -fsSLO https://raw.githubusercontent.com/dextrd/Romdex/main/docker-compose.yml
```

beside a `.env` file with your settings:

```
ROMS_PATH=/srv/games/roms
DATS_PATH=/srv/games/dats
PUID=1000
PGID=1000
```

Then start it:

```bash
docker compose up -d
```

and open `http://your-server:8788`. Setup walks you through the rest: the admin account, your library and DATs,
and plugins.

| Setting | What | Default |
| --- | --- | --- |
| `ROMS_PATH` | Your game library. Needed. | |
| `DATS_PATH` | Your No-Intro and Redump DATs | `./dats` |
| `PUID`, `PGID` | The user and group that own the library, as `id` shows them. The igir worker and plugins run as them. | `1000` |
| `ROMDEX_PORT` | The port the web app listens on | `8788` |
| `ROMDEX_VERSION` | Which release to run, such as `1.0.0`; `latest` follows each release | `latest` |

To update, pull the new images and start again; the database is upgraded on the way:

```bash
docker compose pull && docker compose up -d
```

## The folders

| Folder | In the container | What it is |
| --- | --- | --- |
| `ROMS_PATH` | `/roms` | Your game library. The core only reads it; the igir worker files into it. |
| `DATS_PATH` | `/dats` | No-Intro and Redump DATs, zipped or not. Romdex only reads it. |
| (a volume) | `/config` | Romdex's database, settings and logs. Back this up. |
| (a volume) | `/staging` | Where dumps wait between arriving and being filed, shared by the core, the worker and the plugins. |

## Retrievers

Retrievers, which bring dumps in, aren't in the compose file: each has a repository of its own, whose README has the
lines to paste under `services:` and the folders it needs, such as the inbox folder. Add the ones you want, start
them with `docker compose up -d`, and add each under **Settings → Plugins**. See
[Plugins](plugins.md#retrievers) for the list.

Make a retriever's folder yourself before its first start, such as `mkdir inbox` for the inbox folder. Otherwise
Docker makes it, owned by root, and the retriever can't take dumps out of it.

## SELinux

On hosts with SELinux enforcing, such as Fedora, containers can't read folders in your home folder until they carry
the container label. Romdex then says it isn't allowed to read the library or DAT folder. Label each folder you
mount, as its owner:

```bash
chcon -R -t container_file_t /path/to/roms /path/to/dats /path/to/inbox
```

With podman, files also carry the categories of the container that wrote them, and dumps pass from a plugin through
staging to the igir worker. Give every container the same `security_opt: ["label=level:s0:c100,c200"]` so they can
read each other's files.

Next: [Getting started](getting-started.md).
