"""List the models a Codex CLI app-server offers, across every model/list page.

Usage: python probe-model-list.py <path-to-codex.exe>

Prints each model with its hidden/default flags. Codex delivers the model catalog per client
version, so run it against two executables to see why a model is missing from the picker.
"""
import json
import subprocess
import sys


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)

    proc = subprocess.Popen(
        [sys.argv[1], "app-server"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
    )

    def send(message: dict) -> None:
        proc.stdin.write(json.dumps(message) + "\n")
        proc.stdin.flush()

    def wait(request_id: int) -> dict:
        while True:
            line = proc.stdout.readline()
            if not line:
                raise SystemExit("app-server closed the connection")
            message = json.loads(line)
            if message.get("id") == request_id:
                return message

    try:
        send({"id": 1, "method": "initialize", "params": {"clientInfo": {"name": "probe", "version": "0"}}})
        wait(1)
        send({"method": "initialized"})

        cursor, request_id, page = None, 10, 0
        while True:
            page += 1
            send({"id": request_id, "method": "model/list", "params": {"includeHidden": True, "cursor": cursor}})
            result = wait(request_id).get("result", {})
            for model in result.get("data", []):
                name = model.get("model") or model.get("id")
                print(f"page{page}  {name:<24} hidden={model.get('hidden')} default={model.get('isDefault')}")
            cursor = result.get("nextCursor")
            request_id += 1
            if not cursor or page > 20:
                break
    finally:
        proc.kill()


if __name__ == "__main__":
    main()
