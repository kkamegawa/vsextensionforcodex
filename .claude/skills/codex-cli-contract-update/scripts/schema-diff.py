"""Property-level diff of generated app-server JSON schemas between two Codex CLI versions.

Usage: python schema-diff.py <baseline-dir> <target-dir> <relative-file> [<relative-file> ...]

Example:
  python schema-diff.py schemas/0.155.1/stable schemas/0.159.1/stable \
      ClientRequest.json ServerNotification.json v2/ModelListResponse.json

Output lines: "+" added, "-" removed, "~" changed (enum/required sets show +added -removed).
Shared definitions are printed once, except in the method tables (ClientRequest, ServerNotification,
ServerRequest). Descriptions and titles are ignored.
"""
import json
import os
import sys

IGNORE = {"description", "title", "$schema", "examples", "default", "markdownDescription"}
METHOD_TABLES = ("ClientRequest.json", "ServerNotification.json", "ServerRequest.json")


def short(value) -> str:
    text = json.dumps(value, sort_keys=True)
    return text if len(text) < 160 else text[:157] + "..."


def item_key(item) -> str:
    if isinstance(item, dict):
        for key in ("$ref", "title"):
            if key in item:
                return str(item[key])
        properties = item.get("properties", {})
        for key in ("method", "type"):
            if key in properties and "enum" in properties[key]:
                return f"{key}={properties[key]['enum']}"
    return json.dumps(item, sort_keys=True)[:120]


def walk(a, b, path, out) -> None:
    if type(a) is not type(b):
        out.append(f"~ {path}: {short(a)} -> {short(b)}")
        return
    if isinstance(a, dict):
        for key in sorted(set(a) | set(b)):
            if key in IGNORE:
                continue
            if key not in a:
                out.append(f"+ {path}/{key}: {short(b[key])}")
            elif key not in b:
                out.append(f"- {path}/{key}: {short(a[key])}")
            else:
                walk(a[key], b[key], f"{path}/{key}", out)
    elif isinstance(a, list):
        if all(not isinstance(x, (dict, list)) for x in a + b):
            before, after = set(map(str, a)), set(map(str, b))
            if before != after:
                out.append(f"~ {path}: +{sorted(after - before)} -{sorted(before - after)}")
            return
        keyed_a = {item_key(x): x for x in a}
        keyed_b = {item_key(x): x for x in b}
        for key in sorted(set(keyed_a) | set(keyed_b)):
            if key not in keyed_a:
                out.append(f"+ {path}[{key}]")
            elif key not in keyed_b:
                out.append(f"- {path}[{key}]")
            else:
                walk(keyed_a[key], keyed_b[key], f"{path}[{key}]", out)
    elif a != b:
        out.append(f"~ {path}: {short(a)} -> {short(b)}")


def definition_of(line: str):
    head = line.split(":")[0]
    return head.split("/definitions/")[1].split("/")[0] if "/definitions/" in head else None


def main() -> None:
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    baseline, target = sys.argv[1], sys.argv[2]
    seen = set()
    for relative in sys.argv[3:]:
        with open(os.path.join(baseline, relative), encoding="utf-8") as f:
            a = json.load(f)
        with open(os.path.join(target, relative), encoding="utf-8") as f:
            b = json.load(f)
        out = []
        walk(a, b, "", out)
        print(f"===== {relative}")
        for line in out:
            definition = definition_of(line)
            if definition in seen and not relative.endswith(METHOD_TABLES):
                continue
            print(line)
        seen.update(d for d in map(definition_of, out) if d)


if __name__ == "__main__":
    main()
