from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ENTRYPOINT = Path(__file__).resolve().parents[1] / "docker-entrypoint.sh"


class ServiceCredentialsTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        binaries = self.root / "bin"
        binaries.mkdir()
        self.cookie = self.root / "cookie"
        self.cookie.write_text("dummy:cookie")
        self.log = self.root / "s5cmd.jsonl"
        commands = {
            "cat": """
import os
import sys
from pathlib import Path
if sys.argv[1:] == ["/app/COMMIT"]:
    print("test-commit")
elif sys.argv[1:] == [os.environ["BITCOIN_RPC_COOKIE"]]:
    print(Path(sys.argv[1]).read_text(), end="")
else:
    sys.exit(1)
""",
            "curl": """
import json
import sys
payload = sys.argv[sys.argv.index("--data") + 1]
if payload == "@-":
    sys.stdin.read()
    print("[]")
else:
    method = json.loads(payload)["method"]
    values = {
        "getblockchaininfo": {"headers": 10, "blocks": 10},
        "getindexinfo": {},
        "getblockcount": 10,
    }
    print(json.dumps({"result": values[method]}))
""",
            "s5cmd": """
import json
import os
import sys
with open(os.environ["CREDENTIAL_TEST_LOG"], "a") as log:
    log.write(json.dumps({
        "args": sys.argv[1:],
        "access": os.environ.get("AWS_ACCESS_KEY_ID"),
        "secret": os.environ.get("AWS_SECRET_ACCESS_KEY"),
    }) + "\\n")
""",
            "dotnet": "import sys; sys.exit(1)\n",
            "sleep": "import sys; sys.exit(9)\n",
        }
        for name, body in commands.items():
            executable = binaries / name
            executable.write_text(f"#!{sys.executable}\n{body}")
            executable.chmod(0o755)
        self.environment = {
            key: value
            for key, value in os.environ.items()
            if not key.startswith(("S3_", "AWS_", "DUMPLINGS_"))
        }
        self.environment.update(
            PATH=f"{binaries}{os.pathsep}{os.environ['PATH']}",
            BITCOIN_RPC_COOKIE=str(self.cookie),
            DUMPLINGS_S3_DESTINATION="s3://dummy/dumplings",
            DUMPLINGS_INTERVAL="0",
            S3_PROFILE="archive",
            CREDENTIAL_TEST_LOG=str(self.log),
        )

    def run_service(self, **environment: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", str(ENTRYPOINT)],
            env={**self.environment, **environment},
            cwd=self.root,
            capture_output=True,
            text=True,
            check=False,
            timeout=5,
        )

    def test_readonly_file_selects_profile_and_preserves_contents(self) -> None:
        credentials = self.root / "mounted credentials"
        content = "[archive]\naws_access_key_id=dummy\naws_secret_access_key=dummy\n"
        credentials.write_text(content)
        credentials.chmod(0o400)

        result = self.run_service(
            S3_CREDENTIALS_FILE=str(credentials),
            S3_ACCESS_KEY_ID="unused-access",
        )

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("sync failed", result.stderr)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertEqual(
            calls[0]["args"][:4],
            ["--credentials-file", str(credentials), "--profile", "archive"],
        )
        self.assertEqual(credentials.read_text(), content)
        self.assertNotIn(content, result.stdout + result.stderr)

    def test_environment_pairs_do_not_select_a_file_profile(self) -> None:
        for prefix in ("S3", "AWS"):
            with self.subTest(prefix=prefix):
                result = self.run_service(
                    **{
                        f"{prefix}_ACCESS_KEY_ID": "dummy-access",
                        f"{prefix}_SECRET_ACCESS_KEY": "dummy-secret",
                    }
                )
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("sync failed", result.stderr)
                call = json.loads(self.log.read_text().splitlines()[-1])
                self.assertNotIn("--profile", call["args"])
                self.assertNotIn("--credentials-file", call["args"])
                self.assertEqual(call["access"], "dummy-access")
                self.assertEqual(call["secret"], "dummy-secret")

    def test_profile_without_environment_keys_keeps_default_file_lookup(self) -> None:
        result = self.run_service()

        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("sync failed", result.stderr)
        call = json.loads(self.log.read_text().splitlines()[0])
        self.assertEqual(call["args"][:2], ["--profile", "archive"])
        self.assertNotIn("--credentials-file", call["args"])

    def test_missing_credentials_file_is_rejected(self) -> None:
        result = self.run_service(S3_CREDENTIALS_FILE=str(self.root / "missing"))

        self.assertEqual(result.returncode, 2)
        self.assertIn("readable regular file", result.stderr)
        self.assertFalse(self.log.exists())

    def test_directory_is_rejected_as_credentials_file(self) -> None:
        result = self.run_service(S3_CREDENTIALS_FILE=str(self.root))

        self.assertEqual(result.returncode, 2)
        self.assertIn("readable regular file", result.stderr)
        self.assertFalse(self.log.exists())

    def test_one_shot_reports_scanner_failure_without_publishing_snapshot(self) -> None:
        result = self.run_service()
        self.assertEqual(result.returncode, 1)
        self.assertIn("sync failed; nothing published", result.stderr)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertFalse(any("latest.json" in argument for call in calls for argument in call["args"]))
