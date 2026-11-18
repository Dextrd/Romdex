# Installing

Romdex runs in Docker. Save [docker-compose.yml](../../docker-compose.yml) on your server, with a `.env` file beside it:

```
ROMS_PATH=/srv/games/roms
DATS_PATH=/srv/games/dats
```

Then start it:

```bash
docker compose up -d
```

and open `http://your-server:8788`. Setup walks you through the rest.

## The folders

| Folder | In the container | What it is |
| --- | --- | --- |
| `ROMS_PATH` | `/roms` | Your game library. The core only reads it; the igir worker files into it. |
| `DATS_PATH` | `/dats` | No-Intro and Redump DATs, zipped or not. Romdex only reads it. |
| (a volume) | `/config` | Romdex's database, settings and logs. Back this up. |
| (a volume) | `/staging` | Where dumps wait between arriving and being filed, shared by the core, the worker and the plugins. |
| `INBOX_PATH` | `/inbox` | Drop dumps here for the inbox plugin (default `./inbox`). |

The compose file also runs the igir worker, which files dumps, the inbox plugin, and `emulatorjs`, which plays games
in the browser. Each is GPL or stands alone, so each is a container of its own; leave out any you don't want.

The library's owner, `PUID` and `PGID` in the `.env` file (default 1000), is who the igir worker and the plugins run
as, so the files they write are yours.

Create the inbox folder yourself before the first start. Otherwise Docker creates it, owned by root, and the inbox plugin
can't take dumps out of it.

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
