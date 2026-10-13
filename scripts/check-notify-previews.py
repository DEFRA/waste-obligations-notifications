#!/usr/bin/env python3
"""Fail unless the focused real-provider run reports all four previews passed."""
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

results = Path("TestResults/NotifyPreviews")
results.mkdir(parents=True, exist_ok=True)
report = results / "NotifyPreviews.trx"
report.unlink(missing_ok=True)
command = [
    "dotnet", "test", "--test-modules",
    "tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll",
    "--no-build", "--results-directory", str(results), "--",
    "--filter-class", "Defra.WasteObligations.Consumer.IntegrationTests.Scenarios.SubmissionTemplatePreviewTests",
    "--report-trx", "--report-trx-filename", report.name,
]
result = subprocess.run(command, check=False)
if result.returncode != 0:
    raise SystemExit(result.returncode)
if not report.is_file():
    raise SystemExit("Required Notify previews produced no execution report.")
root = ET.parse(report).getroot()
namespace = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
cases = root.findall(".//trx:UnitTestResult", namespace)
expected = "WhenConfiguredSubmissionTemplateIsPreviewed_ShouldRenderExpectedContent"
if (
    len(cases) != 4
    or any(case.get("outcome") != "Passed" or expected not in case.get("testName", "") for case in cases)
    or len({case.get("testName") for case in cases}) != 4
    or any(
        sum(variant in case.get("testName", "") for case in cases) != 1
        for variant in ("direct-en", "direct-cy", "scheme-en", "scheme-cy")
    )
):
    raise SystemExit("Required Notify previews must execute and pass all four submission variants.")
print("All four required Notify submission previews executed and passed.")
