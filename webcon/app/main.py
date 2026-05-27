"""FastAPI app exposing a token-gated web console for one or more CS2 RCON servers.

Endpoints:
  GET  /                  → dashboard (server list + broadcast input)
  GET  /server/{id}       → per-server console UI
  GET  /api/servers       → JSON list of configured servers (label + id only)
  GET  /api/status        → JSON health probe of each server (quick RCON check)
  WS   /ws?token=&server= → per-server bidirectional stream
                              - inbound : {"cmd": "..."} → executed via RCON
                              - outbound: {"type": "rcon"|"log"|"info"|"error"|"prompt", "text": "..."}
  WS   /ws/broadcast?token=...
                          → fan-out command to every server; outbound frames are
                            tagged with `server_id` and lines are prefixed with
                            `[server-id]` so the UI can render them interleaved.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import tempfile
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

# CSI escape sequences ("\x1b[...m") emitted by the CSSharp logger — strip them so
# the web UI shows clean text instead of "[39;49m" artifacts.
_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")

import docker
from fastapi import Body, FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from .rcon import AsyncRcon, RconError
from .workshop_history import WorkshopHistory, extract_workshop_id

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("webcon")

TOKEN = os.environ.get("WEBCON_TOKEN", "")
SERVERS_CONFIG = os.environ.get("SERVERS_CONFIG", "/config/servers.json")
ENV_FILE = os.environ.get("ENV_FILE", "/config/.env")
WORKSHOP_HISTORY_FILE = os.environ.get("WORKSHOP_HISTORY_FILE", "/config/workshop_history.json")

STATIC_DIR = Path(__file__).parent / "static"

# Server `id` must be URL-safe and also safe to embed in a shell-style env key
# suffix. We deliberately require lowercase to keep the env-key derivation
# unambiguous.
_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_-]*$")


# ---------------------------------------------------------------------------
# server registry — loaded once at startup from servers.json
# ---------------------------------------------------------------------------

class ServerCfg:
    __slots__ = ("id", "label", "rcon_host", "rcon_port", "log_container",
                 "password_env", "password")

    def __init__(self, raw: dict[str, Any]) -> None:
        self.id: str = raw["id"]
        self.label: str = raw.get("label", self.id)
        self.rcon_host: str = raw["rcon_host"]
        self.rcon_port: int = int(raw.get("rcon_port", 27015))
        self.log_container: str = raw.get("log_container", self.id)
        self.password_env: str = raw.get("password_env", "RCON_PASSWORD")
        self.password: str = os.environ.get(self.password_env, "")

    def public_dict(self) -> dict[str, Any]:
        # never expose the password
        return {
            "id": self.id,
            "label": self.label,
            "rcon_host": self.rcon_host,
            "rcon_port": self.rcon_port,
            "log_container": self.log_container,
        }

    def persist_dict(self) -> dict[str, Any]:
        # full record written back to servers.json (no password value, just key)
        return {
            "id": self.id,
            "label": self.label,
            "rcon_host": self.rcon_host,
            "rcon_port": self.rcon_port,
            "log_container": self.log_container,
            "password_env": self.password_env,
        }


def load_servers(path: str) -> list[ServerCfg]:
    p = Path(path)
    if not p.is_file():
        # Backwards-compat single-server fallback from env so the app still boots
        # cleanly if someone forgets to mount servers.json.
        host = os.environ.get("RCON_HOST", "cs2-server")
        port = int(os.environ.get("RCON_PORT", "27015"))
        lc = os.environ.get("LOG_CONTAINER", host)
        log.warning("servers config %s missing — falling back to single-server env: %s", path, host)
        return [ServerCfg({
            "id": host, "label": host, "rcon_host": host, "rcon_port": port,
            "log_container": lc, "password_env": "RCON_PASSWORD",
        })]
    raw = json.loads(p.read_text())
    if not isinstance(raw, list) or not raw:
        raise RuntimeError(f"{path} must be a non-empty JSON array")
    out: list[ServerCfg] = []
    seen: set[str] = set()
    for entry in raw:
        cfg = ServerCfg(entry)
        if cfg.id in seen:
            raise RuntimeError(f"duplicate server id in {path}: {cfg.id}")
        seen.add(cfg.id)
        out.append(cfg)
    return out


# ---------------------------------------------------------------------------
# log broadcaster — one per container, fans out tailed lines to subscribers
# ---------------------------------------------------------------------------

class LogBroadcaster:
    """Tails a container's stdout and fans it out to all connected WS clients."""

    def __init__(self, container_name: str):
        self.container_name = container_name
        self.subscribers: set[asyncio.Queue[str]] = set()
        self._task: asyncio.Task | None = None
        self._stop = asyncio.Event()

    def start(self) -> None:
        if self._task is None:
            self._task = asyncio.create_task(self._run())

    async def stop(self) -> None:
        self._stop.set()
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except (asyncio.CancelledError, Exception):
                pass

    def subscribe(self) -> asyncio.Queue[str]:
        q: asyncio.Queue[str] = asyncio.Queue(maxsize=1000)
        self.subscribers.add(q)
        return q

    def unsubscribe(self, q: asyncio.Queue[str]) -> None:
        self.subscribers.discard(q)

    async def _run(self) -> None:
        """One thread reads docker logs; we hand lines off via the event loop."""
        loop = asyncio.get_running_loop()
        while not self._stop.is_set():
            try:
                client = docker.from_env()
                container = client.containers.get(self.container_name)

                def reader():
                    """Blocking iterator over container log bytes. docker-py yields raw chunks
                    — often byte-by-byte for unbuffered loggers — so we accumulate and split on
                    newlines ourselves to emit clean per-line events."""
                    buf = bytearray()
                    for raw in container.logs(stream=True, follow=True, tail=0):
                        if not raw:
                            continue
                        buf.extend(raw)
                        while True:
                            nl = buf.find(b"\n")
                            if nl < 0:
                                break
                            line_bytes = bytes(buf[:nl])
                            del buf[:nl + 1]
                            text = line_bytes.decode("utf-8", errors="replace").rstrip("\r")
                            text = _ANSI_RE.sub("", text)
                            if text.strip():
                                loop.call_soon_threadsafe(self._fanout, text)

                await loop.run_in_executor(None, reader)
            except Exception as exc:
                log.warning("log tail %s dropped, retrying in 3s: %s", self.container_name, exc)
                await asyncio.sleep(3)

    def _fanout(self, line: str) -> None:
        for q in list(self.subscribers):
            try:
                q.put_nowait(line)
            except asyncio.QueueFull:
                # Slow consumer — drop oldest by draining one, then enqueue.
                try:
                    q.get_nowait()
                    q.put_nowait(line)
                except Exception:
                    pass


# Globals populated at startup
SERVERS: dict[str, ServerCfg] = {}
BROADCASTERS: dict[str, LogBroadcaster] = {}
# Active /ws sessions, keyed by server id → set of WebSocket connections.
# Used so DELETE /api/servers/{id} can close any open consoles with 4404.
ACTIVE_WS: dict[str, set[WebSocket]] = {}
# Serializes all registry mutations (add/edit/delete) so two concurrent calls
# can't race on servers.json or the .env append.
REGISTRY_LOCK = asyncio.Lock()

# Workshop map history (ID → name, last_used, …). One per process.
WORKSHOP = WorkshopHistory(WORKSHOP_HISTORY_FILE)


# ---------------------------------------------------------------------------
# servers.json + .env persistence helpers
# ---------------------------------------------------------------------------

def _safe_write_text(path: str, body: str) -> None:
    """Write text to `path` as safely as we can. Strategy:
      - If we can rename over the destination (same fs, not a bind-mounted single
        file), use the tempfile + atomic-rename pattern.
      - Otherwise (typical for `/config/<file>` bind mounts inside the container,
        where rename across the mount returns EBUSY), open the file in-place
        with O_TRUNC, write the new body in a single call, fsync, and return.
    For the bind-mount case the write isn't strictly atomic, but the files
    involved are small (a few hundred bytes), so the crash window is narrow."""
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    data = body.encode("utf-8")

    # Try the atomic rename path first.
    fd, tmp = tempfile.mkstemp(prefix=".webcon.", dir=str(p.parent))
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        try:
            os.replace(tmp, p)
            return
        except OSError as exc:
            # EBUSY happens when `p` is a bind-mounted single file inside a
            # container — rename can't replace it. Fall through to in-place.
            if exc.errno not in (16,):  # EBUSY
                raise
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass

    # In-place rewrite. Truncate-then-write keeps the inode the bind mount
    # points at, which is exactly what we need for bind-mounted files.
    with open(p, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())


def _atomic_write_json(path: str, data: Any) -> None:
    """Serialize JSON and persist via `_safe_write_text`."""
    _safe_write_text(path, json.dumps(data, indent=2) + "\n")


def _persist_servers() -> None:
    payload = [cfg.persist_dict() for cfg in SERVERS.values()]
    _atomic_write_json(SERVERS_CONFIG, payload)


def _env_key_for(server_id: str) -> str:
    """Derive a unique env-var key for a given server id.
    `cs2-server` → `RCON_PASSWORD_CS2_SERVER`."""
    suffix = server_id.upper().replace("-", "_")
    return f"RCON_PASSWORD_{suffix}"


def _read_env_file(path: str) -> list[str]:
    """Read the raw lines of the .env file (preserving comments / blank lines).
    Returns [] if the file doesn't exist yet."""
    p = Path(path)
    if not p.is_file():
        return []
    return p.read_text(encoding="utf-8").splitlines()


def _upsert_env_value(path: str, key: str, value: str) -> None:
    """Set KEY=VALUE inside the .env file, preserving any other keys and any
    surrounding comments. If KEY already exists, its line is replaced in place;
    otherwise the line is appended at the end. Atomic via temp file + rename.
    NOTE: this also calls os.environ[key] = value so the new password is
    immediately usable in-process without a restart."""
    os.environ[key] = value

    p = Path(path)
    lines = _read_env_file(path)
    key_re = re.compile(rf"^\s*{re.escape(key)}\s*=")
    new_line = f"{key}={value}"
    replaced = False
    out: list[str] = []
    for ln in lines:
        if not replaced and key_re.match(ln):
            out.append(new_line)
            replaced = True
        else:
            out.append(ln)
    if not replaced:
        # Make sure we don't double a blank line at the end.
        if out and out[-1] != "":
            out.append(new_line)
        else:
            out.append(new_line)
    body = "\n".join(out)
    if not body.endswith("\n"):
        body += "\n"
    _safe_write_text(path, body)


@asynccontextmanager
async def lifespan(app: FastAPI):
    if not TOKEN:
        log.error("WEBCON_TOKEN is empty — refusing to start. Set it in .env.")
        raise RuntimeError("WEBCON_TOKEN is required")
    for cfg in load_servers(SERVERS_CONFIG):
        SERVERS[cfg.id] = cfg
        b = LogBroadcaster(cfg.log_container)
        b.start()
        BROADCASTERS[cfg.id] = b
        log.info("registered server %s (%s:%d, log=%s)",
                 cfg.id, cfg.rcon_host, cfg.rcon_port, cfg.log_container)
    yield
    await asyncio.gather(*(b.stop() for b in BROADCASTERS.values()), return_exceptions=True)


app = FastAPI(lifespan=lifespan)
app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")


# ---------------------------------------------------------------------------
# HTTP routes — the UI is one HTML file that JS routes based on location.pathname.
# Both `/` and `/server/{id}` serve the same index.html; the JS reads the URL.
# ---------------------------------------------------------------------------

@app.get("/")
async def index() -> FileResponse:
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/server/{server_id}")
async def server_page(server_id: str) -> FileResponse:
    if server_id not in SERVERS:
        raise HTTPException(status_code=404, detail="unknown server")
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/api/servers")
async def api_servers(token: str = "") -> JSONResponse:
    if token != TOKEN:
        raise HTTPException(status_code=401, detail="bad token")
    return JSONResponse([cfg.public_dict() for cfg in SERVERS.values()])


@app.get("/api/status")
async def api_status(token: str = "") -> JSONResponse:
    """Probe each server with a quick RCON connect + auth. Returns a list of
    {id, label, connected, detail?}. Used by the dashboard for the status pill."""
    if token != TOKEN:
        raise HTTPException(status_code=401, detail="bad token")

    async def probe(cfg: ServerCfg) -> dict[str, Any]:
        rc = AsyncRcon(cfg.rcon_host, cfg.rcon_port, cfg.password)
        try:
            await asyncio.wait_for(rc.connect(), timeout=2.5)
        except Exception as exc:
            return {"id": cfg.id, "label": cfg.label, "connected": False, "detail": str(exc)}
        finally:
            try:
                await rc.close()
            except Exception:
                pass
        return {"id": cfg.id, "label": cfg.label, "connected": True}

    results = await asyncio.gather(*(probe(cfg) for cfg in SERVERS.values()))
    return JSONResponse(results)


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    return {"status": "ok"}


# ---------------------------------------------------------------------------
# Workshop map history — see app/workshop_history.py for storage details.
# ---------------------------------------------------------------------------

@app.get("/api/workshop-maps")
async def api_workshop_maps(token: str = "") -> JSONResponse:
    """Return the persisted workshop-ID → name list, most-recently-used first.
    Used by the dashboard + per-server console to populate the recent-maps
    dropdown next to the Workshop ID input. Placeholder names trigger an async
    Steam Web API refetch as a side effect of calling this."""
    _check_token(token)
    items = await WORKSHOP.list_sorted()
    return JSONResponse({"items": items})


@app.post("/api/workshop-maps")
async def api_workshop_maps_add(
    token: str = "",
    body: dict[str, Any] = Body(...),
) -> JSONResponse:
    """Manually add a workshop ID to the history (e.g. a 'pin to favorites'
    button). The implicit add on `host_workshop_map <id>` covers the normal
    case; this is here for explicit curation."""
    _check_token(token)
    if not isinstance(body, dict):
        raise HTTPException(status_code=400, detail="body must be a JSON object")
    wid = body.get("id")
    if not isinstance(wid, str) or not wid.strip().isdigit():
        raise HTTPException(status_code=400, detail="id must be a numeric string")
    await WORKSHOP.record_use(wid.strip())
    items = await WORKSHOP.list_sorted()
    return JSONResponse({"items": items}, status_code=201)


# ---------------------------------------------------------------------------
# CRUD endpoints — add / edit / remove servers from the UI
# ---------------------------------------------------------------------------

def _validate_payload(body: dict[str, Any], *, require_password: bool) -> dict[str, Any]:
    """Validate + normalize an incoming server payload. Raises HTTPException
    with detail messages friendly enough to surface in the UI."""
    if not isinstance(body, dict):
        raise HTTPException(status_code=400, detail="body must be a JSON object")

    def s(key: str) -> str:
        v = body.get(key)
        if not isinstance(v, str):
            raise HTTPException(status_code=400, detail=f"{key} must be a string")
        return v.strip()

    sid = s("id")
    if not _ID_RE.match(sid):
        raise HTTPException(status_code=400, detail=
            "id must match ^[a-z0-9][a-z0-9_-]*$ (lowercase, digits, '-' or '_')")

    label = s("label") or sid
    rcon_host = s("rcon_host")
    if not rcon_host:
        raise HTTPException(status_code=400, detail="rcon_host is required")
    log_container = s("log_container") or rcon_host

    port_raw = body.get("rcon_port")
    try:
        rcon_port = int(port_raw)
    except (TypeError, ValueError):
        raise HTTPException(status_code=400, detail="rcon_port must be an integer")
    if not (1 <= rcon_port <= 65535):
        raise HTTPException(status_code=400, detail="rcon_port must be 1..65535")

    password = body.get("password", "")
    if not isinstance(password, str):
        raise HTTPException(status_code=400, detail="password must be a string")
    if require_password and not password:
        raise HTTPException(status_code=400, detail="password is required")

    return {
        "id": sid,
        "label": label,
        "rcon_host": rcon_host,
        "rcon_port": rcon_port,
        "log_container": log_container,
        "password": password,
    }


def _check_token(token: str) -> None:
    if token != TOKEN:
        raise HTTPException(status_code=401, detail="bad token")


def _close_active_ws(server_id: str) -> int:
    """Schedule a fire-and-forget close (4404) on every active /ws session for
    this server id. We don't await the close, because `WebSocket.close()` waits
    for the client's close ack (up to the underlying library's ~10s timeout),
    which would stall the HTTP DELETE caller. Returns the number scheduled."""
    sessions = ACTIVE_WS.pop(server_id, set())
    for ws in list(sessions):
        async def _do_close(w=ws):
            try:
                await w.close(code=4404)
            except Exception:
                pass
        asyncio.create_task(_do_close())
    return len(sessions)


@app.post("/api/servers")
async def api_servers_create(
    token: str = "",
    body: dict[str, Any] = Body(...),
) -> JSONResponse:
    _check_token(token)
    data = _validate_payload(body, require_password=True)

    async with REGISTRY_LOCK:
        if data["id"] in SERVERS:
            raise HTTPException(status_code=409, detail=f"server id {data['id']!r} already exists")

        env_key = _env_key_for(data["id"])
        try:
            _upsert_env_value(ENV_FILE, env_key, data["password"])
        except Exception as exc:
            log.exception("failed to update %s", ENV_FILE)
            raise HTTPException(status_code=500, detail=f"failed to write env file: {exc}")

        cfg = ServerCfg({
            "id": data["id"],
            "label": data["label"],
            "rcon_host": data["rcon_host"],
            "rcon_port": data["rcon_port"],
            "log_container": data["log_container"],
            "password_env": env_key,
        })
        SERVERS[cfg.id] = cfg

        try:
            _persist_servers()
        except Exception as exc:
            # Roll back the in-memory add so state stays consistent.
            SERVERS.pop(cfg.id, None)
            log.exception("failed to write %s", SERVERS_CONFIG)
            raise HTTPException(status_code=500, detail=f"failed to write servers.json: {exc}")

        b = LogBroadcaster(cfg.log_container)
        b.start()
        BROADCASTERS[cfg.id] = b
        log.info("added server %s (%s:%d, log=%s)",
                 cfg.id, cfg.rcon_host, cfg.rcon_port, cfg.log_container)

    return JSONResponse(cfg.public_dict(), status_code=201)


@app.put("/api/servers/{server_id}")
async def api_servers_update(
    server_id: str,
    token: str = "",
    body: dict[str, Any] = Body(...),
) -> JSONResponse:
    _check_token(token)
    data = _validate_payload(body, require_password=False)

    async with REGISTRY_LOCK:
        existing = SERVERS.get(server_id)
        if existing is None:
            raise HTTPException(status_code=404, detail="unknown server")

        if data["id"] != server_id:
            raise HTTPException(status_code=400, detail="id in body must match URL")

        # If password is non-empty, overwrite the .env value at the existing
        # key (keep the original env key — no rename, no rewrite needed).
        if data["password"]:
            try:
                _upsert_env_value(ENV_FILE, existing.password_env, data["password"])
            except Exception as exc:
                log.exception("failed to update %s", ENV_FILE)
                raise HTTPException(status_code=500, detail=f"failed to write env file: {exc}")
            existing.password = data["password"]

        # Replace in-memory cfg field by field so the same object identity is kept
        existing.label = data["label"]
        existing.rcon_host = data["rcon_host"]
        existing.rcon_port = data["rcon_port"]

        # If the log container changed, swap the broadcaster.
        old_b: LogBroadcaster | None = None
        if data["log_container"] != existing.log_container:
            old_b = BROADCASTERS.pop(existing.id, None)
            existing.log_container = data["log_container"]
            new_b = LogBroadcaster(existing.log_container)
            new_b.start()
            BROADCASTERS[existing.id] = new_b

        try:
            _persist_servers()
        except Exception as exc:
            log.exception("failed to write %s", SERVERS_CONFIG)
            raise HTTPException(status_code=500, detail=f"failed to write servers.json: {exc}")

        log.info("updated server %s", existing.id)

    if old_b is not None:
        asyncio.create_task(_safe_stop_broadcaster(old_b))

    return JSONResponse(existing.public_dict())


@app.delete("/api/servers/{server_id}")
async def api_servers_delete(server_id: str, token: str = "") -> JSONResponse:
    _check_token(token)

    async with REGISTRY_LOCK:
        cfg = SERVERS.pop(server_id, None)
        if cfg is None:
            raise HTTPException(status_code=404, detail="unknown server")

        b = BROADCASTERS.pop(server_id, None)

        try:
            _persist_servers()
        except Exception as exc:
            # Roll back so the file and memory don't diverge.
            SERVERS[server_id] = cfg
            if b is not None:
                BROADCASTERS[server_id] = b
            log.exception("failed to write %s", SERVERS_CONFIG)
            raise HTTPException(status_code=500, detail=f"failed to write servers.json: {exc}")

        # Close any active /ws sessions to this server (fire-and-forget so the
        # HTTP DELETE caller doesn't wait for the client close handshake).
        n = _close_active_ws(server_id)
        log.info("removed server %s (closing %d active ws)", server_id, n)

    # Stop the broadcaster outside the lock + as a background task. The
    # underlying blocking docker.logs iterator can take a moment to unblock
    # (it's pinned in a thread); we don't make the HTTP caller wait for it.
    if b is not None:
        asyncio.create_task(_safe_stop_broadcaster(b))

    return JSONResponse({"deleted": server_id})


async def _safe_stop_broadcaster(b: LogBroadcaster) -> None:
    try:
        await b.stop()
    except Exception:
        log.exception("broadcaster stop failed")


# ---------------------------------------------------------------------------
# WebSockets
# ---------------------------------------------------------------------------

async def _ws_auth_or_close(ws: WebSocket) -> bool:
    token = ws.query_params.get("token", "")
    if token != TOKEN:
        await ws.close(code=4401)
        return False
    return True


async def _open_rcon(ws: WebSocket, cfg: ServerCfg) -> AsyncRcon | None:
    rcon = AsyncRcon(cfg.rcon_host, cfg.rcon_port, cfg.password)
    try:
        await rcon.connect()
    except Exception as exc:
        await ws.send_json({"type": "error", "text": f"[{cfg.id}] RCON connect failed: {exc}"})
        return None
    await ws.send_json({"type": "info", "text": f"[{cfg.id}] RCON connected to {cfg.rcon_host}:{cfg.rcon_port}"})
    return rcon


@app.websocket("/ws")
async def ws_one_server(ws: WebSocket) -> None:
    """Single-server console socket. Mirrors original behavior."""
    if not await _ws_auth_or_close(ws):
        return
    server_id = ws.query_params.get("server", "")
    cfg = SERVERS.get(server_id)
    if cfg is None:
        await ws.accept()
        await ws.send_json({"type": "error", "text": f"unknown server id: {server_id!r}"})
        await ws.close(code=4404)
        return

    await ws.accept()

    rcon = await _open_rcon(ws, cfg)
    if rcon is None:
        await ws.close()
        return

    broadcaster = BROADCASTERS[cfg.id]
    log_q = broadcaster.subscribe()

    # Track this socket so DELETE /api/servers/{id} can close it cleanly (4404).
    ACTIVE_WS.setdefault(cfg.id, set()).add(ws)

    async def pump_logs():
        try:
            while True:
                line = await log_q.get()
                await ws.send_json({"type": "log", "text": line})
        except (WebSocketDisconnect, RuntimeError):
            pass

    log_task = asyncio.create_task(pump_logs())

    try:
        while True:
            msg = await ws.receive_text()
            try:
                payload = json.loads(msg)
                cmd = payload.get("cmd", "").strip()
            except Exception:
                await ws.send_json({"type": "error", "text": "bad payload"})
                continue
            if not cmd:
                continue
            wid = extract_workshop_id(cmd)
            if wid:
                # Record + fire async Steam name fetch — never block the RCON
                # call itself; the response below still goes out immediately.
                asyncio.create_task(WORKSHOP.record_use(wid))
            await ws.send_json({"type": "prompt", "text": f"> {cmd}"})
            try:
                response = await rcon.exec(cmd)
            except RconError as exc:
                await ws.send_json({"type": "error", "text": f"rcon: {exc}"})
                continue
            await ws.send_json({"type": "rcon", "text": response})
    except WebSocketDisconnect:
        pass
    finally:
        log_task.cancel()
        broadcaster.unsubscribe(log_q)
        sessions = ACTIVE_WS.get(cfg.id)
        if sessions is not None:
            sessions.discard(ws)
            if not sessions:
                ACTIVE_WS.pop(cfg.id, None)
        await rcon.close()


@app.websocket("/ws/broadcast")
async def ws_broadcast(ws: WebSocket) -> None:
    """Fan-out console: one socket, every command is run against every server in
    parallel. Each response frame is tagged with `server_id`, and `text` is
    prefixed with `[server-id]` per line so plain-text UIs render usefully too.
    No log tail in broadcast mode — open a per-server view for that."""
    if not await _ws_auth_or_close(ws):
        return
    if not SERVERS:
        await ws.accept()
        await ws.send_json({"type": "error", "text": "no servers configured"})
        await ws.close()
        return

    await ws.accept()

    # Open one RCON per server up-front so latency on the first command is low,
    # and a per-server connect failure is reported once rather than per-command.
    rcons: dict[str, AsyncRcon] = {}
    for cfg in SERVERS.values():
        rc = await _open_rcon(ws, cfg)
        if rc is not None:
            rcons[cfg.id] = rc

    if not rcons:
        await ws.send_json({"type": "error", "text": "no servers reachable"})
        await ws.close()
        return

    def tag(server_id: str, body: str) -> str:
        # Prefix every line so interleaved output stays attributable.
        lines = body.split("\n") if body else [""]
        return "\n".join(f"[{server_id}] {ln}" if ln else "" for ln in lines)

    async def run_one(sid: str, rc: AsyncRcon, cmd: str) -> None:
        try:
            resp = await rc.exec(cmd)
        except RconError as exc:
            await ws.send_json({
                "type": "error",
                "server_id": sid,
                "text": tag(sid, f"rcon: {exc}"),
            })
            return
        await ws.send_json({
            "type": "rcon",
            "server_id": sid,
            "text": tag(sid, resp.rstrip("\n")),
        })

    try:
        while True:
            msg = await ws.receive_text()
            try:
                payload = json.loads(msg)
                cmd = payload.get("cmd", "").strip()
            except Exception:
                await ws.send_json({"type": "error", "text": "bad payload"})
                continue
            if not cmd:
                continue
            wid = extract_workshop_id(cmd)
            if wid:
                asyncio.create_task(WORKSHOP.record_use(wid))
            await ws.send_json({"type": "prompt", "text": f"> {cmd}"})
            await asyncio.gather(*(run_one(sid, rc, cmd) for sid, rc in rcons.items()))
    except WebSocketDisconnect:
        pass
    finally:
        for rc in rcons.values():
            try:
                await rc.close()
            except Exception:
                pass
