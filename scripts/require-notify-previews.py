#!/usr/bin/env python3
"""Require fresh TRX evidence for exactly the four submission preview cases."""
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
        methods = {
            definition.get("id"): definition.find("t:TestMethod", namespace)
            for definition in definitions
        }
        observed = []
        for result in results:
            method = methods[result.get("testId")]
            if (method is None
                or not method.get("className", "").split(",")[0].endswith(".NotifySubmissionPreviewTests")
                or result.get("outcome") != "Passed"):
                raise ValueError()
            observed.append(method.get("name"))
        if (counters is None
            or any(counters.get(name) != "4" for name in ("total", "executed", "passed"))
            or any(int(value) != 0 for name, value in counters.attrib.items()
                   if name not in {"total", "executed", "passed"})
            or len(observed) != 4 or set(observed) != expected):
            raise ValueError()
    except (OSError, ET.ParseError, ValueError, KeyError, IndexError):
        sys.exit("Required Notify previews failed: expected four executed, passed submission cases and zero skipped.")
    print("Required Notify previews: all four submission cases executed and passed.")


if __name__ == "__main__":
    main()
