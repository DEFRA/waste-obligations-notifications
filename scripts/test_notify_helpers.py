"""Credential-free checks of the helpers' public process boundaries."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parent


class NotifyHelperTests(unittest.TestCase):
    def test_local_fallback_and_explicit_destination_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "scripts").mkdir()
            launcher = root / "scripts/with-notify-key.py"
            shutil.copyfile(SCRIPTS / launcher.name, launcher)
            literal = "synthetic-$(touch should-not-exist)-`literal`"
            (root / ".env").write_text("=".join(("GovukNotify_ApiKey", json.dumps(literal))) + "\n", encoding="utf-8")
            for mode, destination in (("preview", "GOVUKNOTIFY_APIKEY"), ("consumer", "Notify__ApiKey")):
                for explicit in (None, "", "explicit-synthetic-key"):
                    with self.subTest(mode=mode, explicit=explicit):
                        environment = {key: value for key, value in os.environ.items()
                                       if key.casefold() not in ("govuknotify_apikey", "notify__apikey")}
                        if explicit is not None:
                            environment[destination] = explicit
                        result = subprocess.run(
                            [sys.executable, str(launcher), mode, sys.executable, "-c",
                             "import json,os; print(json.dumps(dict(os.environ)))"],
                            cwd=root, env=environment, capture_output=True, text=True, check=True,
                        )
                        self.assertEqual(literal if explicit is None else explicit, json.loads(result.stdout)[destination])
                        self.assertFalse((root / "should-not-exist").exists())
            environment = {key: value for key, value in os.environ.items() if key.casefold() != "notify__apikey"}
            environment["NOTIFY__APIKEY"] = ""
            result = subprocess.run(
                [sys.executable, str(launcher), "consumer", sys.executable, "-c",
                 "import json,os; print(json.dumps(dict(os.environ)))"],
                cwd=root, env=environment, capture_output=True, text=True, check=True,
            )
            actual = json.loads(result.stdout)
            self.assertEqual("", actual["NOTIFY__APIKEY"])
            self.assertNotIn("Notify__ApiKey", actual)

    def test_launcher_continues_path_after_missing_interpreter(self):
        with tempfile.TemporaryDirectory(dir=SCRIPTS.parent) as directory:
            root = Path(directory)
            first = root / "first"
            second = root / "second"
            first.mkdir()
            second.mkdir()
            name = "notify-helper-command"
            (first / name).write_text("#!/missing/mo549/interpreter\n", encoding="utf-8")
            (second / name).write_text(
                "#!" + sys.executable + "\nprint('command executed')\n", encoding="utf-8"
            )
            (first / name).chmod(0o700)
            (second / name).chmod(0o700)
            environment = os.environ | {
                "PATH": str(first) + os.pathsep + str(second),
                "GOVUKNOTIFY_APIKEY": "",
            }
            result = subprocess.run(
                [sys.executable, str(SCRIPTS / "with-notify-key.py"), "preview", name],
                cwd=root, env=environment, capture_output=True, text=True, check=True,
            )
            self.assertEqual("command executed", result.stdout.strip())

    def test_required_gate_accepts_four_and_rejects_nonexecution(self):
        # The isolated worker mounts /tmp without executable-file support.
        with tempfile.TemporaryDirectory(dir=SCRIPTS.parent) as directory:
            root = Path(directory)
            fake_dotnet = root / "dotnet"
            fake_dotnet.write_text(
                "#!/usr/bin/env python3\n"
                "import os,pathlib,sys\n"
                "mode=os.environ['FAKE_PREVIEW_MODE']\n"
                "if mode=='error': sys.exit(1)\n"
                "if mode=='missing': sys.exit(0)\n"
                "path=pathlib.Path(sys.argv[sys.argv.index('--results-directory')+1])\n"
                "count=3 if mode=='partial' else 4\n"
                "outcome='NotExecuted' if mode=='skipped' else 'Passed'\n"
                "names=['direct-en','direct-cy','scheme-en','scheme-cy']\n"
                "if mode=='duplicate': names=['direct-en']*4\n"
                "cases=''.join(f'<UnitTestResult testName=\"WhenConfiguredSubmissionTemplateIsPreviewed_ShouldRenderExpectedContent({names[i]})\" outcome=\"{outcome}\" />' for i in range(count))\n"
                "prefix='<!DOCTYPE TestRun [<!ENTITY unused \"fixture\">]>' if mode=='doctype' else ''\n"
                "(path/'NotifyPreviews.trx').write_text(prefix+'<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>'+cases+'</Results></TestRun>')\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o700)
            for mode in ("passed", "error", "missing", "partial", "skipped", "duplicate", "doctype"):
                with self.subTest(mode=mode):
                    environment = os.environ | {"PATH": str(root) + os.pathsep + os.environ["PATH"], "FAKE_PREVIEW_MODE": mode}
                    result = subprocess.run(
                        [sys.executable, str(SCRIPTS / "check-notify-previews.py")],
                        cwd=root, env=environment, capture_output=True, text=True, check=False,
                    )
                    self.assertEqual(mode == "passed", result.returncode == 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
