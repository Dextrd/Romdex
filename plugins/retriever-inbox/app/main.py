"""The inbox retriever's HTTP API (Romdex retrieval contract v1.0)."""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from .contract import CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, ListRequest, SearchRequest, dump, env, error, staging_directory
from .inbox import Inbox

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "keepOriginals": {
            "type": "boolean", "default": False, "title": "Keep originals",
            "description": "Copy what's dropped in instead of moving it out of the inbox.",
        },
        "settleSeconds": {
            "type": "integer", "minimum": 0, "maximum": 3600, "default": 30, "title": "Wait for copying to finish",
            "description": "Seconds something has to be left unchanged before it's taken, so a half-copied file isn't.",
        },
        "minScore": {
            "type": "number", "minimum": 0, "maximum": 1, "default": 0.5, "title": "Lowest name match",
            "description": "When searching, items named less like the game than this aren't offered. Checksum matches always are.",
        },
    },
}

MANIFEST: dict[str, Any] = {
    "id": "romdex.retrieval.inbox",
    "name": "Inbox folder",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Takes the dumps you drop into a folder. Romdex works out what each is by its checksums.",
    "homepage": "https://github.com/dextrd/Romdex/tree/main/plugins/retriever-inbox",
    "capabilities": ["list", "search", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": [], "files": ["inbox (read, and write when moving files out)", "staging (write)"]},
}

app = FastAPI(title="Romdex inbox retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))
inbox = Inbox(env("INBOX_ROOT", "/inbox"))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def _mounted() -> None:
    if not inbox.root.is_dir():
        raise error(503, f"The inbox folder {inbox.root} isn't mounted.", "source-unavailable")


@app.get("/v1/manifest")
def manifest() -> dict[str, Any]:
    return MANIFEST


@app.post("/v1/health")
def health(body: HealthRequest) -> dict[str, Any]:
    if not inbox.root.is_dir():
        return {"status": "unhealthy", "message": f"The inbox folder {inbox.root} isn't mounted."}
    count = len(inbox.items(float(body.settings.get("settleSeconds", 30))))
    return {"status": "healthy", "message": f"{count} item{'' if count == 1 else 's'} waiting in the inbox"}


@app.post("/v1/list")
def list_items(body: ListRequest) -> dict[str, Any]:
    _mounted()
    return {"candidates": dump([inbox.candidate(i) for i in inbox.items(float(body.settings.get("settleSeconds", 30)))])}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    _mounted()
    return {"candidates": dump(inbox.search(body.target, max(1, min(body.limit, 25)), body.settings))}


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: inbox.fetch(j, body.candidate.candidate_id, directory, body.settings))
    return job.status()


@app.get("/v1/fetch/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    return job.status()


@app.delete("/v1/fetch/{job_id}", status_code=204)
def cancel(job_id: str) -> None:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    job.cancelled.set()
