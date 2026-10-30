#!/usr/bin/env python3
"""Require complete TRX evidence for every supported Notify preview."""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


EXPECTED = {
    "NotifySubmissionPreviewTests": {
        "DirectEnglish_ShouldRenderSubmission",
        "DirectWelsh_ShouldRenderSubmission",
        "SchemeEnglish_ShouldRenderSubmission",
        "SchemeWelsh_ShouldRenderSubmission",
    },
    "NotifyCancellationPreviewTests": {
        "NotSignedByCorrectPersonEnglish_ShouldRenderCancellation",
        "NotSignedByCorrectPersonWelsh_ShouldRenderCancellation",
        "RecyclingObligationsChangedEnglish_ShouldRenderCancellation",
        "RecyclingObligationsChangedWelsh_ShouldRenderCancellation",
        "CanMeetRecyclingObligationsEnglish_ShouldRenderCancellation",
        "CanMeetRecyclingObligationsWelsh_ShouldRenderCancellation",
        "ProducerRequestedEnglish_ShouldRenderCancellation",
        "ProducerRequestedWelsh_ShouldRenderCancellation",
    },
    "NotifyReminderPreviewTests": {
        "EnglishEnvironmentAgency_ShouldRenderReminder",
        "EnglishSepa_ShouldRenderReminder",
        "EnglishNrw_ShouldRenderReminder",
        "EnglishNiea_ShouldRenderReminder",
        "WelshBilingual_ShouldRenderReminder",
    },
}


def main():
    try:
        if len(sys.argv) != 2:
            raise ValueError()
        report = ET.parse(Path(sys.argv[1])).getroot()
        # This canonical TRX XML namespace identifies a format; it is not an HTTP endpoint.
        namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}  # NOSONAR
        counters = report.find("t:ResultSummary/t:Counters", namespace)
        results = report.findall("t:Results/t:UnitTestResult", namespace)
        definitions = report.findall("t:TestDefinitions/t:UnitTest", namespace)
        expected = {
            (f"Defra.WasteObligations.Consumer.IntegrationTests.Scenarios.{class_name}", method)
            for class_name, methods in EXPECTED.items()
            for method in methods
        }
        methods = {}
        for definition in definitions:
            identifier = definition.attrib["id"]
            if not identifier or identifier in methods:
                raise ValueError()
            methods[identifier] = definition.find("t:TestMethod", namespace)
        observed = []
        result_ids = set()
        for result in results:
            identifier = result.attrib["testId"]
            if identifier in result_ids:
                raise ValueError()
            result_ids.add(identifier)
            method = methods[identifier]
            if method is None or result.get("outcome") != "Passed":
                raise ValueError()
            observed.append((method.get("className", "").split(",")[0], method.get("name")))
        count = str(len(expected))
        if (counters is None
            or any(counters.get(name) != count for name in ("total", "executed", "passed"))
            or any(int(value) != 0 for name, value in counters.attrib.items()
                   if name not in {"total", "executed", "passed"})
            or len(observed) != len(expected) or set(observed) != expected
            or set(methods) != result_ids):
            raise ValueError()
    except (OSError, ET.ParseError, ValueError, KeyError, IndexError):
        sys.exit("Required Notify previews failed: expected every case to execute and pass with zero skipped.")
    print(f"Required Notify previews: all {len(expected)} cases executed and passed.")


if __name__ == "__main__":
    main()
