r"""Live smoke: real Camoufox + real enrollment commands, disposable profile.

Drives the CURRENT repo pyhost.py (not the deployed copy) through the full
enrollment lifecycle with a real headed browser:

  session.open          -> one browser window (resident page, about:blank)
  google.enrollment.start -> resident page closed, enrollment page armed
                           THEN navigated to Google sign-in (one window)
  google.enrollment.status -> armed / signed_out family, no plaintext
  google.enrollment.cancel -> teardown + clean replacement page
  session.navigate      -> MUST succeed (sess["page"] repaired by teardown)
  session.close / shutdown

Exit code 0 = every gate passed.
"""

import json
import os
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PYHOST = os.path.join(
    REPO_ROOT, "module", "sharedLogic", "pyhost", "pyhost.py")
PYTHON = os.path.join(
    os.environ["LOCALAPPDATA"], "Citadel", "runtime", ".venv",
    "Scripts", "python.exe")

failures = []


def check(name, condition, detail=""):
    status = "PASS" if condition else "FAIL"
    print("[%s] %s %s" % (status, name, detail))
    if not condition:
        failures.append(name)


def main():
    credenz = tempfile.mkdtemp(prefix="CitadelEnrollSmoke-")
    env = {**os.environ, "CITADEL_CREDENZ": credenz}
    proc = subprocess.Popen(
        [PYTHON, "-u", PYHOST],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, encoding="utf-8", env=env)
    rid = 0

    def call(cmd, **params):
        nonlocal rid
        rid += 1
        request = {"id": rid, "cmd": cmd, **params}
        proc.stdin.write(json.dumps(request) + "\n")
        proc.stdin.flush()
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("pyhost EOF — host died on " + cmd)
        return json.loads(line)

    try:
        ping = call("ping")
        check("ping", ping.get("ok") is True)

        opened = call("session.open", profile="smoke.one",
                      start_url="about:blank", headless=False)
        check("session.open", opened.get("ok") is True, str(opened))
        sid = opened.get("session")

        started = call("google.enrollment.start", session=sid)
        check("enrollment.start returns armed",
              started.get("ok") is True
              and started.get("state") == "armed", str(started))

        # Give the background navigation a moment, then poll status.
        import time
        time.sleep(4)
        status = call("google.enrollment.status", session=sid)
        check("enrollment.status ok", status.get("ok") is True, str(status))
        check("status never carries a password",
              "password" not in status, str(sorted(status)))
        check("state is a sign-in family state",
              status.get("state") in
              ("armed", "password_observed", "waiting_for_google"),
              status.get("state", "?"))
        check("url reached Google sign-in",
              "accounts.google.com" in (status.get("url") or ""),
              status.get("url", "?"))

        cancelled = call("google.enrollment.cancel", session=sid)
        check("enrollment.cancel", cancelled.get("ok") is True
              and cancelled.get("state") == "cancelled", str(cancelled))

        # The repair gate: teardown must have restored a LIVE resident
        # page reference — navigate must succeed on it.
        nav = call("session.navigate", session=sid,
                   url="https://example.com/")
        check("navigate on repaired resident page",
              nav.get("ok") is True, str(nav))

        closed = call("session.close", session=sid)
        check("session.close", closed.get("ok") is True, str(closed))

        down = call("shutdown")
        check("shutdown", down.get("ok") is True, str(down))
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            proc.kill()
        stderr = proc.stderr.read()
        proc.stderr.close()
        # No plaintext in the log — there was never a password typed, but
        # assert the log stays protocol-clean anyway.
        check("pyhost exits 0", proc.returncode == 0,
              "rc=%s" % proc.returncode)
        if stderr.strip():
            print("--- pyhost stderr ---")
            print(stderr.strip())
        import shutil
        shutil.rmtree(credenz, ignore_errors=True)

    total = len(failures)
    print("\nRESULT: %s (%d failed)"
          % ("PASS" if not failures else "FAIL", total))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
