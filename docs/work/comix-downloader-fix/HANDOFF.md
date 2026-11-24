# Comix Downloader Fix — Handoff

## Goal

Press Start on Comix in Citadel's MangaReader Downloader → catalog results appear.

## Architecture (how it should work)

```
C# ComixSource.BrowseAsync
  → DownloaderPyHostClient.EnsureSessionAsync("comix", "https://comix.ws/browse")
    → pyhost cmd_open → Camoufox persistent context → page.goto → WAF wait
  → DownloaderPyHostClient.ApiAsync(fullUrl)
    → pyhost cmd_api
      → _ensure_bridge(page, session)
        → find apicore module URL from performance entries
        → capture Axios client instance
        → inject bridge script (DOM node + MutationObserver)
      → bridge calls client.get(path, {params})
        → site's request interceptor signs with "_" token
        → site's response interceptor decrypts
      → response via DOM attribute → Python → C#
```

## Confirmed facts (validated in Camoufox via single diagnostic run)

1. Site renamed its API bundle: `env-*.js` → `apicore-*.js`. Filename fix applied and working.
2. `page.evaluate()` shares the module map in Camoufox. `import(apicoreUrl)` returns correct exports.
3. Export `B` = Axios factory (has `.Axios`, `.Axios.prototype`, `.create`).
4. Export `p` = service object `cn` (plain object, methods internally call the hidden client).
5. **No export has `defaults.baseURL === '/api/v1'`**. The configured instance is created inside a closure (`qj = ji.create(eq)`) and is NOT exported. `Object.values(module).find(isApi)` returns `[]`.
6. The page itself loads and renders fine in Camoufox (headless, direct connection). Title correct, browse UI present.
7. In Chrome DevTools, patching `Axios.prototype.GI` then clicking a title link captured the instance (`this.defaults.baseURL === '/api/v1'`, 2 request interceptors, 2 response interceptors). Calling `.get('/manga', {params:{limit:1}})` on the captured instance returned HTTP 200 `{items, meta}`.
8. In Camoufox, the same prototype patch did NOT intercept. `stillPatched: true` but `patched: false` after clicking. The click caused a full page navigation (not SPA), destroying the patched document.
9. Raw API without token returns `{"message":"Missing token.","code":"missing_token"}`. Response with token is encrypted at network level; decryption happens in the Axios response interceptor.
10. Axios uses its own internal `bind()` utility, NOT `Function.prototype.bind`.
11. SOCKS5 proxy with auth is not supported by Firefox/Camoufox engine. User's proxy is SOCKS5 only.

## What failed and why

| Attempt | Why it failed |
|---------|--------------|
| `Function.prototype.bind` init script | Axios doesn't use it. Patch broke all page JS → crash → C# retry loop → barrage spawning |
| Prototype patch via `add_script_tag` + click | Camoufox does full navigation on link click (not SPA). Patch destroyed before request fires |
| `page.evaluate` + call service object `fn({})` | Wrong params or wrong object; no request triggered through the client |
| `page.evaluate` + click pagination immediately | Button not rendered yet (page still loading results). No click → no capture → error → retry loop |
| `page.evaluate` + wait 15s for button + click | Deployed but UNTESTED. May still fail if Camoufox pagination also causes full navigation |

## Current code state

File: `module/mangareader/Features/Downloader/mangareader_downloader/browser.py`
Deployed copy: `artifacts/publish/win-x64/sharedLogic/pyhost/mangareader_downloader/browser.py`

Changes applied:
- `_LOCATE_ENV_URL_JS`: regex changed from `env-` to `apicore-`
- `_valid_api_module_url`: regex changed + port check added
- `_API_CAPTURE_JS`: new constant. Uses `page.evaluate()` to patch prototype, wait for pagination button, click it, poll for capture, restore. Stores in `window.__citadelApiClient`.
- `_ensure_bridge`: calls `page.evaluate(_API_CAPTURE_JS, env_url)` before injecting bridge
- `_API_BRIDGE_JS` `api()`: checks `window.__citadelApiClient` first, falls back to export scan
- Comment updated (removed stale "env chunk" reference)

Tests: `tests/Module.Mangareader.Downloader.Tests/test_browser_transport.py` — 9/9 pass.
Test harness fixes: `_BridgePage.evaluate` accepts `*args`, `_ChallengePage` uses `evaluate` instead of stale `wait_for_url`, fake pages don't need `add_init_script`.

## The one remaining unknown

**Why does prototype patching intercept in Chrome but not Camoufox?**

Hypothesis A: Camoufox link clicks cause full navigation (confirmed for title links). Pagination buttons might behave differently (they're `<button>` not `<a>`). The latest fix waits for the button and clicks it — untested.

Hypothesis B: `page.evaluate()` runs in a utility world that shares the module map for `import()` but NOT for prototype identity. The patched prototype in evaluate's world is a different object than what the page's main-world scripts use. (Disproved partially: diagnostic showed same exports. But prototype identity wasn't tested.)

Hypothesis C: The page's client methods are bound copies (via Axios internal bind) that captured the original function reference at creation time. Patching the prototype AFTER creation doesn't affect already-bound instance methods. (This would explain why it fails everywhere, but it DID work in Chrome... unless Chrome's click triggered a NEW instance creation via router re-init.)

## Recommended next step

Run ONE controlled Camoufox session (direct, no proxy, single page load) with this diagnostic:

```javascript
// After page loads and results render:
// 1. Check if pagination button exists
// 2. Patch prototype
// 3. Click pagination button (NOT a link)
// 4. Wait 3s
// 5. Report: did patch fire? did page navigate? what's document.URL now?
```

If the button click ALSO causes full navigation → the site does full navigations for everything in Camoufox (no SPA). Then the approach must change to: intercept the INITIAL page load request (before it happens) via `context.add_init_script` that patches at the XHR or module-evaluation level.

If the button click stays on the same page but patch doesn't fire → Hypothesis C (bound methods). Then the fix is: patch BEFORE the instance is created, i.e., patch `B.create` or `B.Axios` constructor via an init script that runs after the module loads but before the instance is created. This requires a `MutationObserver` on script tags or a `page.route` intercept of the apicore JS to prepend the patch.

## Retry loop

C# `ApiAsync` catches `API_CLIENT_UNAVAILABLE` and retries with proxy rotation when in proxy mode. Each retry reuses the session → `cmd_api` → `_ensure_bridge` → capture fails → error → retry. To stop the barrage: either make capture work, or add `session["capture_failed"]` flag to skip re-attempt and surface a terminal error.

## User constraints

- No spam to live site. One controlled request OK. Repeated without proxy risks IP ban.
- SOCKS5 proxy auth not supported by Camoufox. User's proxy is SOCKS5 only. Direct connection used for diagnostics.
- Don't break other running downloads. Python payload is loaded at pyhost startup; Stop/Start Comix picks up new code without affecting other modules.
- User does not read code. Report in plain words. Fix at the exact failure point only.