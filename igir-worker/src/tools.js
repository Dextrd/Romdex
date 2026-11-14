// Running the command-line tools: igir, chdman and DolphinTool. Each runs as a child process with its arguments
// passed as a list, never through a shell.

import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);

export const CHDMAN = process.env.CHDMAN || "chdman";
export const DOLPHIN_TOOL = process.env.DOLPHIN_TOOL || "dolphin-tool";

/** igir's entry point, from the pinned package. */
export const IGIR = path.join(path.dirname(require.resolve("igir/package.json")), "dist", "index.js");

export const IGIR_VERSION = require("igir/package.json").version;

/** A tool that failed or couldn't start, with the end of what it said. */
export class ToolError extends Error {}

/** Runs a tool and resolves with its output; rejects with the end of its output when it fails. */
export function run(command, args, { cwd, timeoutMs = 4 * 60 * 60 * 1000 } = {}) {
  return new Promise((resolve, reject) => {
    let output = "";
    let child;
    try {
      child = spawn(command, args, { cwd, stdio: ["ignore", "pipe", "pipe"], env: { ...process.env, NO_COLOR: "1" } });
    } catch (e) {
      reject(new ToolError(`${path.basename(command)} couldn't be started: ${e.message}`));
      return;
    }
    const timer = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    const keep = (chunk) => {
      output += chunk.toString();
      // Only the end is ever shown, and a long conversion says a lot.
      if (output.length > 200_000) output = output.slice(-100_000);
    };
    child.stdout.on("data", keep);
    child.stderr.on("data", keep);
    child.on("error", (e) => {
      clearTimeout(timer);
      reject(new ToolError(e.code === "ENOENT" ? `${path.basename(command)} isn't installed here.` : `${path.basename(command)} failed: ${e.message}`));
    });
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      if (code === 0) resolve(output);
      else reject(new ToolError(`${path.basename(command)} ${signal ? `was stopped (${signal})` : `exited with ${code}`}: ${tail(output)}`));
    });
  });
}

/** igir, run by the same Node as the worker. */
export function igir(args, options) {
  return run(process.execPath, [IGIR, ...args], options);
}

/** A tool's version, or null if it isn't installed. */
export async function versionOf(command, args, pattern) {
  try {
    const output = await run(command, args, { timeoutMs: 15_000 });
    return pattern.exec(output)?.[1] ?? "installed";
  } catch (e) {
    // chdman with no arguments prints its usage, version included, and exits non-zero.
    const found = pattern.exec(e.message ?? "");
    return found ? found[1] : null;
  }
}

/** One at a time: igir, chdman and DolphinTool each want the disk to themselves, and the library mustn't be raced. */
export class Queue {
  #last = Promise.resolve();

  run(work) {
    const next = this.#last.then(work, work);
    this.#last = next.catch(() => {});
    return next;
  }
}

function tail(output) {
  const lines = output.trim().split("\n").map((l) => l.trim()).filter(Boolean);
  const text = lines.slice(-6).join(" | ");
  return text.length > 600 ? "…" + text.slice(-600) : text || "no output";
}
