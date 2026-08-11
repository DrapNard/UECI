#!/usr/bin/env python3
"""Dependency-free sanity checker used only by maintainers when .NET is unavailable."""
from pathlib import Path
from xml.etree.ElementTree import iterparse
import sys

path = Path(sys.argv[1])
counts = {"File": 0, "Blob": 0, "Pack": 0}
base = None
for _, element in iterparse(path, events=("start",)):
    tag = element.tag.rsplit("}", 1)[-1]
    if tag == "DependencyManifest":
        base = element.attrib.get("BaseUrl")
    if tag in counts:
        counts[tag] += 1
    element.clear()
print(f"BaseUrl={base}")
print(" ".join(f"{k}s={v}" for k, v in counts.items()))
if not base or any(v == 0 for v in counts.values()):
    raise SystemExit(1)
