#!/usr/bin/env python3
"""Read newline-delimited live snapshots from the local OTAPI telemetry plugin."""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from collections.abc import Iterator
from typing import Any


def snapshots(host: str = "127.0.0.1", port: int = 38761) -> Iterator[dict[str, Any]]:
    """Yield snapshots and reconnect if the game server restarts or the socket drops."""
    while True:
        try:
            with socket.create_connection((host, port), timeout=5) as connection:
                connection.settimeout(None)
                with connection.makefile("r", encoding="utf-8", newline="\n") as stream:
                    for line in stream:
                        if line.strip():
                            yield json.loads(line)
        except (OSError, json.JSONDecodeError) as error:
            print(f"[telemetry] connection lost: {error}; retrying in 2s", file=sys.stderr)
            time.sleep(2)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=38761)
    args = parser.parse_args()

    for snapshot in snapshots(args.host, args.port):
        print(json.dumps(snapshot, ensure_ascii=False, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    main()
