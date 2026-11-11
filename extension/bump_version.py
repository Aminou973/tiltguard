"""Bump the extension version (patch by default, or: python bump_version.py minor)."""
import json, sys, os
p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "manifest.json")
m = json.load(open(p, encoding="utf-8"))
a, b, c = map(int, m["version"].split("."))
m["version"] = f"{a}.{b + 1}.0" if len(sys.argv) > 1 and sys.argv[1] == "minor" else f"{a}.{b}.{c + 1}"
json.dump(m, open(p, "w", encoding="utf-8"), indent=2)
print(m["version"])
