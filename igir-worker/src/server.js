// The igir worker's HTTP API, called only by the Romdex core. See README.md.

import http from "node:http";
import { fileURLToPath } from "node:url";

import { convert, read } from "./discs.js";
import { file, folderFor } from "./filing.js";
import { LIBRARY, RequestError, STAGING } from "./paths.js";
import { stage } from "./stage.js";
import { CHDMAN, DOLPHIN_TOOL, IGIR_VERSION, Queue, ToolError, versionOf } from "./tools.js";

export const VERSION = "0.1.0";

const queue = new Queue();

const routes = {
  "GET /v1/health": async () => ({ status: "healthy", message: `igir ${IGIR_VERSION}; library at ${LIBRARY}, staging at ${STAGING}` }),
  "GET /v1/version": async () => ({
    version: VERSION,
    igir: IGIR_VERSION,
    chdman: await versionOf(CHDMAN, [], /manager\s+([0-9.]+)/i),
    dolphinTool: await versionOf(DOLPHIN_TOOL, ["--version"], /([0-9][0-9a-z.+-]*)/i),
  }),
  "POST /v1/file": (body) => queue.run(() => file(body)),
  "POST /v1/folder": async (body) => folderFor(body),
  "POST /v1/convert": (body) => queue.run(() => convert(body)),
  "POST /v1/read": (body) => read(body),
  "POST /v1/stage": (body) => queue.run(() => stage(body)),
};

export function createServer() {
  return http.createServer(async (request, response) => {
    const route = routes[`${request.method} ${new URL(request.url, "http://worker").pathname}`];
    if (!route) return send(response, 404, { error: "No such endpoint." });
    try {
      const body = request.method === "POST" ? await readJson(request) : undefined;
      send(response, 200, await route(body));
    } catch (e) {
      if (e instanceof RequestError) return send(response, e.status, { error: e.message });
      // A tool that failed did so on the files it was given, not because the worker is broken.
      if (e instanceof ToolError) return send(response, 422, { error: e.message });
      console.error(e);
      send(response, 500, { error: e.message ?? String(e) });
    }
  });
}

async function readJson(request) {
  let text = "";
  for await (const chunk of request) {
    text += chunk;
    if (text.length > 16 * 1024 * 1024) throw new RequestError("The request is too big.", 413);
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new RequestError("The body isn't JSON.");
  }
}

function send(response, status, body) {
  const json = JSON.stringify(body);
  response.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(json) });
  response.end(json);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT || 8080);
  createServer().listen(port, () => console.log(`igir worker ${VERSION} (igir ${IGIR_VERSION}) on port ${port}`));
}
