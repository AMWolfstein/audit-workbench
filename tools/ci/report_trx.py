#!/usr/bin/env python3
"""Summarize xUnit TRX results for the CI workflow.

The environment that drives this repository cannot download GitHub Actions
logs or artifacts, so test results are surfaced through runner annotations
(readable through the Checks API) and the job summary instead.
"""

from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

MAX_ANNOTATIONS = 10


def load_trx(path: Path) -> ET.Element:
    """Parse a TRX file, dropping its default namespace so tags match plainly."""
    text = path.read_text(encoding="utf-8", errors="replace")
    text = re.sub(r'\sxmlns(:\w+)?="[^"]*"', "", text)
    return ET.fromstring(text)


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: report_trx.py <results-directory>", file=sys.stderr)
        return 2

    results_dir = Path(sys.argv[1])
    trx_files = sorted(results_dir.rglob("*.trx"))
    if not trx_files:
        print("::error::No TRX files were produced by the test run.")
        return 1

    total = passed = failed = not_executed = 0
    failures: list[tuple[str, str]] = []

    for trx in trx_files:
        root = load_trx(trx)

        counters = root.find(".//Counters")
        if counters is not None:
            total += int(counters.get("total", "0"))
            passed += int(counters.get("passed", "0"))
            failed += int(counters.get("failed", "0"))
            not_executed += int(counters.get("notExecuted", "0"))

        for result in root.iter("UnitTestResult"):
            if result.get("outcome") != "Failed":
                continue
            name = result.get("testName", "<unknown>")
            message = ""
            error_info = result.find(".//Output/ErrorInfo/Message")
            if error_info is not None and error_info.text:
                message = " ".join(error_info.text.split())[:300]
            failures.append((name, message))

    summary = (
        f"Test results: {total} total - "
        f"{passed} passed, {failed} failed, {not_executed} not executed"
    )
    print(summary)

    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(f"### {summary}\n")
            if failures:
                handle.write("\n| Failed test | Message |\n|---|---|\n")
                for name, message in failures:
                    handle.write(f"| `{name}` | {message.replace(chr(124), '/')} |\n")

    for name, message in failures[:MAX_ANNOTATIONS]:
        print(f"::error title=Failed test::{name} - {message}")
    if len(failures) > MAX_ANNOTATIONS:
        print(
            f"::error title=Failed tests::"
            f"{len(failures) - MAX_ANNOTATIONS} more failures were not annotated"
        )

    print(f"::notice::{summary}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
