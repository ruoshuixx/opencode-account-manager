#!/usr/bin/env python3
"""Focused native input-contract checks for Test-Linux.sh. No external services are used."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import signal
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
from pathlib import Path


ARCHIVE_ROOT = "opencode-account-manager-linux-x64"
ARCHIVE_MODES = {
    ARCHIVE_ROOT: ("dir", 0o755),
    f"{ARCHIVE_ROOT}/OpenCodeAccountManager": ("file", 0o755),
    f"{ARCHIVE_ROOT}/start.sh": ("file", 0o755),
    f"{ARCHIVE_ROOT}/install-desktop.sh": ("file", 0o755),
    f"{ARCHIVE_ROOT}/uninstall-desktop.sh": ("file", 0o755),
    f"{ARCHIVE_ROOT}/opencode-account-manager.desktop.in": ("file", 0o644),
    f"{ARCHIVE_ROOT}/opencode-account-manager.png": ("file", 0o644),
    f"{ARCHIVE_ROOT}/README.txt": ("file", 0o644),
}
NATIVE_SCENARIOS = (
    "unix-secret-store-cross-process",
    "single-instance-race-activation-abrupt-restart",
    "curl-local-fixture-stdin",
    "curl-output-bound",
    "curl-owned-process-cancellation",
    "updater-probe-mismatch-and-nonexecutable",
    "updater-target-concurrent-modification",
    "updater-unconfirmed-child-compensation",
)
HARNESS_UPDATE_SCENARIOS = (
    "probe-version-mismatch",
    "nonexecutable-candidate",
    "target-concurrent-modification",
    "unconfirmed-child-compensation",
)
SHELL_UPDATE_SCENARIOS = (
    "successful-real-bundle-handover",
    "production-managed-entry-timeout",
    "production-parent-exit-timeout",
    "readonly-install-refusal",
)
HARNESS_UPDATE_FIELDS = {
    "probe-version-mismatch": "boundary=production-candidate-validator candidate=actual-second-bundle",
    "nonexecutable-candidate": "boundary=production-probe-runner-start-denied child_started=no",
    "target-concurrent-modification": "boundary=production-swap-mechanism-only orchestration=not-tested",
    "unconfirmed-child-compensation": "boundary=production-coordinator-mechanism-only launch=controlled-exception",
}
SHELL_UPDATE_FIELDS = {
    "successful-real-bundle-handover": "parent_wait=production-ipc child_cleanup=production-mechanism-only ui_opened=no",
    "production-managed-entry-timeout": "orchestration=production child_control=harness-pre-entry-hold ui_opened=no",
    "production-parent-exit-timeout": "old_backup=preserved-for-manual-recovery ui_opened=no",
    "readonly-install-refusal": "",
}
VERSION_RE = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+\Z")
HASH_RE = re.compile(r"[0-9a-f]{64}\Z")
NATIVE_LINE_RE = re.compile(
    r"NATIVE_SCENARIO name=([a-z0-9-]+) status=(PASS|FAIL)(?: type=([A-Za-z][A-Za-z0-9_]*))?\Z"
)
UPDATE_LINE_RE = re.compile(
    r"UPDATE_SCENARIO name=([a-z0-9-]+) status=(PASS|FAIL)"
    r"(?: [a-z][a-z0-9_]*=[A-Za-z0-9_.:-]+)*\Z"
)
FAILURE_LINE_RE = re.compile(
    r"(?:NATIVE_SCENARIO name=[a-z0-9-]+ status=FAIL type=[A-Za-z][A-Za-z0-9_]*|"
    r"NATIVE_OWNED_CHILD_CLEANUP=UNCONFIRMED|"
    r"UPDATE_SCENARIO name=[a-z0-9-]+ status=FAIL(?: [a-z][a-z0-9_]*=[A-Za-z0-9_.:-]+)*|"
    r"NATIVE_SCENARIOS executed=[0-9]+ passed=[0-9]+ failed=[0-9]+ skipped=[0-9]+|"
    r"HARNESS_FAILURE type=[A-Za-z][A-Za-z0-9_]*)\Z"
)


class ContractError(Exception):
    pass


def _object_without_duplicate_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ContractError("duplicate JSON property")
        result[key] = value
    return result


def _update_pass_line(name: str, fields: str) -> str:
    suffix = f" {fields}" if fields else ""
    return f"UPDATE_SCENARIO name={name} status=PASS{suffix}"


def _one_json_line(raw: bytes) -> dict[str, object]:
    if not raw.endswith(b"\n") or raw.endswith(b"\r\n") or raw.count(b"\n") != 1 or b"\r" in raw:
        raise ContractError("expected exactly one LF-terminated JSON line")
    try:
        value = json.loads(raw[:-1].decode("utf-8"), object_pairs_hook=_object_without_duplicate_keys)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ContractError("invalid JSON line") from error
    if not isinstance(value, dict):
        raise ContractError("JSON line must be an object")
    return value


def verify_probe_bytes(raw: bytes, expected_version: str) -> None:
    if not VERSION_RE.fullmatch(expected_version):
        raise ContractError("expected version must have three numeric components")
    document = _one_json_line(raw)
    if set(document) != {"product", "version", "platform", "protocol"}:
        raise ContractError("probe JSON properties do not match the production contract")
    if document != {
        "product": "OpenCodeAccountManager",
        "version": expected_version,
        "platform": "linux-x64",
        "protocol": 1,
    }:
        raise ContractError("probe JSON does not match the expected product/version/platform/protocol")


def verify_info_bytes(raw: bytes, expected_version: str) -> None:
    document = _one_json_line(raw)
    expected = {
        "entryAssembly": "AccountManager.Linux.IntegrationHarness",
        "entryVersion": expected_version,
        "appAssembly": "OpenCodeAccountManager",
        "appVersion": expected_version,
        "framework": ".NET 8.0.31",
        "processBasename": "OpenCodeAccountManager",
        "singleFile": True,
    }
    if document != expected:
        raise ContractError("harness identity/version/runtime info does not match the expected bundle")


def canonical_archive_name(name: str, is_directory: bool) -> str:
    if name.startswith("./"):
        name = name[2:]
        if name.startswith("./"):
            raise ContractError("archive member has more than one leading ./ prefix")
    if name.startswith("/") or "\\" in name or not name:
        raise ContractError("archive member has an unsafe path")
    if is_directory and name.endswith("/"):
        name = name[:-1]
    parts = name.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise ContractError("archive member has an unsafe path component")
    return name


def validate_archive_members(members: list[tarfile.TarInfo]) -> None:
    actual: dict[str, tarfile.TarInfo] = {}
    for member in members:
        canonical = canonical_archive_name(member.name, member.isdir())
        if canonical in actual:
            raise ContractError("archive contains a duplicate canonical path")
        expected = ARCHIVE_MODES.get(canonical)
        if expected is None:
            raise ContractError("archive contains an unexpected path")
        expected_type, expected_mode = expected
        actual_type = "dir" if member.isdir() else "file" if member.isfile() else "other"
        if actual_type != expected_type or member.mode & 0o7777 != expected_mode:
            raise ContractError("archive member type or mode does not match the portable contract")
        actual[canonical] = member
    if set(actual) != set(ARCHIVE_MODES):
        raise ContractError("archive is missing a required portable member")


def validate_archive(archive_path: Path) -> None:
    try:
        with tarfile.open(archive_path, mode="r:gz") as archive:
            validate_archive_members(archive.getmembers())
    except ContractError:
        raise
    except (OSError, tarfile.TarError) as error:
        raise ContractError("archive could not be read") from error


def verify_sha256(path: Path, expected_hash: str) -> None:
    if not HASH_RE.fullmatch(expected_hash):
        raise ContractError("expected SHA-256 must be lowercase hexadecimal")
    if path.is_symlink() or not path.is_file():
        raise ContractError("hash input must be a regular non-link file")
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != expected_hash:
        raise ContractError("file hash differs from the bound candidate hash")


def _parse_summary(native_text: str, shell_updates_text: str | None = None) -> list[str]:
    native_scenarios: list[str] = []
    harness_updates: list[str] = []
    shell_updates: list[str] = []
    summaries: list[tuple[int, int, int, int]] = []
    ui_lines = 0

    for line in native_text.splitlines():
        match = NATIVE_LINE_RE.fullmatch(line)
        if match:
            name, status, _exception_type = match.groups()
            if status != "PASS":
                raise ContractError("native scenario did not pass")
            native_scenarios.append(name)
            continue
        match = UPDATE_LINE_RE.fullmatch(line)
        if match:
            name, status = match.groups()
            if status != "PASS":
                raise ContractError("harness updater scenario did not pass")
            fields = HARNESS_UPDATE_FIELDS.get(name)
            if fields is None or line != _update_pass_line(name, fields):
                raise ContractError("harness updater boundary label is unexpected")
            harness_updates.append(name)
            continue
        if line.startswith("NATIVE_SCENARIOS "):
            summary = re.fullmatch(
                r"NATIVE_SCENARIOS executed=([0-9]+) passed=([0-9]+) failed=([0-9]+) skipped=([0-9]+)", line
            )
            if summary is None:
                raise ContractError("native scenario summary is malformed")
            summaries.append(tuple(int(value) for value in summary.groups()))
        elif line.startswith("NATIVE_SCENARIO ") or line.startswith("UPDATE_SCENARIO "):
            raise ContractError("scenario identity line is malformed")
        elif line.startswith("NATIVE_UI="):
            if line != "NATIVE_UI=NOT_TESTED (no Avalonia window, tray host, or desktop session was opened)":
                raise ContractError("native UI boundary marker is malformed")
            ui_lines += 1
        elif line == "NATIVE_OWNED_CHILD_CLEANUP=UNCONFIRMED" or line.startswith("HARNESS_FAILURE "):
            raise ContractError("native harness reported an unconfirmed child or top-level failure")

    if len(native_scenarios) != len(NATIVE_SCENARIOS) or set(native_scenarios) != set(NATIVE_SCENARIOS):
        raise ContractError("native scenario identities are missing, duplicated, or unexpected")
    if len(summaries) != 1 or summaries[0] != (len(NATIVE_SCENARIOS), len(NATIVE_SCENARIOS), 0, 0):
        raise ContractError("native scenario summary counts do not match the required identities")
    if ui_lines != 1:
        raise ContractError("native UI boundary marker is missing or duplicated")
    if len(harness_updates) != len(HARNESS_UPDATE_SCENARIOS) or set(harness_updates) != set(HARNESS_UPDATE_SCENARIOS):
        raise ContractError("harness updater scenario identities are missing, duplicated, or unexpected")

    if shell_updates_text is not None:
        for line in shell_updates_text.splitlines():
            match = UPDATE_LINE_RE.fullmatch(line)
            if match is None:
                raise ContractError("shell updater summary contains a malformed or unsanitized line")
            name, status = match.groups()
            if status != "PASS":
                raise ContractError("shell updater scenario did not pass")
            fields = SHELL_UPDATE_FIELDS.get(name)
            if fields is None or line != _update_pass_line(name, fields):
                raise ContractError("shell updater label is unexpected")
            shell_updates.append(name)
        if len(shell_updates) != len(SHELL_UPDATE_SCENARIOS) or set(shell_updates) != set(SHELL_UPDATE_SCENARIOS):
            raise ContractError("shell updater scenario identities are missing, duplicated, or unexpected")

    return [
        *(f"NATIVE_SCENARIO name={name} status=PASS" for name in NATIVE_SCENARIOS),
        f"NATIVE_SCENARIOS executed={len(NATIVE_SCENARIOS)} passed={len(NATIVE_SCENARIOS)} failed=0 skipped=0",
        "NATIVE_UI=NOT_TESTED (no Avalonia window, tray host, or desktop session was opened)",
        *(_update_pass_line(name, HARNESS_UPDATE_FIELDS[name]) for name in HARNESS_UPDATE_SCENARIOS),
        *(_update_pass_line(name, SHELL_UPDATE_FIELDS[name]) for name in shell_updates),
    ]


def _stat_start_ticks(stat_text: str) -> int:
    closing = stat_text.rfind(")")
    if closing < 0:
        raise ContractError("process stat record is malformed")
    fields = stat_text[closing + 2 :].split()
    if len(fields) <= 19:
        raise ContractError("process stat record is truncated")
    try:
        return int(fields[19])
    except ValueError as error:
        raise ContractError("process start-time field is malformed") from error


def _parse_process_marker(text: str) -> tuple[str, int, int, int]:
    fields = text.strip().split(":")
    if len(fields) != 4 or fields[0] not in {"managed-child-entered", "held-before-entry", "startup-rejected", "harness-child"}:
        raise ContractError("owned-child identity marker is malformed")
    try:
        pid, start_ticks, monotonic_ms = (int(value) for value in fields[1:])
    except ValueError as error:
        raise ContractError("owned-child identity values are malformed") from error
    if pid <= 0 or start_ticks <= 0 or monotonic_ms < 0:
        raise ContractError("owned-child identity values are out of range")
    return fields[0], pid, start_ticks, monotonic_ms


def _proc_start_ticks(pid: int, proc_root: Path = Path("/proc")) -> int | None:
    identity = _proc_state_start_ticks(pid, proc_root)
    return None if identity is None else identity[1]


def _proc_state_start_ticks(pid: int, proc_root: Path = Path("/proc")) -> tuple[str, int] | None:
    try:
        root_status = proc_root.stat()
    except OSError as error:
        raise ContractError("process identity source could not be inspected") from error
    if not stat.S_ISDIR(root_status.st_mode):
        raise ContractError("process identity source is not a directory")

    process_directory = proc_root / str(pid)
    try:
        stat_text = (process_directory / "stat").read_text(encoding="ascii")
    except FileNotFoundError as error:
        try:
            process_directory.stat()
        except FileNotFoundError:
            return None
        except OSError as identity_error:
            raise ContractError("process directory identity could not be inspected") from identity_error
        raise ContractError("process stat disappeared while its PID directory remained") from error
    except ProcessLookupError:
        return None
    except OSError as error:
        raise ContractError("process stat could not be read") from error
    closing = stat_text.rfind(")")
    if closing < 0:
        raise ContractError("process stat record is malformed")
    fields = stat_text[closing + 2 :].split()
    if len(fields) <= 19:
        raise ContractError("process stat record is truncated")
    try:
        state = fields[0]
        start_ticks = int(fields[19])
    except ValueError as error:
        raise ContractError("process start-time field is malformed") from error
    if state not in {"R", "S", "D", "Z", "T", "t", "X", "x", "K", "W", "P", "I"}:
        raise ContractError("process state field is malformed")
    return state, start_ticks


def _process_matches(pid: int, expected_start: int, proc_root: Path = Path("/proc")) -> bool | None:
    try:
        actual = _proc_state_start_ticks(pid, proc_root)
    except (OSError, ContractError):
        return None
    if actual is None or actual[1] != expected_start or actual[0] in {"Z", "X"}:
        return False
    return True


def _verify_parent_timeout(start_text: str, result_text: str) -> int:
    start_kind, start_pid, start_ticks, start_ms = _parse_process_marker(start_text)
    result_kind, result_pid, result_ticks, result_ms = _parse_process_marker(result_text)
    if start_kind != "managed-child-entered" or result_kind != "startup-rejected":
        raise ContractError("parent-timeout markers do not identify the production child phases")
    if (start_pid, start_ticks) != (result_pid, result_ticks):
        raise ContractError("parent-timeout start/result markers identify different children")
    elapsed = result_ms - start_ms
    if elapsed < 28_000 or elapsed > 45_000:
        raise ContractError("production parent-exit wait did not span its bounded timeout")
    return elapsed


def _verify_parent_hold_timeout(
    start_text: str,
    result_text: str,
    hold_start_text: str,
    hold_end_text: str,
) -> tuple[int, int]:
    child_elapsed = _verify_parent_timeout(start_text, result_text)
    try:
        hold_start = int(hold_start_text.strip())
        hold_end = int(hold_end_text.strip())
    except ValueError as error:
        raise ContractError("parent hold monotonic markers are malformed") from error
    hold_elapsed = hold_end - hold_start
    if hold_start < 0 or hold_elapsed < 33_000 or hold_elapsed > 45_000:
        raise ContractError("the updater parent did not remain alive for the controlled 35-second hold")
    _start_kind, _pid, _ticks, child_start_ms = _parse_process_marker(start_text)
    _result_kind, _pid, _ticks, child_result_ms = _parse_process_marker(result_text)
    if child_start_ms > hold_start or child_result_ms < hold_start or child_result_ms > hold_end:
        raise ContractError("the child timeout did not occur while the exact updater parent was held alive")
    return child_elapsed, hold_elapsed


def _verify_readiness_result(text: str) -> int:
    match = re.fullmatch(r"readiness-timeout-restored-original:([0-9]+)\n", text)
    if match is None:
        raise ContractError("readiness timeout result marker is malformed")
    elapsed = int(match.group(1))
    if elapsed < 18_000 or elapsed > 35_000:
        raise ContractError("readiness failure did not span the production bounded-entry deadline")
    return elapsed


def _archive_fixture(names: list[tuple[str, str, int]]) -> bytes:
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w:gz") as archive:
        for name, kind, mode in names:
            member = tarfile.TarInfo(name)
            member.mode = mode
            if kind == "dir":
                member.type = tarfile.DIRTYPE
                archive.addfile(member)
            elif kind == "link":
                member.type = tarfile.SYMTYPE
                member.linkname = "target"
                archive.addfile(member)
            else:
                body = b"synthetic"
                member.size = len(body)
                archive.addfile(member, io.BytesIO(body))
    return stream.getvalue()


def self_test() -> None:
    expected_members = [
        (f"./{name}/" if kind == "dir" else f"./{name}", kind, mode)
        for name, (kind, mode) in ARCHIVE_MODES.items()
    ]
    def check_archive(entries: list[tuple[str, str, int]]) -> None:
        payload = _archive_fixture(entries)
        with io.BytesIO(payload) as stream, tarfile.open(fileobj=stream, mode="r:gz") as archive:
            validate_archive_members(archive.getmembers())

    check_archive(expected_members)

    for bad_name in ("././" + ARCHIVE_ROOT + "/OpenCodeAccountManager", "../outside", "/absolute"):
        entries = expected_members + [(bad_name, "file", 0o644)]
        try:
            check_archive(entries)
        except ContractError:
            pass
        else:
            raise AssertionError("unsafe or unexpected archive path was accepted")
    duplicate = expected_members + [(f"{ARCHIVE_ROOT}/README.txt", "file", 0o644)]
    try:
        check_archive(duplicate)
    except ContractError:
        pass
    else:
        raise AssertionError("duplicate canonical archive path was accepted")
    linked = [(name, kind, mode) for name, kind, mode in expected_members]
    linked.append((f"{ARCHIVE_ROOT}/unexpected-link", "link", 0o777))
    try:
        check_archive(linked)
    except ContractError:
        pass
    else:
        raise AssertionError("archive link was accepted")
    known_link = [
        (name, "link" if name == f"./{ARCHIVE_ROOT}/OpenCodeAccountManager" else kind, mode)
        for name, kind, mode in expected_members
    ]
    try:
        check_archive(known_link)
    except ContractError:
        pass
    else:
        raise AssertionError("a required archive file replaced by a link was accepted")

    valid_probe = b'{"product":"OpenCodeAccountManager","version":"1.0.4","platform":"linux-x64","protocol":1}\n'
    verify_probe_bytes(valid_probe, "1.0.4")
    for invalid_probe in (valid_probe + valid_probe, valid_probe.replace(b"1.0.4", b"1.0.5"), valid_probe[:-1]):
        try:
            verify_probe_bytes(invalid_probe, "1.0.4")
        except ContractError:
            pass
        else:
            raise AssertionError("mismatched or multi-line probe output was accepted")

    info = {
        "entryAssembly": "AccountManager.Linux.IntegrationHarness",
        "entryVersion": "91.4.1",
        "appAssembly": "OpenCodeAccountManager",
        "appVersion": "91.4.1",
        "framework": ".NET 8.0.31",
        "processBasename": "OpenCodeAccountManager",
        "singleFile": True,
    }
    verify_info_bytes(json.dumps(info, separators=(",", ":")).encode() + b"\n", "91.4.1")
    try:
        verify_info_bytes(json.dumps(info).encode() + b"\nextra\n", "91.4.1")
    except ContractError:
        pass
    else:
        raise AssertionError("multi-line harness info was accepted")

    native = [f"NATIVE_SCENARIO name={name} status=PASS" for name in NATIVE_SCENARIOS]
    harness_update_lines = [
        "UPDATE_SCENARIO name=probe-version-mismatch status=PASS boundary=production-candidate-validator candidate=actual-second-bundle",
        "UPDATE_SCENARIO name=nonexecutable-candidate status=PASS boundary=production-probe-runner-start-denied child_started=no",
        "UPDATE_SCENARIO name=target-concurrent-modification status=PASS boundary=production-swap-mechanism-only orchestration=not-tested",
        "UPDATE_SCENARIO name=unconfirmed-child-compensation status=PASS boundary=production-coordinator-mechanism-only launch=controlled-exception",
    ]
    native += harness_update_lines
    native += [
        f"NATIVE_SCENARIOS executed={len(NATIVE_SCENARIOS)} passed={len(NATIVE_SCENARIOS)} failed=0 skipped=0",
        "NATIVE_UI=NOT_TESTED (no Avalonia window, tray host, or desktop session was opened)",
    ]
    shell_update_lines = [
        "UPDATE_SCENARIO name=successful-real-bundle-handover status=PASS parent_wait=production-ipc child_cleanup=production-mechanism-only ui_opened=no",
        "UPDATE_SCENARIO name=production-managed-entry-timeout status=PASS orchestration=production child_control=harness-pre-entry-hold ui_opened=no",
        "UPDATE_SCENARIO name=production-parent-exit-timeout status=PASS old_backup=preserved-for-manual-recovery ui_opened=no",
        "UPDATE_SCENARIO name=readonly-install-refusal status=PASS",
    ]
    rendered = _parse_summary("\n".join(native), "\n".join(shell_update_lines))
    if not all(line in rendered for line in harness_update_lines + shell_update_lines):
        raise AssertionError("rendered scenario summary dropped an approved boundary/runner label")
    if any(line.startswith("UPDATE_SCENARIO name=target-concurrent-modification status=PASS")
           and "boundary=" not in line for line in rendered):
        raise AssertionError("rendered summary reduced a mechanism-only case to generic PASS")
    shell_updates = shell_update_lines
    for invalid_native, invalid_updates in (
        ("\n".join(native[:-5] + native[-3:]), "\n".join(shell_updates)),
        ("\n".join(native + [native[0]]), "\n".join(shell_updates)),
        ("\n".join(native), "\n".join(shell_updates[:-1])),
    ):
        try:
            _parse_summary(invalid_native, invalid_updates)
        except ContractError:
            pass
        else:
            raise AssertionError("missing or duplicate scenario identity was accepted")
    try:
        _parse_summary("\n".join(native + ["NATIVE_SCENARIO name=unexpected status=PASS"]), "\n".join(shell_updates))
    except ContractError:
        pass
    else:
        raise AssertionError("an unexpected native scenario identity was accepted")
    invalid_label_native = "\n".join(native).replace(
        harness_update_lines[2], "UPDATE_SCENARIO name=target-concurrent-modification status=PASS"
    )
    try:
        _parse_summary(invalid_label_native, "\n".join(shell_updates))
    except ContractError:
        pass
    else:
        raise AssertionError("a mechanism-only scenario missing its approved boundary fields was accepted")

    safe_failure_log = (
        "noise containing synthetic-harness-token-not-a-credential\n"
        "NATIVE_SCENARIO name=single-instance-race-activation-abrupt-restart status=FAIL type=TimeoutException\n"
        "HARNESS_FAILURE type=InvalidOperationException\n"
    )
    with tempfile.TemporaryDirectory(prefix="ocam-linux-log-selftest-") as temporary:
        failure_path = Path(temporary) / "failure.log"
        failure_path.write_text(safe_failure_log, encoding="utf-8")
        sanitized = sanitized_failure_lines(failure_path)
        if sanitized != [
            "NATIVE_SCENARIO name=single-instance-race-activation-abrupt-restart status=FAIL type=TimeoutException",
            "HARNESS_FAILURE type=InvalidOperationException",
        ] or any("synthetic-harness-token" in line for line in sanitized):
            raise AssertionError("sanitized failure-line filtering leaked noise or omitted safe diagnostics")

    safe = _stat_start_ticks("42 (fixture (with ) chars)) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 987654 20")
    if safe != 987654:
        raise AssertionError("/proc stat start-time parsing did not handle parenthesized comm fields")
    marker = _parse_process_marker("managed-child-entered:42:987654:1000")
    if marker != ("managed-child-entered", 42, 987654, 1000):
        raise AssertionError("owned-child marker parsing failed")

    proc_root = Path(tempfile.mkdtemp(prefix="ocam-proc-contract-selftest-"))
    try:
        def write_proc_stat(pid: int, state: str, start_ticks: int) -> None:
            process_directory = proc_root / str(pid)
            process_directory.mkdir(parents=True, exist_ok=True)
            fields = [state, *(["0"] * 18), str(start_ticks)]
            (process_directory / "stat").write_text(
                f"{pid} (synthetic-child) {' '.join(fields)}\n", encoding="ascii"
            )

        def assert_identity_unknown(pid: int, start_ticks: int) -> None:
            marker_path = proc_root / f"unknown-child-{pid}.marker"
            marker_path.write_text(f"harness-child:{pid}:{start_ticks}:10\n", encoding="ascii")
            for operation in (
                lambda: _wait_owned_exit(marker_path, 0.01, proc_root),
                lambda: _cleanup_owned_process(marker_path, proc_root),
            ):
                try:
                    operation()
                except ContractError:
                    pass
                else:
                    raise AssertionError("unknown child identity was treated as confirmed exit")

        def assert_identity_gone(pid: int, start_ticks: int) -> None:
            marker_path = proc_root / f"gone-child-{pid}.marker"
            marker_path.write_text(f"harness-child:{pid}:{start_ticks}:10\n", encoding="ascii")
            _wait_owned_exit(marker_path, 0.01, proc_root)
            _cleanup_owned_process(marker_path, proc_root)

        write_proc_stat(10_000, "S", 54)
        if _process_matches(10_000, 54, proc_root) is not True:
            raise AssertionError("a readable matching running process was not distinguished")
        if _process_matches(10_001, 55, proc_root) is not False:
            raise AssertionError("a genuinely missing synthetic process was not confirmed absent")
        assert_identity_gone(10_001, 55)
        write_proc_stat(10_002, "S", 56)
        if _process_matches(10_002, 55, proc_root) is not False:
            raise AssertionError("a verified reused PID was not distinguished from the owned process")
        assert_identity_gone(10_002, 55)
        write_proc_stat(10_003, "Z", 57)
        if _process_matches(10_003, 57, proc_root) is not False:
            raise AssertionError("a verified zombie process was not confirmed exited")
        assert_identity_gone(10_003, 57)
        write_proc_stat(10_004, "X", 58)
        if _process_matches(10_004, 58, proc_root) is not False:
            raise AssertionError("a verified exited process state was not confirmed")
        assert_identity_gone(10_004, 58)

        (proc_root / str(10_005)).mkdir()
        assert_identity_unknown(10_005, 59)
        (proc_root / str(10_006)).mkdir()
        (proc_root / str(10_006) / "stat").mkdir()
        assert_identity_unknown(10_006, 60)
        write_proc_stat(10_007, "S", 61)
        (proc_root / str(10_007) / "stat").write_text("malformed stat record\n", encoding="ascii")
        assert_identity_unknown(10_007, 61)
    finally:
        shutil.rmtree(proc_root)

    if _verify_parent_timeout(
        "managed-child-entered:42:987654:1000", "startup-rejected:42:987654:31000"
    ) != 30_000:
        raise AssertionError("parent-exit timeout duration validation failed")
    try:
        _verify_parent_timeout(
            "managed-child-entered:42:987654:1000", "startup-rejected:42:987654:2000"
        )
    except ContractError:
        pass
    else:
        raise AssertionError("an immediate parent-exit refusal was accepted as a timeout")
    if _verify_parent_hold_timeout(
        "managed-child-entered:42:987654:1000",
        "startup-rejected:42:987654:31000",
        "2000",
        "37000",
    ) != (30_000, 35_000):
        raise AssertionError("parent-alive duration and child timeout overlap were not verified")
    for invalid in (
        "readiness-timeout-restored-original:1000\n",
        "readiness-timeout-restored-original:20000\nextra\n",
        "readiness-timeout-restored-original:20000",
    ):
        try:
            _verify_readiness_result(invalid)
        except ContractError:
            pass
        else:
            raise AssertionError("malformed or early readiness timeout result was accepted")
    if _verify_readiness_result("readiness-timeout-restored-original:20000\n") != 20_000:
        raise AssertionError("a bounded production readiness timeout marker was rejected")

    with tempfile.TemporaryDirectory(prefix="ocam-linux-contract-selftest-") as temporary:
        root = Path(temporary)
        archive_path = root / "synthetic.tar.gz"
        archive_path.write_bytes(_archive_fixture(expected_members))
        validate_archive(archive_path)
        candidate = root / "candidate"
        candidate.write_bytes(b"synthetic ELF bytes")
        if sys.platform.startswith("linux"):
            candidate.chmod(0o644)
        expected_hash = hashlib.sha256(candidate.read_bytes()).hexdigest()
        verify_sha256(candidate, expected_hash)
        if sys.platform.startswith("linux") and candidate.stat().st_mode & 0o777 != 0o644:
            raise AssertionError("hash validation changed or rejected a transported 0644 input mode")
        try:
            verify_sha256(candidate, "0" * 64)
        except ContractError:
            pass
        else:
            raise AssertionError("a mismatched extracted executable hash was accepted")
        link = root / "candidate-link"
        link.symlink_to(candidate.name)
        try:
            verify_sha256(link, expected_hash)
        except ContractError:
            pass
        else:
            raise AssertionError("a linked extracted executable was accepted")

        if sys.platform.startswith("linux"):
            child = subprocess.Popen(
                [sys.executable, "-c", "import time; time.sleep(60)"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
            )
            try:
                deadline = time.monotonic() + 3.0
                child_start = None
                while time.monotonic() < deadline and child_start is None:
                    child_start = _proc_start_ticks(child.pid)
                    if child_start is None:
                        time.sleep(0.01)
                if child_start is None:
                    raise AssertionError("controlled cleanup child identity was unavailable")
                marker_path = root / "owned-child.marker"
                marker_path.write_text(
                    f"harness-child:{child.pid}:{child_start}:{int(time.monotonic() * 1000)}\n",
                    encoding="ascii",
                )
                _cleanup_owned_process(marker_path)
                child.wait(timeout=3.0)
                _wait_owned_exit(marker_path, 3.0)
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=3.0)

    print("PASS: synthetic archive/probe/identity/scenario helper regression cases")


def sanitized_failure_lines(path: Path) -> list[str]:
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return []
    return [line for line in lines if FAILURE_LINE_RE.fullmatch(line)]


def _read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        raise ContractError("required contract input could not be read") from error


def _owned_marker(path: Path) -> tuple[str, int, int, int]:
    return _parse_process_marker(_read_text(path))


def _wait_owned_exit(
    path: Path,
    timeout_seconds: float,
    proc_root: Path = Path("/proc"),
) -> None:
    _kind, pid, start_ticks, _tick = _owned_marker(path)
    deadline = time.monotonic() + timeout_seconds
    while True:
        state = _process_matches(pid, start_ticks, proc_root)
        if state is False:
            return
        if state is None:
            raise ContractError("the exact harness-owned child identity could not be confirmed")
        if time.monotonic() >= deadline:
            raise ContractError("the exact harness-owned child is still running")
        time.sleep(0.05)


def _send_signal_for_identity(
    pid: int,
    expected_start: int,
    signum: int,
    proc_root: Path = Path("/proc"),
) -> bool:
    pidfd_open = getattr(os, "pidfd_open", None)
    pidfd_send_signal = getattr(signal, "pidfd_send_signal", None)
    if pidfd_open is not None and pidfd_send_signal is not None:
        try:
            pidfd = pidfd_open(pid, 0)
        except ProcessLookupError:
            return False
        try:
            identity_state = _process_matches(pid, expected_start, proc_root)
            if identity_state is None:
                raise ContractError("the exact harness-owned child identity could not be confirmed")
            if identity_state is False:
                return False
            pidfd_send_signal(pidfd, signum)
            return True
        finally:
            os.close(pidfd)

    identity_state = _process_matches(pid, expected_start, proc_root)
    if identity_state is None:
        raise ContractError("the exact harness-owned child identity could not be confirmed")
    if identity_state is False:
        return False
    try:
        os.kill(pid, signum)
        return True
    except ProcessLookupError:
        return False


def _cleanup_owned_process(path: Path, proc_root: Path = Path("/proc")) -> None:
    _kind, pid, start_ticks, _tick = _owned_marker(path)
    identity_state = _process_matches(pid, start_ticks, proc_root)
    if identity_state is None:
        raise ContractError("the exact harness-owned child identity could not be confirmed")
    if identity_state is False:
        return
    if not _send_signal_for_identity(pid, start_ticks, signal.SIGTERM, proc_root):
        return
    deadline = time.monotonic() + 2.0
    while time.monotonic() < deadline:
        identity_state = _process_matches(pid, start_ticks, proc_root)
        if identity_state is None:
            raise ContractError("the exact harness-owned child identity became unreadable during cleanup")
        if identity_state is False:
            return
        time.sleep(0.05)
    identity_state = _process_matches(pid, start_ticks, proc_root)
    if identity_state is None:
        raise ContractError("the exact harness-owned child identity became unreadable during cleanup")
    if identity_state is False:
        return
    if not _send_signal_for_identity(pid, start_ticks, signal.SIGKILL, proc_root):
        return
    deadline = time.monotonic() + 3.0
    while time.monotonic() < deadline:
        identity_state = _process_matches(pid, start_ticks, proc_root)
        if identity_state is None:
            raise ContractError("the exact harness-owned child identity became unreadable during cleanup")
        if identity_state is False:
            return
        time.sleep(0.05)
    identity_state = _process_matches(pid, start_ticks, proc_root)
    if identity_state is None:
        raise ContractError("the exact harness-owned child identity became unreadable during cleanup")
    if identity_state is True:
        raise ContractError("the exact harness-owned child could not be stopped")


def main() -> int:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("self-test")
    archive = commands.add_parser("validate-archive")
    archive.add_argument("path", type=Path)
    digest = commands.add_parser("verify-sha256")
    digest.add_argument("path", type=Path)
    digest.add_argument("expected")
    probe = commands.add_parser("verify-probe")
    probe.add_argument("path", type=Path)
    probe.add_argument("version")
    info = commands.add_parser("verify-info")
    info.add_argument("path", type=Path)
    info.add_argument("version")
    native = commands.add_parser("validate-native")
    native.add_argument("path", type=Path)
    summary = commands.add_parser("validate-summary")
    summary.add_argument("native_path", type=Path)
    summary.add_argument("shell_updates_path", type=Path)
    failures = commands.add_parser("print-failures")
    failures.add_argument("path", type=Path)
    exited = commands.add_parser("wait-process-exited")
    exited.add_argument("marker", type=Path)
    exited.add_argument("--timeout-seconds", type=float, default=5.0)
    cleanup = commands.add_parser("cleanup-process")
    cleanup.add_argument("marker", type=Path)
    parent_timeout = commands.add_parser("verify-parent-timeout")
    parent_timeout.add_argument("start_marker", type=Path)
    parent_timeout.add_argument("result_marker", type=Path)
    parent_timeout.add_argument("parent_hold_start", type=Path)
    parent_timeout.add_argument("parent_hold_end", type=Path)
    readiness = commands.add_parser("verify-readiness-result")
    readiness.add_argument("path", type=Path)
    args = parser.parse_args()

    try:
        if args.command == "self-test":
            self_test()
        elif args.command == "validate-archive":
            validate_archive(args.path)
            print("ARCHIVE_CONTRACT=PASS members=8 prefix=optional-single-dot-slash")
        elif args.command == "verify-sha256":
            verify_sha256(args.path, args.expected)
            print("SHA256_BINDING=PASS")
        elif args.command == "verify-probe":
            verify_probe_bytes(args.path.read_bytes(), args.version)
            print(f"PROBE_CONTRACT=PASS version={args.version}")
        elif args.command == "verify-info":
            verify_info_bytes(args.path.read_bytes(), args.version)
            print(f"HARNESS_INFO_CONTRACT=PASS version={args.version}")
        elif args.command == "validate-native":
            for line in _parse_summary(_read_text(args.path)):
                print(line)
        elif args.command == "validate-summary":
            for line in _parse_summary(_read_text(args.native_path), _read_text(args.shell_updates_path)):
                print(line)
        elif args.command == "print-failures":
            for line in sanitized_failure_lines(args.path):
                print(line)
        elif args.command == "wait-process-exited":
            _wait_owned_exit(args.marker, args.timeout_seconds)
            print("OWNED_CHILD_EXIT=CONFIRMED")
        elif args.command == "cleanup-process":
            _cleanup_owned_process(args.marker)
            print("OWNED_CHILD_CLEANUP=CONFIRMED")
        elif args.command == "verify-parent-timeout":
            child_elapsed, hold_elapsed = _verify_parent_hold_timeout(
                _read_text(args.start_marker),
                _read_text(args.result_marker),
                _read_text(args.parent_hold_start),
                _read_text(args.parent_hold_end),
            )
            print(f"PARENT_EXIT_WAIT_MS={child_elapsed} PARENT_ALIVE_HOLD_MS={hold_elapsed}")
        elif args.command == "verify-readiness-result":
            elapsed = _verify_readiness_result(_read_text(args.path))
            print(f"READINESS_TIMEOUT_MS={elapsed}")
        return 0
    except Exception as error:
        error_type = type(error).__name__
        print(f"LINUX_CONTRACT_FAILURE command={args.command} type={error_type}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
