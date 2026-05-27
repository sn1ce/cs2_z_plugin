"""Workshop map history — persists IDs the user has changed to via the
`host_workshop_map <id>` RCON command, alongside the human-readable map title
fetched from Steam's public published-file metadata endpoint.

Storage layout (JSON, mounted into the container via compose):
    {
      "items": [
        {
          "id":         "3312510632",
          "name":       "Riptide Frenetic",
          "first_used": "2026-05-27T11:00:00Z",
          "last_used":  "2026-05-27T11:00:00Z"
        }
      ]
    }

Concurrency model:
    All read / mutate operations go through an asyncio.Lock so the on-disk file
    cannot be torn between writers. The Steam API fetch is fired asynchronously
    via `asyncio.create_task` from the WS handler — it never blocks the RCON
    call. If the fetch fails, we keep a placeholder name and try again on next
    /api/workshop-maps fetch (so stale entries gradually heal).
"""
from __future__ import annotations

import asyncio
import datetime as _dt
import json
import logging
import re
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

log = logging.getLogger("webcon.workshop")


def _atomic_write_json(path: str, data: Any) -> None:
    """Local proxy to main._atomic_write_json. Imported lazily to avoid a
    circular import (main imports this module at startup)."""
    from .main import _atomic_write_json as _impl
    _impl(path, data)

STEAM_ENDPOINT = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
STEAM_TIMEOUT = 6.0  # seconds; this endpoint is normally <500ms

# Substring used to mark an entry whose name we haven't successfully fetched
# yet. Detected so we can retry on the next list-read.
_PLACEHOLDER_PREFIX = "(unknown"


def _now_iso() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _placeholder_name(workshop_id: str) -> str:
    return f"{_PLACEHOLDER_PREFIX} — ID {workshop_id})"


class WorkshopHistory:
    """File-backed map history. One instance per process is enough."""

    def __init__(self, path: str) -> None:
        self.path = path
        self._lock = asyncio.Lock()
        # Track IDs we're currently fetching so concurrent calls don't dupe.
        self._inflight: set[str] = set()

    # ------------- IO helpers -------------------------------------------------

    def _read_raw(self) -> dict[str, Any]:
        p = Path(self.path)
        if not p.is_file():
            return {"items": []}
        try:
            data = json.loads(p.read_text(encoding="utf-8") or "{}")
        except Exception as exc:
            log.warning("workshop_history.json unreadable, starting empty: %s", exc)
            return {"items": []}
        if not isinstance(data, dict):
            return {"items": []}
        items = data.get("items")
        if not isinstance(items, list):
            data["items"] = []
        return data

    def _write_raw(self, data: dict[str, Any]) -> None:
        _atomic_write_json(self.path, data)

    # ------------- Public API -------------------------------------------------

    async def list_sorted(self) -> list[dict[str, Any]]:
        """Return items sorted by `last_used` desc. Fires a background refetch
        for any entry still carrying a placeholder name."""
        async with self._lock:
            data = self._read_raw()
            items = list(data.get("items") or [])

        # Refetch any placeholder names in the background (best-effort).
        for item in items:
            name = item.get("name") or ""
            wid = item.get("id") or ""
            if wid and name.startswith(_PLACEHOLDER_PREFIX):
                self.schedule_name_fetch(wid)

        def _sort_key(it: dict[str, Any]) -> str:
            return it.get("last_used") or ""

        items.sort(key=_sort_key, reverse=True)
        return items

    async def record_use(self, workshop_id: str) -> None:
        """Mark `workshop_id` as just-used. Inserts a fresh row (placeholder
        name) if we've never seen this ID, otherwise bumps `last_used`. Then
        schedules a name fetch if we still don't know the title."""
        wid = workshop_id.strip()
        if not wid.isdigit():
            return
        async with self._lock:
            data = self._read_raw()
            items: list[dict[str, Any]] = data.get("items") or []
            now = _now_iso()
            found = None
            for it in items:
                if it.get("id") == wid:
                    found = it
                    break
            if found is None:
                items.append({
                    "id": wid,
                    "name": _placeholder_name(wid),
                    "first_used": now,
                    "last_used": now,
                })
            else:
                found["last_used"] = now
                if not found.get("first_used"):
                    found["first_used"] = now
            data["items"] = items
            try:
                self._write_raw(data)
            except Exception as exc:
                log.exception("failed to write workshop_history.json: %s", exc)
        # Resolve the name if we don't have it yet.
        if found is None or (found.get("name") or "").startswith(_PLACEHOLDER_PREFIX):
            self.schedule_name_fetch(wid)

    # ------------- Steam name resolution -------------------------------------

    def schedule_name_fetch(self, workshop_id: str) -> None:
        """Kick off a background task that fetches the workshop item's title
        and updates the on-disk record. Idempotent — duplicate scheduling for
        the same ID coalesces into one in-flight request."""
        if workshop_id in self._inflight:
            return
        self._inflight.add(workshop_id)
        asyncio.create_task(self._resolve_name(workshop_id))

    async def _resolve_name(self, workshop_id: str) -> None:
        try:
            name = await asyncio.get_running_loop().run_in_executor(
                None, _fetch_workshop_title, workshop_id,
            )
        except Exception as exc:
            log.warning("steam fetch failed for %s: %s", workshop_id, exc)
            name = None
        finally:
            self._inflight.discard(workshop_id)

        if not name:
            return  # placeholder stays; we'll retry on next /list call

        async with self._lock:
            data = self._read_raw()
            items: list[dict[str, Any]] = data.get("items") or []
            for it in items:
                if it.get("id") == workshop_id:
                    it["name"] = name
                    break
            else:
                # ID was somehow removed between record + fetch — re-insert.
                now = _now_iso()
                items.append({
                    "id": workshop_id,
                    "name": name,
                    "first_used": now,
                    "last_used": now,
                })
            data["items"] = items
            try:
                self._write_raw(data)
            except Exception as exc:
                log.exception("failed to write workshop_history.json: %s", exc)


def _fetch_workshop_title(workshop_id: str) -> str | None:
    """Blocking POST to Steam's public published-file metadata endpoint.
    Runs in a worker thread (see `_resolve_name`)."""
    body = urllib.parse.urlencode({
        "itemcount": "1",
        "publishedfileids[0]": workshop_id,
    }).encode("utf-8")
    req = urllib.request.Request(
        STEAM_ENDPOINT,
        data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=STEAM_TIMEOUT) as resp:  # noqa: S310
        raw = resp.read()
    payload = json.loads(raw.decode("utf-8"))
    details = (
        payload.get("response", {})
        .get("publishedfiledetails", [])
    )
    if not details:
        return None
    title = details[0].get("title")
    if not isinstance(title, str):
        return None
    title = title.strip()
    return title or None


# Extract workshop ID from a raw RCON command string. Accepts variants like:
#   host_workshop_map 3312510632
#   host_workshop_map  3312510632 ; mp_warmup_end
#   host_workshop_map "3312510632"
_CMD_RE = re.compile(r"^\s*host_workshop_map\s+\"?(\d+)\"?", re.IGNORECASE)


def extract_workshop_id(cmd: str) -> str | None:
    if not cmd:
        return None
    m = _CMD_RE.match(cmd)
    return m.group(1) if m else None
