// The worker's own logic, without running igir or the disc tools.

import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";

process.env.STAGING_ROOT = await fs.mkdtemp(path.join(os.tmpdir(), "worker-staging-"));
process.env.LIBRARY_ROOT = await fs.mkdtemp(path.join(os.tmpdir(), "worker-library-"));

const { parseCsv } = await import("../src/csv.js");
const { consoleFolder, folderFor, readDat } = await import("../src/filing.js");
const { hash } = await import("../src/discs.js");
const { inside, LIBRARY, STAGING } = await import("../src/paths.js");
const { stage } = await import("../src/stage.js");
const { createServer } = await import("../src/server.js");

test("igir's report is read, quoted fields and all", () => {
  const rows = parseCsv('﻿DAT Name,Game Name,Status,ROM Files\nGate - NES,"Glass Garden (Europe) (En,Fr,De)",FOUND,"/a/b.zip|/a/c ""d"".bin"\r\n,,UNUSED,/x\n');
  assert.equal(rows.length, 2);
  assert.equal(rows[0]["Game Name"], "Glass Garden (Europe) (En,Fr,De)");
  assert.equal(rows[0]["ROM Files"], '/a/b.zip|/a/c "d".bin');
  assert.equal(rows[1]["Status"], "UNUSED");
});

test("a one-entry DAT gives its system and files", () => {
  const { name, roms } = readDat(`<?xml version="1.0"?><datafile><header><name>Sony - PlayStation</name></header>
    <game name="A &amp; B (USA)"><rom name="A &amp; B (USA).cue" size="1"/><rom name="A &amp; B (USA) (Track 1).bin" size="2"/></game></datafile>`);
  assert.equal(name, "Sony - PlayStation");
  assert.deepEqual(roms, ["A & B (USA).cue", "A & B (USA) (Track 1).bin"]);
});

test("the RomM folder is igir's, by DAT name first and then by extension", () => {
  assert.equal(consoleFolder("Nintendo - Game Boy", ["a.gb"]), "gb");
  assert.equal(consoleFolder("Nintendo - Nintendo Entertainment System (Headered)", ["a.nes"]), "nes");
  assert.equal(consoleFolder("Sony - PlayStation", ["a.cue", "a (Track 1).bin"]), "psx");
  assert.equal(consoleFolder("Sony - PlayStation 2", ["a.iso"]), "ps2");
  assert.equal(consoleFolder("Nintendo - GameCube", ["a.iso"]), "ngc");
  assert.equal(consoleFolder("Some Club - Things", [".gb"].map((e) => "a" + e)), "gb");
  assert.equal(consoleFolder("Some Club - Things", ["a.xyz"]), null);
  assert.deepEqual(folderFor({ datName: "Some Club - Things", layout: "romm", files: ["a.xyz"] }), { folder: "Some Club - Things" });
  assert.deepEqual(folderFor({ datName: "Nintendo - Game Boy", layout: "datName" }), { folder: "Nintendo - Game Boy" });
  assert.throws(() => folderFor({ datName: "x", layout: "flat" }), /layout/);
});

test("paths from the core stay inside their volume", () => {
  assert.equal(inside(STAGING, "downloads/1", "x"), path.join(STAGING, "downloads", "1"));
  assert.throws(() => inside(STAGING, "../etc", "stagingDirectory"), /must be inside/);
  assert.throws(() => inside(STAGING, "/etc", "stagingDirectory"), /must be inside/);
});

test("a file is hashed as a DAT lists it", async () => {
  const file = path.join(STAGING, "hello.bin");
  await fs.writeFile(file, "hello");
  assert.deepEqual(await hash(file), {
    size: 5,
    crc32: "3610a686",
    md5: "5d41402abc4b2a76b9719d911017c592",
    sha1: "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d",
  });
});

test("the API refuses what it can't do, with a reason", async () => {
  const server = createServer().listen(0);
  try {
    const base = `http://127.0.0.1:${server.address().port}`;
    const missing = await fetch(`${base}/v1/nothing`);
    assert.equal(missing.status, 404);
    const escape = await fetch(`${base}/v1/file`, { method: "POST", body: JSON.stringify({ stagingDirectory: "../..", dat: "<game", layout: "romm", mode: "move" }) });
    assert.equal(escape.status, 400);
    assert.match((await escape.json()).error, /inside/);
    const health = await (await fetch(`${base}/v1/health`)).json();
    assert.equal(health.status, "healthy");
  } finally {
    server.close();
  }
});

test("a library file is staged, and the folders it leaves empty go", async () => {
  await fs.mkdir(path.join(LIBRARY, "gb", "odd"), { recursive: true });
  await fs.writeFile(path.join(LIBRARY, "gb", "keep.gb"), "k");
  await fs.writeFile(path.join(LIBRARY, "gb", "odd", "stray.gb"), "s");
  assert.deepEqual(await stage({ path: "gb/odd/stray.gb", stagingDirectory: "downloads/s" }), { files: ["stray.gb"] });
  assert.equal(await fs.readFile(path.join(STAGING, "downloads", "s", "stray.gb"), "utf8"), "s");
  await assert.rejects(fs.stat(path.join(LIBRARY, "gb", "odd")));
  assert.ok((await fs.stat(path.join(LIBRARY, "gb", "keep.gb"))).isFile());
  await assert.rejects(stage({ path: "gb/gone.gb", stagingDirectory: "downloads/s" }), /isn't a file/);
  await assert.rejects(stage({ path: "../x", stagingDirectory: "downloads/s" }), /must be inside/);
});
