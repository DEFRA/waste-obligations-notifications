#!/usr/bin/env python3
"""Require fresh TRX evidence for the selected submission or cancellation preview cases."""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def main():
    try:
        report = ET.parse(Path(sys.argv[1])).getroot()
        # This canonical TRX XML namespace identifies a format; it is not an HTTP endpoint.
        namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}  # NOSONAR
        counters = report.find("t:ResultSummary/t:Counters", namespace)
        results = report.findall("t:Results/t:UnitTestResult", namespace)
        definitions = report.findall("t:TestDefinitions/t:UnitTest", namespace)
        expected = {
            "DirectEnglish_ShouldRenderSubmission",
            "DirectWelsh_ShouldRenderSubmission",
            "SchemeEnglish_ShouldRenderSubmission",
            "SchemeWelsh_ShouldRenderSubmission",
        }
        kind = sys.argv[2] if len(sys.argv) == 3 else "submission"
        if len(sys.argv) > 3 or kind not in {"submission", "cancellation"}:
            raise ValueError()
        if kind == "cancellation":
            expected = {
                "NotSignedByCorrectPersonEnglish_ShouldRenderCancellation",
                "NotSignedByCorrectPersonWelsh_ShouldRenderCancellation",
                "RecyclingObligationsChangedEnglish_ShouldRenderCancellation",
                "RecyclingObligationsChangedWelsh_ShouldRenderCancellation",
                "CanMeetRecyclingObligationsEnglish_ShouldRenderCancellation",
                "CanMeetRecyclingObligationsWelsh_ShouldRenderCancellation",
                "ProducerRequestedEnglish_ShouldRenderCancellation",
                "ProducerRequestedWelsh_ShouldRenderCancellation",
            }
        expected_class = ".NotifyCancellationPreviewTests" if kind == "cancellation" else ".NotifySubmissionPreviewTests"
        count = str(len(expected))
        methods = {
            definition.get("id"): definition.find("t:TestMethod", namespace)
            for definition in definitions
        }
        observed = []
        for result in results:
            method = methods[result.get("testId")]
            if (method is None
                or not method.get("className", "").split(",")[0].endswith(expected_class)
                or result.get("outcome") != "Passed"):
                raise ValueError()
            observed.append(method.get("name"))
        if (counters is None
            or any(counters.get(name) != count for name in ("total", "executed", "passed"))
            or any(int(value) != 0 for name, value in counters.attrib.items()
                   if name not in {"total", "executed", "passed"})
            or len(observed) != len(expected) or set(observed) != expected):
            raise ValueError()
    except (OSError, ET.ParseError, ValueError, KeyError, IndexError):
        sys.exit("Required Notify previews failed: expected every selected case to execute and pass with zero skipped.")
    print(f"Required Notify previews: all {'eight cancellation' if kind == 'cancellation' else 'four submission'} cases executed and passed.")


if __name__ == "__main__":
    main()
