#!/usr/bin/env python3
"""Exercise the actual CI report shell with independent TRX fixtures."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


class CiReportTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        workflow = Path(__file__).resolve().parents[2] / ".github/workflows/check-pull-request.yml"
        section = workflow.read_text().split("      - name: Create CI report\n", 1)[1]
        section = section.split("      - name: Replace pull request comment\n", 1)[0]
        self.script = "\n".join(
            line[10:] for line in section.split("        run: |\n", 1)[1].splitlines()
        )

    def write_report(self, name, cases, assembly=""):
        report = self.root / "TestResults" / name / f"{name}.trx"
        report.parent.mkdir(parents=True, exist_ok=True)
        namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"  # NOSONAR
        root = ET.Element(namespace + "TestRun")
        definitions = ET.SubElement(root, namespace + "TestDefinitions")
        results = ET.SubElement(root, namespace + "Results")
        for number, (method, outcome) in enumerate(cases):
            identifier = f"{name}-{number}"
            definition = ET.SubElement(definitions, namespace + "UnitTest", id=identifier)
            ET.SubElement(definition, namespace + "TestMethod", className="Tests.Scenarios" + assembly, name=method)
            ET.SubElement(results, namespace + "UnitTestResult", testId=identifier, testName=method, outcome=outcome)
        summary = ET.SubElement(root, namespace + "ResultSummary")
        ET.SubElement(summary, namespace + "Counters", total=str(len(cases)),
                      passed=str(sum(outcome == "Passed" for _, outcome in cases)),
                      failed=str(sum(outcome == "Failed" for _, outcome in cases)),
                      notExecuted=str(sum(outcome == "NotExecuted" for _, outcome in cases)))
        ET.ElementTree(root).write(report)
        return report

    def render(self):
        env = dict(os.environ, WORKFLOW_VALIDATION="success", DEPENDENCY_REVIEW="success",
                   REPORT_DOWNLOAD="success", DOCKER_BUILD="success", PR_VALIDATOR="success",
                   TRIVY="skipped", GITHUB_SERVER_URL="https://github.com",
                   GITHUB_REPOSITORY="example/repository", GITHUB_RUN_ID="123")
        subprocess.run(["bash", "-eu", "-c", self.script], cwd=self.root, env=env,
                       check=True, capture_output=True, text=True)
        return (self.root / "ci-report.md").read_text()

    def test_integration_and_preview_results_are_separate(self):
        self.write_report("Consumer.IntegrationTests", [("Journey", "Passed")])
        self.write_report("NotifyPreviews", [("SubmissionPreview", "Passed"), ("CancellationPreview", "Passed")])
        report = self.render()
        self.assertIn("| Integration tests | ✅ 1/1 passed |", report)
        self.assertIn("| Preview tests | ✅ 2/2 passed |", report)
        self.assertNotIn("including Notify previews", report)
        self.assertNotIn("Notify submission previews", report)
        self.assertNotIn("Notify cancellation previews", report)

    def test_inconsistent_individual_counters_never_report_success(self):
        report = self.write_report("NotifyPreviews", [("Preview", "Passed")])
        root = ET.parse(report).getroot()
        root.find("{*}ResultSummary/{*}Counters").set("failed", "1")
        ET.ElementTree(root).write(report)
        self.assertIn("| Preview tests | ⚪ Results unavailable (invalid TRX evidence) |", self.render())

    def test_count_grows_from_report_evidence(self):
        self.write_report("Consumer.IntegrationTests", [(f"Journey{i}", "Passed") for i in range(3)])
        self.write_report("NotifyPreviews", [(f"Preview{i}", "Passed") for i in range(7)])
        report = self.render()
        self.assertIn("| Integration tests | ✅ 3/3 passed |", report)
        self.assertIn("| Preview tests | ✅ 7/7 passed |", report)

    def test_adverse_outcomes_and_skips_remain_visible_in_their_own_rows(self):
        for outcome in ("Failed", "Error", "Timeout", "Aborted", "Unknown"):
            with self.subTest(outcome=outcome):
                self.write_report("Consumer.IntegrationTests", [("Journey", outcome)])
                self.write_report("NotifyPreviews", [("Preview", "Passed")])
                report = self.render()
                self.assertIn("| Integration tests | ❌ 0/1 passed, 1 failed |", report)
                self.assertIn("| Preview tests | ✅ 1/1 passed |", report)
        self.write_report("Consumer.IntegrationTests", [("Journey", "NotExecuted")])
        self.write_report("NotifyPreviews", [("Preview", "Failed")])
        report = self.render()
        self.assertIn("| Integration tests | ⚠️ 0/1 passed, 1 skipped |", report)
        self.assertIn("| Preview tests | ❌ 0/1 passed, 1 failed |", report)

    def test_missing_or_invalid_evidence_never_reports_all_passed(self):
        self.assertIn("| Preview tests | ⚪ Not run |", self.render())
        preview = self.write_report("NotifyPreviews", [])
        self.assertIn("| Preview tests | ⚪ Results unavailable", self.render())
        preview.write_text("<broken")
        self.assertIn("| Preview tests | ⚪ Results unavailable", self.render())
        preview.write_text("<TestRun><ResultSummary><Counters total='1'/></ResultSummary></TestRun>")
        self.assertIn("| Preview tests | ⚪ Results unavailable", self.render())

    def test_duplicate_identities_and_missing_methods_are_rejected(self):
        preview = self.write_report("NotifyPreviews", [("Preview", "Failed"), ("Preview", "Passed")])
        self.assertIn("| Preview tests | ⚪ Results unavailable", self.render())
        preview.write_text(preview.read_text().replace("TestMethod", "MissingMethod"))
        self.assertIn("| Preview tests | ⚪ Results unavailable", self.render())


if __name__ == "__main__":
    unittest.main()
