// Discs: converting a staged disc to CHD (chdman) or RVZ (DolphinTool), and reading what a CHD or RVZ holds, hashed
// as the DAT lists it, since only this container has the tools.

import { createReadStream } from "node:fs";
import fs from "node:fs/promises";
import crypto from "node:crypto";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";

import { RequestError, STAGING, inside, rootOf } from "./paths.js";
import { CHDMAN, DOLPHIN_TOOL, ToolError, run } from "./tools.js";

const CD_SHEETS = [".cue", ".gdi"];

/**
 * Converts a staged disc beside itself, keeping the source: the core checks the result against the DAT before letting
 * the source go.
 * @returns {{ file: string }} the converted file, relative to the staging directory
 */
export async function convert({ stagingDirectory, format, source }) {
  const directory = inside(STAGING, stagingDirectory, "stagingDirectory");
  const input = inside(directory, source, "source");
  const extension = path.extname(input).toLowerCase();
  await fs.access(input).catch(() => {
    throw new RequestError(`${source} isn't staged.`, 404);
  });
  if (format === "chd") {
    const output = input.slice(0, -extension.length) + ".chd";
    if (CD_SHEETS.includes(extension)) await run(CHDMAN, ["createcd", "-i", input, "-o", output, "-f"]);
    else if (extension === ".iso") await run(CHDMAN, ["createdvd", "-i", input, "-o", output, "-f"]);
    else throw new RequestError("A CHD is made from a cue sheet, a gdi or an iso.");
    return { file: path.relative(directory, output) };
  }
  if (format === "rvz") {
    if (extension !== ".iso" && extension !== ".gcm") throw new RequestError("An RVZ is made from an iso.");
    const output = input.slice(0, -extension.length) + ".rvz";
    await fs.rm(output, { force: true });
    await run(DOLPHIN_TOOL, ["convert", "-i", input, "-o", output, "-f", "rvz", "-c", "zstd", "-l", "5", "-b", "131072"]);
    return { file: path.relative(directory, output) };
  }
  throw new RequestError('format must be "chd" or "rvz".');
}

/**
 * What a CHD or RVZ holds, hashed: a CHD's tracks, as chdman splits them, without its own cue sheet; an RVZ's disc
 * image, whose size DolphinTool doesn't give.
 * @returns {{ contents: { entry: string, size: number, crc32: string, md5: string, sha1: string }[] }}
 */
export async function read({ root, path: relative }) {
  const file = inside(rootOf(root), relative, "path");
  const extension = path.extname(file).toLowerCase();
  if (extension === ".chd") return { contents: await readChd(file) };
  if (extension === ".rvz") return { contents: [await readRvz(file)] };
  throw new RequestError("Only CHDs and RVZs are read here; the core reads everything else itself.");
}

async function readChd(file) {
  const info = await run(CHDMAN, ["info", "-i", file]);
  const scratch = await fs.mkdtemp(path.join(os.tmpdir(), "romdex-chd-"));
  try {
    const name = path.basename(file, path.extname(file));
    if (/CHT2|CHTR|CHCD|CHGD/.test(info)) {
      await run(CHDMAN, ["extractcd", "-i", file, "-o", path.join(scratch, name + ".cue"), "--splitbin", "-f"]);
    } else if (info.includes("DVD")) {
      await run(CHDMAN, ["extractdvd", "-i", file, "-o", path.join(scratch, name + ".iso"), "-f"]);
    } else {
      await run(CHDMAN, ["extractraw", "-i", file, "-o", path.join(scratch, name + ".img"), "-f"]);
    }
    const contents = [];
    for (const entry of (await fs.readdir(scratch)).sort()) {
      if (CD_SHEETS.includes(path.extname(entry).toLowerCase())) continue;
      contents.push({ entry, ...(await hash(path.join(scratch, entry))) });
    }
    return contents;
  } finally {
    await fs.rm(scratch, { recursive: true, force: true });
  }
}

async function readRvz(file) {
  const digests = {};
  for (const algorithm of ["crc32", "md5", "sha1"]) {
    const output = await run(DOLPHIN_TOOL, ["verify", "-i", file, "-a", algorithm]);
    // With an algorithm named, it prints only the digest.
    const digest = output.trim().split("\n").at(-1)?.trim().toLowerCase() ?? "";
    if (!/^[0-9a-f]{1,40}$/.test(digest)) throw new ToolError(`DolphinTool didn't give its ${algorithm}: ${output.trim()}`);
    digests[algorithm] = digest;
  }
  return {
    entry: path.basename(file, path.extname(file)) + ".iso",
    size: -1,
    crc32: digests.crc32.padStart(8, "0"),
    md5: digests.md5,
    sha1: digests.sha1,
  };
}

/** A file's size, CRC32, MD5 and SHA-1, in one pass. */
export async function hash(file) {
  const md5 = crypto.createHash("md5");
  const sha1 = crypto.createHash("sha1");
  let crc = 0;
  let size = 0;
  for await (const chunk of createReadStream(file, { highWaterMark: 1 << 20 })) {
    md5.update(chunk);
    sha1.update(chunk);
    crc = zlib.crc32(chunk, crc);
    size += chunk.length;
  }
  return { size, crc32: crc.toString(16).padStart(8, "0"), md5: md5.digest("hex"), sha1: sha1.digest("hex") };
}
