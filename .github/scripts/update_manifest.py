"""Add (or replace) a version entry for this plugin in the repository manifest.

Jellyfin reads manifest.json from the raw GitHub URL to list the plugin in its
catalog; each version entry points at the zip attached to a GitHub Release.
"""

import json
import os
from datetime import datetime, timezone

MANIFEST = "manifest.json"

with open("meta.json", encoding="utf-8") as f:
    meta = json.load(f)

try:
    with open(MANIFEST, encoding="utf-8") as f:
        manifest = json.load(f)
except FileNotFoundError:
    manifest = []

plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
if plugin is None:
    plugin = {"guid": meta["guid"], "versions": []}
    manifest.append(plugin)

plugin.update(
    {
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": os.environ["OWNER"],
        "category": meta["category"],
    }
)

version = os.environ["VERSION"]
entry = {
    "version": version,
    "changelog": os.environ["CHANGELOG"],
    "targetAbi": os.environ["TARGET_ABI"],
    "sourceUrl": os.environ["SOURCE_URL"],
    "checksum": os.environ["CHECKSUM"],
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

versions = [v for v in plugin.get("versions", []) if v["version"] != version]
versions.append(entry)
versions.sort(key=lambda v: tuple(int(p) for p in v["version"].split(".")), reverse=True)
plugin["versions"] = versions

with open(MANIFEST, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
