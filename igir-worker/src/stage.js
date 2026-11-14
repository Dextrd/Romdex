// Moving a file out of the library and into staging, so the core can take it through Downloads again.

import fs from "node:fs/promises";
import path from "node:path";

import { LIBRARY, RequestError, STAGING, inside } from "./paths.js";

/** Moves a library file into a staging folder, and removes the folders it leaves empty. */
export async function stage({ path: relative, stagingDirectory } = {}) {
  const source = inside(LIBRARY, relative, "path");
  const directory = inside(STAGING, stagingDirectory, "stagingDirectory");
  if (source === LIBRARY) throw new RequestError("path must be a file in the library.");
  const stat = await fs.stat(source).catch(() => null);
  if (!stat?.isFile()) throw new RequestError(`${relative} isn't a file in the library.`, 404);

  await fs.mkdir(directory, { recursive: true });
  const target = path.join(directory, path.basename(source));
  try {
    await fs.rename(source, target);
  } catch (e) {
    if (e.code !== "EXDEV") throw e;
    // Staging is on another volume: copy, then delete.
    await fs.copyFile(source, target);
    await fs.rm(source);
  }
  // The core runs as another user, and needs to read and replace it.
  await fs.chmod(target, 0o666);

  for (let folder = path.dirname(source); folder !== LIBRARY; folder = path.dirname(folder)) {
    if ((await fs.readdir(folder)).length > 0) break;
    await fs.rmdir(folder);
  }
  return { files: [path.basename(target)] };
}
