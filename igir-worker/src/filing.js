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
 * Files one dump. The DAT names the system as its own DAT does, which is how igir knows the console for the RomM
 * layout; one it doesn't know goes in a folder named after the DAT instead.
 * @returns {{ files: string[], folder: string }} where each file went, relative to the library
 */
export async function file({ stagingDirectory, dat, layout, mode }) {
  const input = inside(STAGING, stagingDirectory, "stagingDirectory");
  if (typeof dat !== "string" || !dat.includes("<game")) throw new RequestError("dat must be a Logiqx DAT of the dump to file.");
  if (!(mode in MODES)) throw new RequestError('mode must be "move", "extract" or "zip".');
  const { name, roms } = readDat(dat);
  const output = inside(LIBRARY, folderFor({ datName: name, layout, files: roms }).folder, "The folder");

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
 * The library folder a layout gives a system's games: the folder igir's {romm} token gives, by the DAT's name or else
 * by the files' extensions, or one named after the DAT. Lets the core check where the library's games would go.
 */
export function folderFor({ datName, layout, files = [] } = {}) {
  if (typeof datName !== "string" || datName.length === 0) throw new RequestError("datName is needed.");
  if (layout === "datName") return { folder: safe(datName) };
  if (layout === "romm") return { folder: consoleFolder(datName, files) ?? safe(datName) };
  throw new RequestError('layout must be "romm" or "datName".');
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

/**
 * The RomM folder for a system, as igir would choose it: the last console whose DAT name pattern fits, else the last
 * whose extensions include one of the files'. Null when igir knows no RomM folder for it.
 */
export function consoleFolder(datName, roms) {
  consoles ??= require("igir/dist/src/modules/candidates/utils/consoleTokens.json").consoles.map((c) => {
    const [, pattern, flags] = /^\/(.*)\/([a-z]*)$/.exec(c.datNameRegex);
    return { regex: new RegExp(pattern, flags), extensions: c.extensions, romm: c.tokens.romm };
  });
  const byName = consoles.findLast((c) => c.regex.test(datName));
  const extensions = roms.map((r) => path.extname(r).toLowerCase());
  const found = byName ?? consoles.findLast((c) => c.extensions.some((e) => extensions.includes(e)));
  return found?.romm ?? null;
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
