# pyhost protocol v1 — the C# ↔ Python seam

`pyhost.py` is the ONLY way citizens talk to Python. One process per owning
view; stdin/stdout carry **newline-delimited JSON** — one object per line.

This file is the contract. If code and this file disagree, the file wins —
fix the code.

## Transport rules

- **Request:**  `{"id": <int>, "cmd": "<name>", ...params}`
- **Response:** `{"id": <same int>, "ok": true, ...}` or
  `{"id": <same int>, "ok": false, "error": {"code": "...", "message": "..."}}`
- **Exactly one response per request.** No unsolicited lines in v1
  (an `{"event": ...}` shape is reserved for v2 progress).
- **stdout is protocol-only.** All logging goes to stderr. A consumer must
  treat any non-JSON stdout line as a bug in pyhost.
- Requests are processed **sequentially**, each bounded by a timeout:
  default **120 s**, overridable per request via `"timeout": <seconds>`.
- Unknown command → `UNKNOWN_COMMAND`. Malformed JSON → `BAD_JSON`.
  Handler crash → `INTERNAL`. Timeout → `TIMEOUT`.

## Startup contract

- Environment variable **`CITADEL_CREDENZ` is required** and must be an
  **absolute** path. It is set by the C# host at spawn; pyhost never
  computes or guesses a path.
- Missing/invalid → pyhost still answers `ping` (with
  `"credenz_ready": false`) but every other command fails with
  `STARTUP_NO_CREDENZ`.

## Commands

### `ping`
```json
> {"id": 1, "cmd": "ping"}
< {"id": 1, "ok": true, "protocol": 1, "python": "3.12.10",
   "camoufox": "0.5.5", "playwright": "1.51.0", "credenz_ready": true}
```

### `session.open`
Opens a camoufox persistent context — the profile "home". It remains headed
by default so existing callers keep the same interactive behaviour.

```json
> {"id": 2, "cmd": "session.open", "profile": "dhepil.main",
   "start_url": "https://www.google.com/", "headless": false}
< {"id": 2, "ok": true, "session": "s1", "profile": "dhepil.main",
   "profile_dir": "C:\\...\\credenz\\google\\profiles\\dhepil.main",
   "headless": false}
```

- `profile` — required. Validated against `^[A-Za-z0-9._-]+$`, `.` and `..`
  rejected; the resolved directory must stay under
  `<CITADEL_CREDENZ>/google/profiles/` (`BAD_PROFILE_NAME` / `PATH_ESCAPE`).
  The directory is created if absent.
- `start_url` — optional, defaults to `https://www.google.com/`.
- `headless` — optional boolean, defaults to `false`. CamoProf uses a temporary
  headless session only when the user disables `Show browser while checking`.
  `google.relogin` refuses a headless session.
- One live session per profile: a second `open` on the same profile fails
  with `PROFILE_BUSY`.
- Launch failure → `BROWSER_LAUNCH`.

### `session.navigate`

Navigates an existing resident session without creating a second browser or
changing its profile ownership:

```json
> {"id": 3, "cmd": "session.navigate", "session": "s1",
   "url": "https://github.com/"}
< {"id": 3, "ok": true, "session": "s1", "url": "https://github.com/"}
```

- `url` must be an absolute HTTP(S) URL (`BAD_URL`).
- A network/navigation failure returns `NAVIGATE_FAILED` and keeps the session
  registered for retry.
- A closed browser returns `BROWSER_GONE` and removes the stale session.

### `session.verify`
Checks whether Google still recognizes the home. Navigates the session's
page to `https://myaccount.google.com/`, waits 4 s, then decides by the
FINAL URL (deviation from `reference/human_login.py` — recorded below):

- **alive ⇔ the final URL is on `myaccount.google.com`.** Signed-out
  sessions land elsewhere (signin OR Google's public `/account/about/`
  page) — both are `alive: false`, honestly.

```json
> {"id": 3, "cmd": "session.verify", "session": "s1"}
< {"id": 3, "ok": true, "alive": true,
   "url": "https://myaccount.google.com/", "state_saved": true}
```

- **Contract (codex audit decision, 2026-08-31):** `alive: true` IS the
  success criterion. `_session_state.json` is written as a diagnostic
  artifact and `state_saved: true/false` reports only whether it was
  written — trust lives in the persistent directory alone and the file is
  never read back. Gate G2 passes on `alive: true` even if the JSON could
  not be written.
- **Failure split (codex audit #4):** a closed browser → `BROWSER_GONE` and
  the session is dropped (a later `session.open` will not hit
  `PROFILE_BUSY`). A network/DNS/navigation failure → `VERIFY_FAILED` and
  the session is KEPT alive — the browser is presumably fine, only the
  network hiccuped. Not-logged-in is a normal `alive: false` response, not
  an error at all.

`session.verify` remains a backward-compatible adapter over `google.inspect`.

### `google.inspect`

Checks the resident profile at `myaccount.google.com` and returns a structured
account result:

```json
> {"id": 4, "cmd": "google.inspect", "session": "s1"}
< {"id": 4, "ok": true, "state": "active",
   "email": "user@gmail.com", "url": "https://myaccount.google.com/",
   "state_saved": true}
```

- `state` is `active`, `signed_out`, or `unknown`.
- `email` is detected from account identity attributes and contains the email
  address, never Google's display name. It can be null when identity markup is
  unavailable.
- DNS/navigation failures remain `VERIFY_FAILED`; they are not converted to
  `signed_out`.

### `google.relogin`

Performs one ordinary email/password login attempt in an already-open **headed**
session:

```json
> {"id": 5, "cmd": "google.relogin", "session": "s1",
   "email": "user@gmail.com", "password": "request-only"}
< {"id": 5, "ok": true, "state": "active",
   "email": "user@gmail.com", "url": "https://myaccount.google.com/"}
```

The result state is `active`, `credential_rejected`, or `action_required`.
2FA, CAPTCHA, passkeys, device confirmation, recovery, and unknown sign-in
steps become `action_required`; pyhost does not bypass them. Email/password are
request-only data and are never echoed or logged.

### `camoprof.add_profile.*` — type-once credential capture (plugin)

Four polling-compatible commands registered by the **camoprof_add_profile
feature plugin** (loaded via `CITADEL_PYHOST_PLUGINS`). Add Profile lets the
user type their Google password **once**, directly into Google's real login
page in a headed resident session; the plugin captures it via a page listener
and hands it over exactly one time so the C# feature can store it
DPAPI-encrypted for automatic relog. There is no second password dialog and
no OS-level keyboard capture of any kind. The shared core knows none of this:
plugins register command namespaces and lifecycle hooks; the dependency
direction is always plugin → core.

**Arm-before-navigate (guaranteed order):** `start` creates the enrollment
page in the existing persistent context, installs the capture listener on it,
sets it as the session's primary page, then closes the initial `about:blank`
page. Only after the listener is armed does background navigation to Google's
sign-in begin. When `start` returns, exactly one live page remains and capture
is already active, so the first password keystroke cannot be missed.

**Non-blocking start:** the navigation itself runs as an enrollment-owned
background task, not inside the `start` response. Protocol v1 processes
requests sequentially — if `start` awaited its `goto`, a `cancel` arriving
mid-navigation would queue behind it and a dialog close could hang for up to
the 45 s goto timeout. Teardown cancels and awaits that task before closing
the page. A non-browser navigation failure ends the enrollment with state
`failed` (surfaced by `status`); a dead browser still routes through
`BROWSER_GONE`.

**Capture boundaries (enforced in JS at event time AND again in Python before
storing):**

- exact host `accounts.google.com` only (hostname equality, no substring);
- password fields only (`input[type=password]`; Google's audited field is
  `input[name="Passwd"]`);
- the listener exists only on the enrollment page and dies with it;
- retyping overwrites the held value; **emptying the field drops the captured
  candidate** (a stale or partial value can never become the stored relog
  credential just because the user typed and then deleted it);
- values are never logged, never appear in `status`/error responses, and are
  dropped on consume/teardown.

**State machine:**

```text
armed → password_observed → waiting_for_google | challenge
      → complete → consumed
(any non-terminal state)
      → cancelled | expired | browser_gone | wrong_account | failed
```

- one active enrollment per profile; a second `start` while non-terminal fails
  with `ENROLLMENT_ACTIVE` (terminal states may be replaced by a new `start`);
- internal lifetime is 10 minutes (not a UI setting): a background task and a
  status-time deadline check both expire the enrollment, dropping the secret —
  including from `complete`, which must not wait forever for a `finish`;
- `finish` is one-shot: after `consumed`, the secret is gone and a second call
  fails with `ENROLLMENT_CONSUMED`;
- passkey/QR logins reach `complete` with `has_password: false` — honest: the
  session is active but there is no secret to hand over;
- `expected_email` (optional, for password-repair flows): an active identity
  that differs ends the enrollment as `wrong_account`; `finish` then fails
  with `WRONG_ACCOUNT` and no stored credential is overwritten;
- enrollment cleanup follows the **session lifecycle**: every session-death
  path (manual browser close, `session.close`, launch failure, `close_all` on
  shutdown/stdin-EOF) funnels through `_drop_session`, which disarms the
  owning enrollment and drops its secret. No path retains a password after
  its session is gone;
- **one live window after enrollment starts:** the listener-owned page is
  installed as the session primary page before the initial page is closed.
  Teardown retires the session synchronously and closes the page plus browser
  context in the background. The Add Profile browser therefore ends with the
  flow; there is no replacement or idle blank page.

#### `camoprof.add_profile.start`
```json
> {"id": 8, "cmd": "camoprof.add_profile.start", "session": "s1",
   "expected_email": "user@gmail.com"}
< {"id": 8, "ok": true, "session": "s1", "state": "armed"}
```

Headed sessions only (`HEADLESS_RELOGIN`-style guard: `HEADLESS_ENROLLMENT`).
`expected_email` is optional and must be a valid email when present
(`BAD_CREDENTIAL_INPUT`). The response returns as soon as the listener is
armed; the login-page navigation proceeds in the background and a non-browser
failure surfaces later as state `failed` via `status`.

#### `camoprof.add_profile.status`
```json
> {"id": 9, "cmd": "camoprof.add_profile.status", "session": "s1"}
< {"id": 9, "ok": true, "session": "s1", "state": "password_observed",
   "email": null, "has_password": true, "challenge": false,
   "url": "https://accounts.google.com/signin/v2/identifier"}
```

Advances the state machine (URL + identity proof) and never carries plaintext.
`email` is populated only once an active identity has been detected.

#### `camoprof.add_profile.finish`
```json
> {"id": 10, "cmd": "camoprof.add_profile.finish", "session": "s1"}
< {"id": 10, "ok": true, "session": "s1", "email": "user@gmail.com",
   "password": "one-time-handover"}
```

Refused before `complete` (`ENROLLMENT_NOT_COMPLETE`), after consumption
(`ENROLLMENT_CONSUMED`), and on `wrong_account` (`WRONG_ACCOUNT`). `password`
is `null` for passkey logins. This is the only command that ever carries
plaintext, exactly once.

**Fast handover:** `finish` (and `cancel`) return in milliseconds — the
secret is dropped and the session registry entry released synchronously,
while the actual browser shutdown runs as a background task. A command
response never waits on a browser dying.

#### `camoprof.add_profile.cancel`
```json
> {"id": 11, "cmd": "camoprof.add_profile.cancel", "session": "s1"}
< {"id": 11, "ok": true, "session": "s1", "state": "cancelled"}
```

Idempotent full teardown: listener, secret, expiry task, enrollment page, and
the session owned by this flow. An unknown enrollment returns
`{"state": "none"}`. Browser shutdown runs in the background, while the
cancel response returns immediately.

### `session.close`
```json
> {"id": 6, "cmd": "session.close", "session": "s1"}
< {"id": 6, "ok": true, "closed": "s1"}
```
Unknown id → `SESSION_NOT_FOUND`.
The session is removed from the registry only after context shutdown is
confirmed. A close error returns `BROWSER_CLOSE_FAILED` and keeps the session
available for retry; a close timeout likewise keeps it registered.

### `shutdown`
```json
> {"id": 7, "cmd": "shutdown"}
< {"id": 7, "ok": true, "stopping": true}
```
Responds first, then closes every context and exits 0.

## Lifecycle & orphan safety

- **stdin EOF** (parent died / pipe closed) → close all contexts, exit 0.
  This is the orphan guard: a dead C# host can never leave headless ghosts.
- Process exit always closes contexts (`finally` in `__main__`).
- The C# host escalates to `Process.Kill(entireProcessTree: true)` ONLY
  after a graceful `shutdown` + stdin close times out.
- pyhost exiting kills its browser windows; the persistent profile
  directory keeps everything (cookies, storage, device trust).

## Error codes (v1)

| code | meaning |
|---|---|
| `BAD_JSON` | request line is not a JSON object |
| `UNKNOWN_COMMAND` | cmd not in the v1 table |
| `TIMEOUT` | command exceeded its timeout |
| `STARTUP_NO_CREDENZ` | `CITADEL_CREDENZ` missing/not absolute |
| `BAD_PROFILE_NAME` | profile fails the name regex / is `.`/`..` |
| `BAD_HEADLESS` | `session.open.headless` is not boolean |
| `PATH_ESCAPE` | resolved profile path escapes the profiles root |
| `PROFILE_BUSY` | profile already has a live session |
| `BROWSER_LAUNCH` | camoufox failed to start |
| `BROWSER_GONE` | browser window died mid-session (session dropped) |
| `BROWSER_CLOSE_FAILED` | context close failed; session kept for retry |
| `BAD_URL` | `session.navigate` URL is absent or not absolute HTTP(S) |
| `NAVIGATE_FAILED` | navigation failed; live session kept for retry |
| `VERIFY_FAILED` | verify navigation failed (DNS/network/timeout) — session KEPT |
| `HEADLESS_RELOGIN` | relog was requested on a headless session |
| `BAD_CREDENTIAL_INPUT` | relog email/password input is absent or malformed |
| `RELOGIN_FAILED` | headed relog navigation failed; session kept |
| `HEADLESS_ENROLLMENT` | enrollment was requested on a headless session |
| `ENROLLMENT_ACTIVE` | profile already has a live (non-terminal) enrollment |
| `ENROLLMENT_NOT_FOUND` | no enrollment exists for that session |
| `ENROLLMENT_NOT_COMPLETE` | `finish` requested before state `complete` |
| `ENROLLMENT_CONSUMED` | `finish` requested a second time; secret already handed over |
| `ENROLLMENT_START_FAILED` | listener install or login-page navigation failed |
| `WRONG_ACCOUNT` | active identity differs from `expected_email` |
| `SESSION_NOT_FOUND` | unknown session id |
| `INTERNAL` | anything else (message carries type + detail) |

## v2 (reserved — not implemented)

Interactive scraping for the manga downloader: `page.goto`, `page.eval`,
`page.fetch` (download through the browser context so cookies/fingerprint
apply), plus `{"event": ...}` progress lines. Added when a consumer states
concrete needs — do not build ahead.

## Manual smoke (PowerShell)

```powershell
$env:CITADEL_CREDENZ = 'C:\VSCODE\citadel\module\credenz'
'{"id":1,"cmd":"ping"}' | & <runtime>\.venv\Scripts\python.exe pyhost.py
```

Expected: one JSON line with `"ok": true`.
