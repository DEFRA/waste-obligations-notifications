"""Contract tests for local credentials and required Notify preview evidence."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


def load_script(name):
    specification = importlib.util.spec_from_file_location(
        name.replace("-", "_"), ROOT / "scripts" / (name + ".py")
    )
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


wrapper = load_script("with-notify-env")
guard = load_script("require-notify-previews")


class NotifyEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def write_environment_file(self, settings):
        (self.root / ".env").write_text(
            "\n".join(f"{name}={value}" for name, value in settings.items()) + "\n",
            encoding="utf-8",
        )

    def test_missing_file_or_unrelated_key_has_no_fallback(self):
        with patch.dict(os.environ, {"UNRELATED": "retained"}, clear=True):
            self.assertEqual(wrapper.notify_environment(self.root), {"UNRELATED": "retained"})
            self.write_environment_file({"Other_Key": "value"})
            self.assertEqual(wrapper.notify_environment(self.root), {"UNRELATED": "retained"})

    def test_quoted_unquoted_and_empty_local_keys_are_literal(self):
        cases = [(" 'synthetic' ", "synthetic"), ('"synthetic"', "synthetic"),
                 ("synthetic", "synthetic"), ("", ""),
                 ("$(touch SHOULD_NOT_EXIST)", "$(touch SHOULD_NOT_EXIST)")]
        for value, expected in cases:
            with self.subTest(value=value), patch.dict(os.environ, {}, clear=True):
                self.write_environment_file({"Other_Key": "ignored", " GovukNotify_ApiKey ": value})
                self.assertEqual(wrapper.notify_environment(self.root), {
                    "GOVUKNOTIFY_APIKEY": expected, "Notify__ApiKey": expected
                })
                self.assertFalse((self.root / "SHOULD_NOT_EXIST").exists())

    def test_explicit_settings_win_independently_including_empty_and_uppercase(self):
        self.write_environment_file({"GovukNotify_ApiKey": "fallback"})
        cases = [
            ({"GOVUKNOTIFY_APIKEY": "preview"}, {"GOVUKNOTIFY_APIKEY": "preview", "Notify__ApiKey": "fallback"}),
            ({"Notify__ApiKey": "runtime"}, {"GOVUKNOTIFY_APIKEY": "fallback", "Notify__ApiKey": "runtime"}),
            ({"NOTIFY__APIKEY": "fixture"}, {"GOVUKNOTIFY_APIKEY": "fallback", "NOTIFY__APIKEY": "fixture"}),
            ({"GOVUKNOTIFY_APIKEY": "", "Notify__ApiKey": ""}, {"GOVUKNOTIFY_APIKEY": "", "Notify__ApiKey": ""}),
        ]
        for environment, expected in cases:
            with self.subTest(environment=environment), patch.dict(os.environ, environment, clear=True):
                self.assertEqual(wrapper.notify_environment(self.root), expected)

    def test_run_passes_only_known_arguments_and_child_status(self):
        with patch.object(sys, "argv", ["wrapper", "run"]), \
                patch.object(wrapper, "notify_environment", return_value={"synthetic": "value"}) as environment, \
                patch.object(wrapper.subprocess, "run", return_value=Mock(returncode=7)) as launch:
            self.assertEqual(wrapper.main(), 7)
            environment.assert_called_once_with(ROOT)
            launch.assert_called_once_with(
                ["dotnet", "run", "--project", "src/Consumer"],
                cwd=ROOT, env={"synthetic": "value"}, check=False,
            )

    def test_arbitrary_executables_and_extra_arguments_are_rejected_before_launch(self):
        cases = [[], ["preview"], ["sh"], ["run", "--project", "attacker"],
                 ["preview;touch SHOULD_NOT_EXIST"], ["dotnet", "run"]]
        for arguments in cases:
            with self.subTest(arguments=arguments), patch.object(sys, "argv", ["wrapper"] + arguments), \
                    patch.object(wrapper.subprocess, "run") as launch:
                with self.assertRaises(SystemExit):
                    wrapper.main()
                launch.assert_not_called()


class NotifyPreviewEvidenceTests(unittest.TestCase):
    CASES = [
        ("NotifySubmissionPreviewTests", name) for name in [
            "DirectEnglish_ShouldRenderSubmission", "DirectWelsh_ShouldRenderSubmission",
            "SchemeEnglish_ShouldRenderSubmission", "SchemeWelsh_ShouldRenderSubmission"]
    ] + [
        ("NotifyCancellationPreviewTests", name) for name in [
            "NotSignedByCorrectPersonEnglish_ShouldRenderCancellation",
            "NotSignedByCorrectPersonWelsh_ShouldRenderCancellation",
            "RecyclingObligationsChangedEnglish_ShouldRenderCancellation",
            "RecyclingObligationsChangedWelsh_ShouldRenderCancellation",
            "CanMeetRecyclingObligationsEnglish_ShouldRenderCancellation",
            "CanMeetRecyclingObligationsWelsh_ShouldRenderCancellation",
            "ProducerRequestedEnglish_ShouldRenderCancellation",
            "ProducerRequestedWelsh_ShouldRenderCancellation"]
    ] + [
        ("NotifyReminderPreviewTests", name) for name in [
            "EnglishEnvironmentAgency_ShouldRenderReminder", "EnglishSepa_ShouldRenderReminder",
            "EnglishNrw_ShouldRenderReminder", "EnglishNiea_ShouldRenderReminder",
            "WelshBilingual_ShouldRenderReminder"]
    ]
    # This canonical TRX XML namespace identifies a format; it is not an HTTP endpoint.
    NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"  # NOSONAR

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "previews.trx"

    def report(self):
        root = ET.Element("TestRun", xmlns=self.NAMESPACE)
        summary = ET.SubElement(root, "ResultSummary")
        count = str(len(self.CASES))
        counters = ET.SubElement(summary, "Counters", total=count, executed=count, passed=count, failed="0", notExecuted="0")
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, (class_name, method) in enumerate(self.CASES):
            definition = ET.SubElement(definitions, "UnitTest", id=str(index))
            ET.SubElement(definition, "TestMethod", name=method,
                          className=f"Defra.WasteObligations.Consumer.IntegrationTests.Scenarios.{class_name}, Assembly")
            ET.SubElement(results, "UnitTestResult", testId=str(index), outcome="Passed")
        return root, counters, definitions, results

    def invoke(self):
        with patch.object(sys, "argv", ["guard", str(self.path)]), contextlib.redirect_stdout(io.StringIO()) as output:
            guard.main()
            return output.getvalue()

    def test_all_unique_passed_cases_are_accepted(self):
        root, _, _, _ = self.report()
        ET.ElementTree(root).write(self.path)
        self.assertIn("all 17 cases executed and passed", self.invoke())

    def test_failed_skipped_duplicate_unexpected_and_unmatched_cases_are_rejected(self):
        for kind in ["failed", "skipped", "duplicate", "wrong-class", "wrong-family", "unknown-id",
                     "missing-method", "fewer", "unexpected", "duplicate-id", "unused-definition"]:
            with self.subTest(kind=kind):
                root, _, definitions, results = self.report()
                if kind in ("failed", "skipped"):
                    results[3].set("outcome", "Failed" if kind == "failed" else "NotExecuted")
                elif kind == "duplicate":
                    definitions[3][0].set("name", self.CASES[0][1])
                elif kind == "wrong-class":
                    definitions[3][0].set("className", "Other.NotifySubmissionPreviewTests")
                elif kind == "wrong-family":
                    definitions[3][0].set("className", "Defra.WasteObligations.Consumer.IntegrationTests.Scenarios.NotifyCancellationPreviewTests")
                elif kind == "unknown-id":
                    results[3].set("testId", "missing")
                elif kind == "missing-method":
                    definitions[3].remove(definitions[3][0])
                elif kind == "unexpected":
                    definitions[3][0].set("name", "UnexpectedPreview")
                elif kind == "duplicate-id":
                    definitions[3].set("id", "0")
                elif kind == "unused-definition":
                    ET.SubElement(definitions, "UnitTest", id="unused")
                else:
                    results.remove(results[3])
                ET.ElementTree(root).write(self.path)
                with self.assertRaises(SystemExit):
                    self.invoke()

    def test_neither_family_alone_satisfies_the_gate(self):
        for family in ("NotifySubmissionPreviewTests", "NotifyCancellationPreviewTests", "NotifyReminderPreviewTests"):
            with self.subTest(family=family):
                root, _, definitions, results = self.report()
                for index in reversed(range(len(self.CASES))):
                    if self.CASES[index][0] != family:
                        definitions.remove(definitions[index])
                        results.remove(results[index])
                ET.ElementTree(root).write(self.path)
                with self.assertRaises(SystemExit):
                    self.invoke()

    def test_incomplete_nonzero_or_malformed_counters_are_rejected(self):
        for name, value in [("total", "3"), ("executed", "3"), ("passed", "3"),
                            ("notExecuted", "1"), ("failed", "invalid"), ("passed", None)]:
            with self.subTest(name=name, value=value):
                root, counters, _, _ = self.report()
                if value is None:
                    counters.attrib.pop(name)
                else:
                    counters.set(name, value)
                ET.ElementTree(root).write(self.path)
                with self.assertRaises(SystemExit):
                    self.invoke()
        root, counters, _, _ = self.report()
        root.find("ResultSummary").remove(counters)
        ET.ElementTree(root).write(self.path)
        with self.assertRaises(SystemExit):
            self.invoke()

    def test_missing_malformed_reports_and_incorrect_arguments_are_rejected(self):
        with self.assertRaises(SystemExit):
            self.invoke()
        self.path.write_text("not XML", encoding="utf-8")
        with self.assertRaises(SystemExit):
            self.invoke()
        for arguments in ([], [str(self.path), "cancellation"]):
            with patch.object(sys, "argv", ["guard"] + arguments), self.assertRaises(SystemExit):
                guard.main()
