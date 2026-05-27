"""Source RCON async client.

Implements the Valve Source RCON protocol — auth + exec_command packets, no extras.
Reused from the /tmp/rcon.py script we've been using by hand, but asyncio so the
WebSocket loop can multiplex many commands per connection.
"""
from __future__ import annotations

import asyncio
import struct

SERVERDATA_AUTH = 3
SERVERDATA_AUTH_RESPONSE = 2
SERVERDATA_EXECCOMMAND = 2
SERVERDATA_RESPONSE_VALUE = 0


class RconError(Exception):
    pass


class AsyncRcon:
    def __init__(self, host: str, port: int, password: str):
        self.host = host
        self.port = port
        self.password = password
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._lock = asyncio.Lock()
        self._next_id = 1

    async def connect(self) -> None:
        self._reader, self._writer = await asyncio.open_connection(self.host, self.port)
        auth_id = self._send(SERVERDATA_AUTH, self.password)
        # Source returns one RESPONSE_VALUE (empty) then one AUTH_RESPONSE.
        while True:
            resp_id, resp_type, _ = await self._recv()
            if resp_type == SERVERDATA_AUTH_RESPONSE:
                if resp_id == -1:
                    raise RconError("RCON auth failed (wrong password)")
                if resp_id != auth_id:
                    raise RconError(f"RCON auth id mismatch: {resp_id} != {auth_id}")
                return

    async def close(self) -> None:
        if self._writer is not None:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:
                pass
        self._reader = self._writer = None

    async def exec(self, command: str) -> str:
        """Send one command, read all response packets, return concatenated text.

        Termination uses the classic Source "send a follow-up empty
        RESPONSE_VALUE packet" trick — the server's reply to that packet carries
        a fixed sentinel body (\\x00\\x01\\x00\\x00). We collect every packet body
        before the sentinel and concatenate.

        We deliberately do NOT filter packets by `resp_id == cmd_id`: CS2 in
        practice tags responses with the latest packet's id (often the dummy's
        id), so filtering would drop the actual command output — including
        `say` echoes, `status`, etc.
        """
        if self._writer is None or self._reader is None:
            raise RconError("not connected")
        async with self._lock:
            self._send(SERVERDATA_EXECCOMMAND, command)
            # Empty RESPONSE_VALUE → the server echoes back a packet whose body
            # is the literal "\x00\x01\x00\x00" sentinel; that's our "done".
            self._send(SERVERDATA_RESPONSE_VALUE, "")
            chunks: list[str] = []
            while True:
                _, _, body = await self._recv()
                if body == "\x00\x01\x00\x00":
                    break
                chunks.append(body)
            return "".join(chunks)

    def _send(self, packet_type: int, body: str) -> int:
        assert self._writer is not None
        pid = self._next_id
        self._next_id += 1
        payload = body.encode("utf-8") + b"\x00\x00"
        packet = struct.pack("<iii", len(payload) + 8, pid, packet_type) + payload
        self._writer.write(packet)
        return pid

    async def _recv(self) -> tuple[int, int, str]:
        assert self._reader is not None
        size_bytes = await self._reader.readexactly(4)
        (size,) = struct.unpack("<i", size_bytes)
        data = await self._reader.readexactly(size)
        resp_id, resp_type = struct.unpack("<ii", data[:8])
        body = data[8:-2].decode("utf-8", errors="replace")
        return resp_id, resp_type, body
