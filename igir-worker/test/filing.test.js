// Filing with the pinned igir, on made-up dumps: run before upgrading igir, as the plan asks. The disc tools' tests
// run where chdman and DolphinTool are installed, as in the worker's image, and are skipped elsewhere.

import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import { test } from "node:test";

process.env.STAGING_ROOT = await fs.mkdtemp(path.join(os.tmpdir(), "worker-staging-"));
process.env.LIBRARY_ROOT = await fs.mkdtemp(path.join(os.tmpdir(), "worker-library-"));

const { file } = await import("../src/filing.js");
const { convert, read } = await import("../src/discs.js");
const { LIBRARY, STAGING } = await import("../src/paths.js");
const { CHDMAN, versionOf } = await import("../src/tools.js");

const SECTOR = 2352;

/** Bytes standing in for a dump: the same for the same name, different for any other. */
function data(name, size) {
  const out = Buffer.alloc(size);
  let block = crypto.createHash("sha256").update(name).digest();
  for (let i = 0; i < size; i += block.length) {
    block.copy(out, i);
    block = crypto.createHash("sha256").update(block).digest();
  }
  return out;
}

function dat(system, game, files) {
  const roms = files.map(([name, bytes]) => `<rom name="${name}" size="${bytes.length}" crc="${zlib.crc32(bytes).toString(16).padStart(8, "0")}" ` +
    `md5="${crypto.createHash("md5").update(bytes).digest("hex")}" sha1="${crypto.createHash("sha1").update(bytes).digest("hex")}"/>`);
  return `<?xml version="1.0"?><datafile><header><name>${system}</name><description>${system}</description><version>test</version></header>` +
    `<game name="${game}"><description>${game}</description>${roms.join("")}</game></datafile>`;
}

async function stage(job, files) {
  const directory = path.join(STAGING, job);
  for (const [name, bytes] of files) {
    await fs.mkdir(path.dirname(path.join(directory, name)), { recursive: true });
    await fs.writeFile(path.join(directory, name), bytes);
  }
  return job;
}

function disc(name, tracks) {
  const bins = Array.from({ length: tracks }, (_, i) => [`${name} (Track ${i + 1}).bin`, data(`${name} track ${i + 1}`, SECTOR * (8 + i))]);
  const cue = bins.map(([bin], i) => `FILE "${bin}" BINARY\r\n  TRACK ${String(i + 1).padStart(2, "0")} ${i === 0 ? "MODE2/2352" : "AUDIO"}\r\n    INDEX 01 00:00:00\r\n`).join("");
  return [[`${name}.cue`, Buffer.from(cue)], ...bins];
}

test("a cartridge under any name is moved into its RomM folder, named as the DAT has it", async () => {
  const rom = data("Pixel Pond (USA)", 32768);
  const job = await stage("pond", [["whatever.gb", rom]]);
  const result = await file({ stagingDirectory: job, dat: dat("Nintendo - Game Boy", "Pixel Pond (USA)", [["Pixel Pond (USA).gb", rom]]), folder: "gb", mode: "move" });
  assert.deepEqual(result.files, ["gb/Pixel Pond (USA).gb"]);
  assert.deepEqual(await fs.readFile(path.join(LIBRARY, "gb", "Pixel Pond (USA).gb")), rom);
  await assert.rejects(fs.access(path.join(STAGING, job, "whatever.gb")));
});

test("zipped, in a folder named after the DAT", async () => {
  const rom = data("Moss Maze (Japan)", 32768);
  const job = await stage("maze", [["maze.gb", rom]]);
  const result = await file({ stagingDirectory: job, dat: dat("Nintendo - Game Boy", "Moss Maze (Japan)", [["Moss Maze (Japan).gb", rom]]), folder: "Nintendo - Game Boy", mode: "zip" });
  assert.deepEqual(result.files, ["Nintendo - Game Boy/Moss Maze (Japan).zip"]);
});

test("a headered cartridge in a zip is found when it's written out of the zip", async () => {
  const rom = data("Rocket Rabbit (Europe)", 40960);
  const header = Buffer.from([0x4e, 0x45, 0x53, 0x1a, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
  const zip = await zipOf([["rabbit.nes", Buffer.concat([header, rom])]]);
  const job = await stage("rabbit", [["rabbit.zip", zip]]);
  const result = await file({ stagingDirectory: job, dat: dat("Nintendo - Nintendo Entertainment System (Headerless)", "Rocket Rabbit (Europe)", [["Rocket Rabbit (Europe).nes", rom]]), folder: "nes", mode: "zip" });
  assert.deepEqual(result.files, ["nes/Rocket Rabbit (Europe).zip"]);
});

test("a disc of several tracks goes in a folder of its own", async () => {
  const files = disc("Signal Lost (USA)", 2);
  const job = await stage("signal", files.map(([name, bytes]) => [`some folder/${name}`, bytes]));
  const result = await file({ stagingDirectory: job, dat: dat("Sony - PlayStation", "Signal Lost (USA)", files), folder: "roms/psx", mode: "move" });
  assert.deepEqual(result.files, [
    "roms/psx/Signal Lost (USA)/Signal Lost (USA) (Track 1).bin",
    "roms/psx/Signal Lost (USA)/Signal Lost (USA) (Track 2).bin",
    "roms/psx/Signal Lost (USA)/Signal Lost (USA).cue",
  ]);
});

test("files that aren't the dump are refused, and left where they are", async () => {
  const job = await stage("junk", [["homebrew.gb", data("someone's own game", 8192)]]);
  const rom = data("Quiet Quarry (USA)", 32768);
  await assert.rejects(
    file({ stagingDirectory: job, dat: dat("Nintendo - Game Boy", "Quiet Quarry (USA)", [["Quiet Quarry (USA).gb", rom]]), folder: "gb", mode: "move" }),
    (e) => /didn't file it|igir exited/.test(e.message));
  await fs.access(path.join(STAGING, job, "homebrew.gb"));
});

const chdman = await versionOf(CHDMAN, [], /manager\s+([0-9.]+)/i);

test("a disc converts to a CHD that reads back as its tracks", { skip: chdman ? false : "chdman isn't installed here" }, async () => {
  const files = disc("Harbor Lights (Japan)", 3);
  const job = await stage("harbor", files);
  const { file: chd } = await convert({ stagingDirectory: job, format: "chd", source: "Harbor Lights (Japan).cue" });
  assert.equal(chd, "Harbor Lights (Japan).chd");
  const { contents } = await read({ root: "staging", path: `${job}/${chd}` });
  assert.deepEqual(contents.map((c) => c.sha1), files.slice(1).map(([, bytes]) => crypto.createHash("sha1").update(bytes).digest("hex")));
});

/** A zip of some files, made with Node alone: stored, no compression. */
async function zipOf(files) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, bytes] of files) {
    const nameBytes = Buffer.from(name);
    const crc = zlib.crc32(bytes);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(bytes.length, 18);
    local.writeUInt32LE(bytes.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(bytes.length, 20);
    central.writeUInt32LE(bytes.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, bytes);
    centrals.push(central, nameBytes);
    offset += local.length + nameBytes.length + bytes.length;
  }
  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}
