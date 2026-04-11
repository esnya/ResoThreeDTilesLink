from __future__ import annotations

import sys
import unittest
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cleanup_sessions
import run_tokyo_tower_stream


class ListenerProbeTests(unittest.TestCase):
    def test_probe_listener_from_host_returns_first_success(self) -> None:
        with mock.patch.object(
            run_tokyo_tower_stream,
            "run_process",
            return_value=mock.Mock(returncode=0, stdout="OK\n", stderr=""),
        ) as run_process:
            result = run_tokyo_tower_stream.probe_listener_from_host(30811, hosts=["localhost", "127.0.0.1"], timeout_s=1.5)

        self.assertTrue(result["ok"])
        self.assertEqual(result["host"], "localhost")
        self.assertEqual(result["port"], 30811)
        self.assertEqual(result["attempts"], [])
        run_process.assert_called_once()
        args = run_process.call_args.args[0]
        self.assertEqual(args[:3], ["pwsh.exe", "-NoLogo", "-NoProfile"])
        self.assertIn("localhost", args[-1])
        self.assertIn("30811", args[-1])

    def test_probe_listener_from_host_collects_attempt_failures(self) -> None:
        with mock.patch.object(
            run_tokyo_tower_stream,
            "run_process",
            side_effect=[
                mock.Mock(returncode=1, stdout="", stderr="refused localhost"),
                mock.Mock(returncode=1, stdout="", stderr="refused ipv4"),
            ],
        ):
            result = run_tokyo_tower_stream.probe_listener_from_host(30811, hosts=["localhost", "127.0.0.1"], timeout_s=2.0)

        self.assertFalse(result["ok"])
        self.assertEqual(result["port"], 30811)
        self.assertEqual(
            result["attempts"],
            [
                {"host": "localhost", "port": 30811, "error": "refused localhost"},
                {"host": "127.0.0.1", "port": 30811, "error": "refused ipv4"},
            ],
        )

    def test_ensure_listener_ready_raises_with_probe_details(self) -> None:
        probe = {
            "ok": False,
            "port": 30811,
            "attempts": [
                {"host": "localhost", "port": 30811, "error": "refused localhost"},
                {"host": "127.0.0.1", "port": 30811, "error": "refused ipv4"},
            ],
        }

        with mock.patch.object(run_tokyo_tower_stream, "probe_listener_from_host", return_value=probe):
            with self.assertRaises(RuntimeError) as ctx:
                run_tokyo_tower_stream.ensure_listener_ready(30811, hosts=["localhost", "127.0.0.1"])

        self.assertIn("listener preflight failed from the Windows host process", str(ctx.exception))
        self.assertIn("host=localhost", str(ctx.exception))
        self.assertIn("host=127.0.0.1", str(ctx.exception))

    def test_probe_protocol_from_host_reports_success_response(self) -> None:
        with mock.patch.object(
            run_tokyo_tower_stream,
            "invoke_resonite",
            return_value='Running send-json against ws://localhost:30811/\n{"$type":"sessionData","success":true}',
        ) as invoke_resonite:
            result = run_tokyo_tower_stream.probe_protocol_from_host(Path.cwd(), "localhost", 30811, timeout_s=9)

        self.assertTrue(result["ok"])
        self.assertEqual(result["host"], "localhost")
        self.assertEqual(result["port"], 30811)
        self.assertEqual(result["response_json"], {"$type": "sessionData", "success": True})
        invoke_resonite.assert_called_once()

    def test_ensure_protocol_ready_raises_with_error_details(self) -> None:
        probe = {
            "ok": False,
            "host": "localhost",
            "port": 30811,
            "error": "Timed out waiting for response",
        }

        with mock.patch.object(run_tokyo_tower_stream, "probe_protocol_from_host", return_value=probe):
            with self.assertRaises(RuntimeError) as ctx:
                run_tokyo_tower_stream.ensure_protocol_ready(Path.cwd(), "localhost", 30811)

        self.assertIn("protocol preflight failed", str(ctx.exception))
        self.assertIn("Timed out waiting for response", str(ctx.exception))

    def test_probe_protocol_from_host_rejects_success_false_response(self) -> None:
        with mock.patch.object(
            run_tokyo_tower_stream,
            "invoke_resonite",
            return_value='{"$type":"sessionData","success":false,"errorInfo":"bad"}',
        ):
            result = run_tokyo_tower_stream.probe_protocol_from_host(Path.cwd(), "localhost", 30811)

        self.assertFalse(result["ok"])
        self.assertIn("success=False", result["error"])

    def test_probe_protocol_from_host_rejects_non_json_output(self) -> None:
        with mock.patch.object(
            run_tokyo_tower_stream,
            "invoke_resonite",
            return_value="Running send-json against ws://localhost:30811/",
        ):
            result = run_tokyo_tower_stream.probe_protocol_from_host(Path.cwd(), "localhost", 30811)

        self.assertFalse(result["ok"])
        self.assertIn("no JSON object could be parsed", result["error"])

    def test_extract_json_object_returns_last_json_line(self) -> None:
        parsed = run_tokyo_tower_stream.extract_json_object('header\n{"a":1}\n{"b":2}')
        self.assertEqual(parsed, {"b": 2})

    def test_resolve_candidate_hosts_prefers_explicit_host(self) -> None:
        self.assertEqual(
            run_tokyo_tower_stream.resolve_candidate_hosts("192.0.2.10", {"address": "198.51.100.10"}),
            ["192.0.2.10"],
        )

    def test_resolve_candidate_hosts_uses_discovered_address_as_fallback(self) -> None:
        self.assertEqual(
            run_tokyo_tower_stream.resolve_candidate_hosts(None, {"address": "198.51.100.10"}),
            ["localhost", "198.51.100.10"],
        )

    def test_unique_hosts_preserves_order_and_deduplicates(self) -> None:
        self.assertEqual(
            run_tokyo_tower_stream.unique_hosts(["localhost", "127.0.0.1", "localhost", "", "127.0.0.1"]),
            ["localhost", "127.0.0.1"],
        )


class CleanupSessionTests(unittest.TestCase):
    def test_cleanup_main_uses_discovered_host_fallback_when_host_is_omitted(self) -> None:
        args = mock.Mock(repo_path=Path.cwd(), host=None, port=None)
        discovered = {"port": 30811, "address": "198.51.100.10"}
        listener_probe = {"host": "198.51.100.10"}

        with mock.patch.object(cleanup_sessions, "parse_args", return_value=args), \
             mock.patch.object(run_tokyo_tower_stream, "discover_session", return_value=discovered), \
             mock.patch.object(run_tokyo_tower_stream, "resolve_candidate_hosts", return_value=["localhost", "198.51.100.10"]) as resolve_hosts, \
             mock.patch.object(run_tokyo_tower_stream, "ensure_listener_ready", return_value=listener_probe), \
             mock.patch.object(run_tokyo_tower_stream, "ensure_protocol_ready", return_value={"ok": True}), \
             mock.patch.object(run_tokyo_tower_stream, "list_active_live_cli_processes", return_value=[]), \
             mock.patch.object(run_tokyo_tower_stream, "cleanup_sessions") as cleanup:
            exit_code = cleanup_sessions.main()

        self.assertEqual(exit_code, 0)
        resolve_hosts.assert_called_once_with(None, discovered)
        cleanup.assert_called_once_with(Path.cwd().resolve(), "198.51.100.10", 30811)


if __name__ == "__main__":
    unittest.main()
