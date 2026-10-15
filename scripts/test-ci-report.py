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
        workflow = Path(__file__).resolve().parents[1] / ".github/workflows/check-pull-request.yml"
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
                      failed=str(sum(outcome == "Failed" for _, outcome in cases)))
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

    def test_combines_previews_once_using_stable_identities(self):
        self.write_report("Consumer.IntegrationTests", [("Journey", "Passed"), ("Preview", "NotExecuted")], ", Assembly, Version=1.0")
        self.write_report("NotifySubmissionPreviews", [("Preview", "Passed")], ", Assembly, Version=2.0")
        self.assertIn("| Integration tests (including Notify previews) | ✅ 2/2 passed |", self.render())

    def test_count_grows_with_new_and_preview_only_cases(self):
        self.write_report("Consumer.IntegrationTests", [("Journey", "Passed")] + [(f"Preview{i}", "NotExecuted") for i in range(6)])
        self.write_report("NotifySubmissionPreviews", [(f"Preview{i}", "Passed") for i in range(7)])
        self.assertIn("| Integration tests (including Notify previews) | ✅ 8/8 passed |", self.render())

    def test_preserves_adverse_isolated_outcomes_after_successful_preview(self):
        for outcome in ("Failed", "Error", "Timeout", "Aborted", "Unknown"):
            with self.subTest(outcome=outcome):
                self.write_report("Consumer.IntegrationTests", [("Preview", outcome)])
                self.write_report("NotifySubmissionPreviews", [("Preview", "Passed")])
                self.assertIn("❌ 0/1 passed, 1 failed", self.render())

    def test_preview_failure_overrides_an_isolated_pass(self):
        self.write_report("Consumer.IntegrationTests", [("Preview", "Passed")])
        self.write_report("NotifySubmissionPreviews", [("Preview", "Failed")])
        self.assertIn("❌ 0/1 passed, 1 failed", self.render())

    def test_unrelated_skips_remain_visible(self):
        self.write_report("Consumer.IntegrationTests", [("Other", "NotExecuted"), ("Preview", "NotExecuted")])
        self.write_report("NotifySubmissionPreviews", [("Preview", "Passed")])
        self.assertIn("⚠️ 1/2 passed, 1 skipped", self.render())

    def test_missing_or_invalid_evidence_never_reports_all_passed(self):
        self.write_report("Consumer.IntegrationTests", [("Journey", "Passed")])
        self.assertIn("Combined integration results unavailable", self.render())
        preview = self.write_report("NotifySubmissionPreviews", [])
        self.assertIn("Combined integration results unavailable", self.render())
        preview.write_text("<broken")
        self.assertIn("Combined integration results unavailable", self.render())
        preview.write_text("<TestRun><ResultSummary><Counters total='1'/></ResultSummary></TestRun>")
        self.assertIn("Combined integration results unavailable", self.render())

    def test_duplicate_identities_and_missing_methods_are_rejected(self):
        self.write_report("Consumer.IntegrationTests", [("Journey", "Passed")])
        preview = self.write_report("NotifySubmissionPreviews", [("Preview", "Failed"), ("Preview", "Passed")])
        self.assertIn("Combined integration results unavailable", self.render())
        preview.write_text(preview.read_text().replace("TestMethod", "MissingMethod"))
        self.assertIn("Combined integration results unavailable", self.render())


if __name__ == "__main__":
    unittest.main()
