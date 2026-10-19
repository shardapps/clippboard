"""Validate the public, synthetic Store asset pack without extra packages."""
import hashlib
import json
from pathlib import Path
import struct

root = Path(__file__).resolve().parent.parent
folder = root / "docs/marketing/store-0.6"
listing = json.loads((folder / "listing.json").read_text(encoding="utf-8"))
assert listing["productName"] == "Clippboard"
assert 0 < len(listing["description"]) <= 10000
assert 0 < len(listing["shortDescription"]) <= 270
assert 0 < len(listing["features"]) <= 20
assert all(0 < len(feature) <= 200 for feature in listing["features"])
keywords = listing["keywords"]
assert 0 < len(keywords) <= 7 and len(set(keywords)) == len(keywords)
assert all(0 < len(term) <= 40 and term.casefold() != "windows utility" for term in keywords)
assert sum(len(term.split()) for term in keywords) <= 21
seen = set()
for item in listing["screenshots"]:
    assert 0 < len(item["caption"]) <= 200
    path = (folder / "screenshots" / item["file"]).resolve()
    assert path.parent == (folder / "screenshots").resolve()
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n" and len(data) < 50 * 1024 * 1024
    width, height = struct.unpack(">II", data[16:24])
    assert (width, height) == (1600, 900)
    digest = hashlib.sha256(data).hexdigest()
    assert digest not in seen, "Duplicate screenshot"
    seen.add(digest)
assert len(seen) == 6
print("Store listing checks passed: natural metadata limits, six distinct 1600x900 PNGs and bounded captions.")
