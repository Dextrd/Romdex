// The two volumes the worker works in, and the checks that keep every path the core sends inside them.

import path from "node:path";

export const STAGING = path.resolve(process.env.STAGING_ROOT || "/staging");
export const LIBRARY = path.resolve(process.env.LIBRARY_ROOT || "/roms");

/** An error the core should see as its own mistake (4xx), with a message for people. */
export class RequestError extends Error {
  constructor(message, status = 400) {
    super(message);
    this.status = status;
  }
}

/** A path under a root, from one relative to it; refuses anything that would leave the root. */
export function inside(root, relative, what) {
  if (typeof relative !== "string" || relative.length === 0) {
    throw new RequestError(`${what} is needed.`);
  }
  const resolved = path.resolve(root, relative);
  if (resolved !== root && !resolved.startsWith(root + path.sep)) {
    throw new RequestError(`${what} must be inside ${root}.`);
  }
  return resolved;
}

/** The root a read is for: staging or the library. */
export function rootOf(name) {
  if (name === "staging") return STAGING;
  if (name === "library") return LIBRARY;
  throw new RequestError('root must be "staging" or "library".');
}

/** A path relative to the library, with forward slashes, as the core keeps them. */
export function relativeToLibrary(absolute) {
  return path.relative(LIBRARY, absolute).split(path.sep).join("/");
}
