#!/usr/bin/env python3
"""Inventory MD task markers. Existing checkmarks are NOT independent verification."""
import argparse
import csv
import hashlib
import re
import subprocess
from pathlib import Path


def collect(root):
    names = subprocess.check_output(
        ["git", "-C", str(root), "ls-files", "-z", "--", "*.md"]
    ).decode().split("\0")
    records = {}
    documents = []
    for name in sorted(set(names)):
        path = root / name
        if not name or not path.is_file() or name.startswith(("docs/", "archive/")):
            continue
        text = path.read_text(encoding="utf-8-sig")
        documents.append(name)
        lines = text.splitlines()
        for i, line in enumerate(lines):
            marker = re.search(r"\[([ xX])\]", line)
            narrative = re.search(r"Осталось|Остаток|не сделано|не реализовано|TODO|^##.*Дальше", line, re.I)
            if not marker and not narrative:
                continue
            body = line.strip()
            # Include indented continuation lines; preserve a multi-line requirement.
            j = i + 1
            while j < len(lines) and lines[j].startswith(("  ", "\t")) and lines[j].strip():
                body += " " + lines[j].strip()
                j += 1
            status = ("reported_done_not_revalidated" if marker.group(1).lower() == "x"
                      else "open_not_revalidated") if marker else "narrative_review_required"
            key = (status, body)
            row = records.setdefault(key, {
                "id": hashlib.sha256((status + "\0" + body).encode()).hexdigest()[:12],
                "status": status, "origins": [], "text": body,
            })
            row["origins"].append(f"{name}:{i + 1}")
    return documents, sorted(records.values(), key=lambda r: r["origins"][0])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    documents, rows = collect(root)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle, delimiter="\t", lineterminator="\n")
        writer.writerow(["id", "status", "origins", "text"])
        for row in rows:
            writer.writerow([row["id"], row["status"], "; ".join(row["origins"]), row["text"]])
    print(f"documents={len(documents)} unique_records={len(rows)} output={args.output}")
    for status in sorted({r["status"] for r in rows}):
        print(f"{status}={sum(r['status'] == status for r in rows)}")


if __name__ == "__main__":
    main()
