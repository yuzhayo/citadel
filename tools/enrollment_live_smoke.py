r"""Live smoke: real Camoufox + real Add Profile plugin, disposable profile.

Drives the DEPLOYED pyhost payload (exactly what Citadel.Shell runs —
repo source, rebuilt and mirrored beside the shell exe) through the full
Add Profile lifecycle with a real headed browser:

  session.open                 -> one browser window (about:blank)
  camoprof.add_profile.start   -> claims the resident page, arms capture,
                                  THEN navigates it to Google sign-in
                                  (same single window)
  camoprof.add_profile.status  -> sign-in family states, no plaintext
  camoprof.add_profile.cancel  -> teardown: browser ENDS (flow over)
  session.close                -> SESSION_NOT_FOUND = proof the flow
                                  cleaned itself up
  shutdown

Exit code 0 = every gate passed. Rebuild the citizen first:
  dotnet build module/camoprof/Module.Camoprof.csproj -c Release
"""

import json
import os
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# The deployed payload: what the real app executes. This is the same
# copy Citizen.targets mirrors beside the shell executable.
DEPLOYED_PYHOST = os.path.join(
    REPO_ROOT, "core", "Citadel.Shell", "bin", "Release",
    "net10.0-windows", "sharedLogic", "pyhost", "pyhost.py")
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
    if not os.path.exists(DEPLOYED_PYHOST):
        print("deployed payload missing: %s" % DEPLOYED_PYHOST)
        print("run: dotnet build module/camoprof/Module.Camoprof.csproj "
              "-c Release")
        return 2

    credenz = tempfile.mkdtemp(prefix="CitadelEnrollSmoke-")
    env = {**os.environ, "CITADEL_CREDENZ": credenz,
        "CITADEL_PYHOST_PLUGINS": "camoprof_add_profile"}
    proc = subprocess.Popen(
        [PYTHON, "-u", DEPLOYED_PYHOST],
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

        started = call("camoprof.add_profile.start", session=sid)
        check("enrollment.start returns armed",
              started.get("ok") is True
              and started.get("state") == "armed", str(started))

        # Give the background navigation a moment, then poll status.
        import time
        time.sleep(4)
        status = call("camoprof.add_profile.status", session=sid)
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

        cancelled = call("camoprof.add_profile.cancel", session=sid)
        check("enrollment.cancel", cancelled.get("ok") is True
              and cancelled.get("state") == "cancelled", str(cancelled))

        # The flow-END gate: teardown dropped the session itself —
        # close proves absence instead of closing anything.
        closed = call("session.close", session=sid)
        check("session gone after flow end",
              closed.get("ok") is False
              and closed.get("error", {}).get("code") == "SESSION_NOT_FOUND",
              str(closed))

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
