// Filing: igir moves a dump from staging into the library, confirming it against a DAT of that one dump.

import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";

import { parseCsv } from "./csv.js";
import { LIBRARY, RequestError, STAGING, inside, relativeToLibrary } from "./paths.js";
import { igir } from "./tools.js";

const require = createRequire(import.meta.url);

const MODES = { move: [], extract: ["extract"], zip: ["zip"] };

/**
 * Files one dump into the library folder the core chose for it, which it works out with folderFor.
 * @returns {{ files: string[], folder: string }} where each file went, relative to the library
 */
export async function file({ stagingDirectory, dat, folder, mode }) {
  const input = inside(STAGING, stagingDirectory, "stagingDirectory");
  if (typeof dat !== "string" || !dat.includes("<game")) throw new RequestError("dat must be a Logiqx DAT of the dump to file.");
  if (!(mode in MODES)) throw new RequestError('mode must be "move", "extract" or "zip".');
  const output = inside(LIBRARY, folder, "folder");

  const scratch = await fs.mkdtemp(path.join(os.tmpdir(), "romdex-file-"));
  try {
    const datPath = path.join(scratch, "dump.dat");
    const reportPath = path.join(scratch, "report.csv");
    await fs.writeFile(datPath, dat);
    const log = await igir([
      "move", ...MODES[mode], "report",
      "--dat", datPath,
      "--input", input,
      "--output", output,
      "--report-output", reportPath,
      "--disable-cache",
    ], { cwd: scratch });
    const files = await filed(reportPath);
    if (files.length === 0) {
      throw new RequestError(`igir didn't file it: none of the staged files is the dump the DAT describes. igir said: ${lastLines(log)}`, 422);
    }
    return { files, folder: relativeToLibrary(output) };
  } finally {
    await fs.rm(scratch, { recursive: true, force: true });
  }
}

/**
 * The folder a layout names a system's games: the one igir's token for that layout gives, such as {romm} or {es}, by
 * the DAT's name or else by the files' extensions; or one named after the DAT, for "datName" and for a system the
 * layout has no folder for. Relative to wherever the core keeps games.
 */
export function folderFor({ datName, layout, files = [] } = {}) {
  if (typeof datName !== "string" || datName.length === 0) throw new RequestError("datName is needed.");
  if (layout === "datName") return { folder: safe(datName) };
  if (!layouts().includes(layout)) throw new RequestError(`layout must be "datName" or one igir knows: ${layouts().join(", ")}.`);
  return { folder: consoleFolder(datName, files, layout) ?? safe(datName) };
}

/** The library files igir's report says it wrote, relative to the library. */
async function filed(reportPath) {
  let text;
  try {
    text = await fs.readFile(reportPath, "utf8");
  } catch {
    return [];
  }
  const files = new Set();
  for (const row of parseCsv(text)) {
    if (row["Status"] !== "FOUND" || !row["ROM Files"]) continue;
    for (const written of row["ROM Files"].split("|")) {
      const absolute = path.resolve(written);
      if (absolute.startsWith(LIBRARY + path.sep)) files.add(relativeToLibrary(absolute));
    }
  }
  return [...files].sort();
}

/** The DAT's name and its one game's file names. */
export function readDat(dat) {
  const header = /<header>[\s\S]*?<name>([\s\S]*?)<\/name>/.exec(dat);
  if (!header) throw new RequestError("The DAT has no name.");
  const roms = [...dat.matchAll(/<rom\b[^>]*\bname="([^"]*)"/g)].map((m) => unescape(m[1]));
  return { name: unescape(header[1].trim()), roms };
}

let consoles;

function knownConsoles() {
  consoles ??= require("igir/dist/src/modules/candidates/utils/consoleTokens.json").consoles.map((c) => {
    const [, pattern, flags] = /^\/(.*)\/([a-z]*)$/.exec(c.datNameRegex);
    return { regex: new RegExp(pattern, flags), extensions: c.extensions, tokens: c.tokens };
  });
  return consoles;
}

/** The layouts igir has folder names for, by its tokens' names, such as romm, es, batocera and mister. */
export function layouts() {
  return [...new Set(knownConsoles().flatMap((c) => Object.keys(c.tokens)))].sort();
}

/**
 * A layout's folder for a system, as igir would choose it: the last console whose DAT name pattern fits, else the last
 * whose extensions include one of the files'. Null when igir knows no folder for it in that layout.
 */
export function consoleFolder(datName, roms, layout = "romm") {
  const all = knownConsoles();
  const byName = all.findLast((c) => c.regex.test(datName));
  const extensions = roms.map((r) => path.extname(r).toLowerCase());
  const found = byName ?? all.findLast((c) => c.extensions.some((e) => extensions.includes(e)));
  return found?.tokens[layout] ?? null;
}

function safe(name) {
  return name.replaceAll(/[\\/]/g, "_");
}

function unescape(text) {
  return text.replaceAll("&quot;", '"').replaceAll("&apos;", "'").replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&amp;", "&");
}

function lastLines(log) {
  const lines = log.trim().split("\n").map((l) => l.trim()).filter(Boolean);
  return lines.slice(-4).join(" | ") || "nothing";
}
