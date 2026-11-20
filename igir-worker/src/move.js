// Moving files around inside the library, when the core formats it to another layout.

import fs from "node:fs/promises";
import path from "node:path";

import { LIBRARY, RequestError, inside } from "./paths.js";

/** Files that don't keep a folder: what operating systems leave behind, which nobody put there. */
const CLUTTER = new Set([".ds_store", "thumbs.db", "desktop.ini", "._.ds_store"]);

/**
 * Moves library files to other places in the library, one by one; a file that's already where it's going is left
 * alone, and one whose new place is taken isn't moved. Folders the moves leave empty, or with only clutter in them,
 * are removed; a folder with anything else in it stays.
 * @returns {{ moved: {from: string, to: string}[], failed: {from: string, to: string, error: string}[] }}
 */
export async function move({ moves } = {}) {
  if (!Array.isArray(moves) || moves.length === 0) throw new RequestError("moves must be a list of {from, to}.");
  if (moves.length > 10000) throw new RequestError("Send at most 10000 moves at once.");
  const checked = moves.map((m) => {
    const source = inside(LIBRARY, m?.from, "from");
    const target = inside(LIBRARY, m?.to, "to");
    if (source === LIBRARY || target === LIBRARY) throw new RequestError("from and to must be files in the library.");
    return { from: m.from, to: m.to, source, target };
  });

  const moved = [];
  const failed = [];
  const left = new Set();
  for (const m of checked) {
    try {
      await moveOne(m.source, m.target);
      moved.push({ from: m.from, to: m.to });
      left.add(path.dirname(m.source));
    } catch (e) {
      failed.push({ from: m.from, to: m.to, error: e instanceof RequestError ? e.message : (e.message ?? String(e)) });
    }
  }
  for (const folder of left) await tidy(folder);
  return { moved, failed };
}

async function moveOne(source, target) {
  if (source === target) return;
  const stat = await fs.stat(source).catch(() => null);
  if (!stat?.isFile()) throw new RequestError(`${path.relative(LIBRARY, source)} isn't a file in the library.`);
  // A different case of the same name is the same file on some filesystems, and a rename there is fine.
  const there = await fs.stat(target).catch(() => null);
  if (there && !(there.ino === stat.ino && there.dev === stat.dev)) {
    throw new RequestError(`${path.relative(LIBRARY, target)} is already there.`);
  }
  await fs.mkdir(path.dirname(target), { recursive: true });
  await fs.rename(source, target);
}

/** Removes a folder and the folders above it, up to the library, while they hold nothing but clutter. */
export async function tidy(folder) {
  for (; folder !== LIBRARY && folder.startsWith(LIBRARY + path.sep); folder = path.dirname(folder)) {
    const entries = await fs.readdir(folder, { withFileTypes: true }).catch(() => null);
    if (entries === null) continue;
    if (entries.some((e) => !(e.isFile() && CLUTTER.has(e.name.toLowerCase())))) break;
    await fs.rm(folder, { recursive: true, force: true });
  }
}
