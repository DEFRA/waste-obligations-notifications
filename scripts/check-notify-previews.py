#!/usr/bin/env python3
"""Fail unless the focused real-provider run reports all four previews passed."""
from pathlib import Path
import subprocess
from xml.parsers import expat


def read_cases(report):
    cases = []
    parser = expat.ParserCreate(namespace_separator="}")

    def reject_doctype(*_):
        raise SystemExit("Required Notify preview reports must not contain a document type.")

    def start_element(name, attributes):
        if name == "http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult":
            cases.append(attributes)

    parser.StartDoctypeDeclHandler = reject_doctype
    parser.StartElementHandler = start_element
    with report.open("rb") as stream:
        parser.ParseFile(stream)
    return cases


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
cases = read_cases(report)
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
