"""Browser Camoufox dan transport milik Catalog MangaReader.

Satu plugin fitur untuk pyhost: core tidak tahu arti command di sini, dan
plugin ini tidak menyentuh profile Google milik CamoProf atau profile Downloader.
Profile browser Catalog tinggal di bawah ``%LocalAppData%\\Citadel\\MangaReader\\catalog-browser\\
<provider>``.

Batas yang dijaga di sini:
- request API lewat client Axios milik halaman supaya situs sendiri yang
  menandatangani request; token tidak pernah dibuat, disimpan, atau disalin;
- byte halaman TIDAK pernah pulang lewat protokol NDJSON — browser menulis ke
  staging yang diizinkan dan hanya bukti (status, jumlah byte, hash) yang
  kembali;
- payload API dibatasi jauh di bawah batas baris protokol;
- path tulis harus kanonik di dalam root yang diberikan caller C#.
"""

import asyncio
import hashlib
import json
import os
import re
import uuid
from urllib.parse import parse_qsl, urlparse

from providers import PyhostError

NAME_RE = re.compile(r"^[A-Za-z0-9._-]+$")

# Jauh di bawah batas 4 MiB per baris protokol: satu respons API harus tetap
# muat setelah dibungkus envelope JSON.
MAX_API_TEXT = 1 * 1024 * 1024
MAX_PAGE_BYTES = 25 * 1024 * 1024

BRIDGE_ID = "citadel-catalog-bridge"
API_PREFIX = "/api/v1"

# Request API Comix wajib lewat instance Axios milik halaman (di chunk env,
# baseURL "/api/v1") karena chunk secure memasang interceptor token "_" ke
# instance itu. Fetch mentah dijawab 403 "Missing token.", dan token itu tidak
# pernah dibuat, disimpan, atau disalin di sini.
#
# Playwright/Camoufox mengevaluasi JS di realm yang berbeda (Firefox Xray
# boundary), jadi skrip ini dipasang lewat page.add_script_tag supaya berjalan
# di main realm halaman; request dan responsnya lewat atribut DOM, bukan lewat
# objek JS halaman.
_API_BRIDGE_JS = r"""
(() => {
    const BRIDGE_ID = 'citadel-catalog-bridge';
    if (document.getElementById(BRIDGE_ID)) return;

    const node = document.createElement('div');
    node.id = BRIDGE_ID;
    node.setAttribute('hidden', 'hidden');
    document.documentElement.appendChild(node);

    let apiPromise = null;

    function isApi(value) {
        return !!value && !!value.defaults && value.defaults.baseURL === '/api/v1'
            && !!value.interceptors && typeof value.request === 'function';
    }

    function api() {
        if (!apiPromise) {
            apiPromise = (async () => {
                const instance = Object.values(await import(__ENV_MODULE_URL__)).find(isApi);
                if (!instance) throw new Error('instance axios baseURL /api/v1 tidak ditemukan');
                return instance;
            })().catch(error => { apiPromise = null; throw error; });
        }
        return apiPromise;
    }

    function bodyText(data) {
        if (typeof data === 'string') return data;
        try { return JSON.stringify(data); } catch (e) { return ''; }
    }

    function contentType(headers) {
        if (!headers) return '';
        return headers['content-type'] || headers['Content-Type'] || '';
    }

    async function handle(raw) {
        let request;
        try { request = JSON.parse(raw); } catch (e) { return; }
        const write = payload => node.setAttribute(
            'data-response', JSON.stringify(Object.assign({ id: request.id }, payload)));
        const maxText = request.maxText;
        try {
            const client = await api();
            const response = await client.get(request.path, {
                params: request.params || {},
                timeout: request.timeoutMs,
            });
            const text = bodyText(response.data);
            write({
                status: response.status,
                contentType: contentType(response.headers) || 'application/json',
                finalUrl: (response.request && response.request.responseURL) || request.path,
                oversized: text.length > maxText,
                text: text.slice(0, maxText),
            });
        } catch (error) {
            const response = error && error.response;
            if (response) {
                // Jawaban 4xx/5xx tetap dibawa pulang apa adanya supaya alasan
                // provider ("Missing token.", "Invalid token.") terlihat di UI.
                const text = bodyText(response.data);
                write({
                    status: response.status,
                    contentType: contentType(response.headers),
                    finalUrl: request.path,
                    oversized: text.length > maxText,
                    text: text.slice(0, maxText),
                });
                return;
            }
            write({
                status: 0,
                contentType: '',
                finalUrl: request.path,
                oversized: false,
                text: '',
                error: String((error && error.message) || error).slice(0, 300),
            });
        }
    }

    new MutationObserver(mutations => {
        for (const mutation of mutations) {
            if (mutation.attributeName !== 'data-request') continue;
            const raw = node.getAttribute('data-request');
            if (!raw) continue;
            node.removeAttribute('data-request');
            void handle(raw);
        }
    }).observe(node, { attributes: true, attributeFilter: ['data-request'] });
})();
"""

_LOCATE_ENV_URL_JS = r"""() => {
    const declared = Array.from(document.querySelectorAll('script[src], link[href]'))
        .map(node => node.src || node.href)
        .filter(Boolean);
    const observed = performance.getEntriesByType('resource').map(entry => entry.name);
    return declared.concat(observed)
        .find(name => /\/env-[^/]*\.js(\?|$)/.test(name)) || null;
}"""


def _runtime_root():
    """Stable application location for this deployed plugin payload."""
    return os.path.realpath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))


def _instance_key(runtime_root):
    canonical = os.path.normcase(os.path.realpath(runtime_root)).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()[:12]


def browser_root():
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        raise PyhostError("NO_BROWSER_ROOT", "LOCALAPPDATA tidak tersedia")
    return os.path.realpath(
        os.path.join(local, "Citadel", "MangaReader", "catalog-browser", "instances",
                     _instance_key(_runtime_root())))


def _provider_dir(provider):
    if not isinstance(provider, str) or not NAME_RE.match(provider) \
            or provider in (".", ".."):
        raise PyhostError("BAD_PROVIDER_NAME",
                          "nama provider tidak sah: %r" % (provider,))
    root = browser_root()
    candidate = os.path.realpath(os.path.join(root, provider))
    if os.path.commonpath((root, candidate)) != root:
        raise PyhostError("PATH_ESCAPE",
                          "path provider keluar dari root: %r" % (provider,))
    return candidate


def _require_http_url(value, field):
    if not isinstance(value, str) or not value.strip():
        raise PyhostError("BAD_URL", "%s harus URL absolut" % field)
    parsed = urlparse(value.strip())
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise PyhostError("BAD_URL", "%s bukan http(s) absolut: %r" % (field, value))
    return value.strip()


def _contained_target(path, root):
    """Path tulis harus kanonik di dalam root staging milik Catalog."""
    if not isinstance(path, str) or not path.strip():
        raise PyhostError("BAD_PATH", "path tujuan wajib diisi")
    if not isinstance(root, str) or not root.strip():
        raise PyhostError("BAD_PATH", "root staging wajib diisi")

    resolved_root = os.path.realpath(root)
    candidate = os.path.realpath(path)
    if os.path.commonpath((resolved_root, candidate)) != resolved_root:
        raise PyhostError("PATH_ESCAPE",
                          "path tulis keluar dari staging: %r" % (path,))
    return candidate


def _timeout_ms(msg, default_ms):
    value = msg.get("timeout_ms", default_ms)
    if not isinstance(value, (int, float)) or value <= 0:
        raise PyhostError("BAD_TIMEOUT", "timeout_ms harus angka positif")
    return int(min(value, 180000))


def _is_waf_challenge(url):
    if not isinstance(url, str):
        return False
    return urlparse(url).path.startswith("/@waf/")


async def _wait_for_application_page(page, timeout_ms):
    if not _is_waf_challenge(page.url):
        return
    try:
        # The WAF page can complete its browser check without user input. Keep
        # waiting in the same isolated headless profile instead of relaunching a
        # visible browser, which used to create a second profile lifecycle and
        # surface Camoufox windows during normal Catalog work.
        await page.wait_for_url(
            lambda value: not _is_waf_challenge(str(value)),
            timeout=timeout_ms)
        await page.wait_for_load_state("domcontentloaded", timeout=timeout_ms)
    except Exception as exc:  # noqa: BLE001 - browser boundary
        raise PyhostError(
            "SITE_CHALLENGE_TIMEOUT",
            "verifikasi Comix belum selesai: %s" % exc) from exc


async def _navigate_application_page(page, start_url, timeout_ms):
    await page.goto(start_url, wait_until="domcontentloaded",
                    timeout=timeout_ms)
    await _wait_for_application_page(page, timeout_ms)


def _live_provider_session(host, profile):
    """Return the live session already owning this provider profile."""
    for sid, session in host.sessions.items():
        if session.get("profile") != profile:
            continue
        page = session.get("page")
        if session.get("ctx") is None or page is None:
            return None
        is_closed = getattr(page, "is_closed", None)
        if callable(is_closed) and is_closed():
            return None
        return sid, session, page
    return None


async def _forget_dead_provider_session(host, profile):
    """Remove a dead/partial owner so the same profile can open again."""
    for sid, session in tuple(host.sessions.items()):
        if session.get("profile") == profile:
            await host._drop_session(sid, forget_on_failure=True)


async def cmd_open(host, msg):
    """Buka (atau pakai ulang) satu session browser untuk satu provider."""
    provider = msg.get("provider")
    headless = msg.get("headless", True)
    if not isinstance(headless, bool):
        raise PyhostError("BAD_HEADLESS", "headless harus boolean")
    start_url = _require_http_url(msg.get("url"), "url")

    profile = "catalog-" + (provider if isinstance(provider, str) else "x")
    if host._profile_busy(profile):
        existing = _live_provider_session(host, profile)
        if existing is not None:
            sid, session, page = existing
            if session.get("headless", headless) == headless:
                await _navigate_application_page(
                    page, start_url, _timeout_ms(msg, 120000))
                return {"session": sid, "provider": provider,
                        "url": page.url, "headless": headless}
        await _forget_dead_provider_session(host, profile)

    pdir = _provider_dir(provider)
    os.makedirs(pdir, exist_ok=True)

    from camoufox.async_api import AsyncCamoufox  # berat: impor saat dipakai

    cm = AsyncCamoufox(
        persistent_context=True,
        user_data_dir=pdir,
        headless=headless,
        humanize=True,
        os="windows",
        disable_coop=True,
        i_know_what_im_doing=True,
        config={"forceScopeAccess": True},
    )

    # Session didaftarkan SEBELUM masuk context, mengikuti pola core: kalau
    # launch timeout atau dibatalkan, cleanup masih punya pegangan.
    host.next_sid += 1
    sid = "s%d" % host.next_sid
    host.sessions[sid] = {"profile": profile, "cm": cm, "ctx": None,
                          "page": None, "dir": pdir, "headless": headless}
    try:
        ctx = await cm.__aenter__()
        host.sessions[sid]["ctx"] = ctx
        page = ctx.pages[0] if ctx.pages else await ctx.new_page()
        host.sessions[sid]["page"] = page
        await _navigate_application_page(
            page, start_url, _timeout_ms(msg, 120000))
    except asyncio.CancelledError:
        await host._drop_session(sid)
        raise
    except PyhostError:
        await host._drop_session(sid, forget_on_failure=True)
        raise
    except Exception as e:  # noqa: BLE001 - laporkan terstruktur
        await host._drop_session(sid, forget_on_failure=True)
        raise PyhostError("BROWSER_LAUNCH",
                          "%s: %s" % (type(e).__name__, e))

    return {"session": sid, "provider": provider, "url": page.url,
            "headless": headless}


def _coerce_scalar(value):
    return int(value) if re.fullmatch(r"-?\d+", value) else value


def _assign_param(params, key, value):
    """Bentuk query Comix → param terstruktur yang interceptor situs harapkan.

    ``order[chapter_updated_at]=desc`` jadi object bersarang, ``content_rating[]``
    yang berulang jadi list, dan sisanya skalar. Query string mentah tidak pernah
    diteruskan ke client: token ``_`` hanya dibuat benar dari param terstruktur.
    """
    bracketed = re.fullmatch(r"([A-Za-z0-9_]+)\[([A-Za-z0-9_]*)\]", key)
    scalar = _coerce_scalar(value)
    if bracketed is None:
        params[key] = scalar
        return

    name, inner = bracketed.group(1), bracketed.group(2)
    if inner == "":
        existing = params.get(name)
        if isinstance(existing, list):
            existing.append(scalar)
        else:
            params[name] = [scalar]
        return

    bucket = params.get(name)
    if not isinstance(bucket, dict):
        bucket = {}
        params[name] = bucket
    bucket[inner] = scalar


def _api_call_target(url):
    """URL absolut dari C# → (path relatif terhadap baseURL, param terstruktur)."""
    parsed = urlparse(url)
    path = parsed.path or "/"
    if path.startswith(API_PREFIX):
        path = path[len(API_PREFIX):] or "/"

    params = {}
    for key, value in parse_qsl(parsed.query, keep_blank_values=True):
        _assign_param(params, key, value)
    return path, params


async def _ensure_bridge(page):
    """Pasang bridge di main realm; navigasi membuang dokumen beserta isinya."""
    present = "document.getElementById('%s') !== null" % BRIDGE_ID
    if await page.evaluate(present):
        return

    env_url = await page.evaluate(_LOCATE_ENV_URL_JS)
    parsed_page = urlparse(page.url)
    parsed_env = urlparse(env_url) if isinstance(env_url, str) else None
    if (parsed_env is None
            or parsed_env.scheme not in ("http", "https")
            or parsed_env.scheme != parsed_page.scheme
            or parsed_env.netloc != parsed_page.netloc
            or not re.search(r"/env-[^/]*\.js$", parsed_env.path)):
        raise PyhostError(
            "API_CLIENT_UNAVAILABLE",
            "modul API Comix tidak ditemukan pada dokumen aktif")

    bridge = _API_BRIDGE_JS.replace(
        "__ENV_MODULE_URL__", json.dumps(env_url))
    await page.add_script_tag(content=bridge)
    if not await page.evaluate(present):
        raise PyhostError(
            "API_BRIDGE_FAILED",
            "bridge API tidak terpasang di main realm halaman")


async def cmd_api(host, msg):
    """Satu panggilan API lewat client milik halaman; hanya JSON kecil yang pulang."""
    sess = host.get_session(msg.get("session"))
    page = sess.get("page")
    if page is None:
        raise PyhostError("NO_PAGE", "session tidak punya page aktif")
    url = _require_http_url(msg.get("url"), "url")
    timeout_ms = _timeout_ms(msg, 45000)
    path, params = _api_call_target(url)

    await _ensure_bridge(page)

    request_id = uuid.uuid4().hex
    request = json.dumps({
        "id": request_id,
        "path": path,
        "params": params,
        "timeoutMs": timeout_ms,
        "maxText": MAX_API_TEXT,
    })

    # Hanya DOM yang disentuh dari sisi ini, tidak pernah objek JS halaman.
    await page.evaluate(
        "([id, payload]) => {"
        "  const node = document.getElementById(id);"
        "  if (!node) throw new Error('bridge hilang');"
        "  node.removeAttribute('data-response');"
        "  node.setAttribute('data-request', payload);"
        "}",
        [BRIDGE_ID, request],
    )

    deadline = asyncio.get_running_loop().time() + (timeout_ms / 1000.0) + 5.0
    raw = None
    while raw is None:
        text = await page.evaluate(
            "([id, wanted]) => {"
            "  const node = document.getElementById(id);"
            "  if (!node) return null;"
            "  const value = node.getAttribute('data-response');"
            "  if (!value) return null;"
            "  let parsed;"
            "  try { parsed = JSON.parse(value); } catch (e) { return null; }"
            "  if (parsed.id !== wanted) return null;"
            "  node.removeAttribute('data-response');"
            "  return value;"
            "}",
            [BRIDGE_ID, request_id],
        )
        if text:
            raw = json.loads(text)
            break
        if asyncio.get_running_loop().time() >= deadline:
            raise PyhostError(
                "API_TIMEOUT",
                "client halaman tidak menjawab dalam %d ms" % timeout_ms)
        await asyncio.sleep(0.08)

    if raw.get("error"):
        raise PyhostError("API_CLIENT_UNAVAILABLE", str(raw["error"]))

    if raw.get("oversized"):
        raise PyhostError("API_TOO_LARGE",
                          "respons API melebihi %d karakter" % MAX_API_TEXT)

    body = raw.get("text") or ""
    payload = None
    if body:
        try:
            payload = json.loads(body)
        except ValueError:
            # HTML/error page adalah sinyal penting bagi aturan fallback C#.
            payload = None

    return {"status": raw.get("status"),
            "content_type": raw.get("contentType") or "",
            "final_url": raw.get("finalUrl") or url,
            "json": payload,
            "is_json": payload is not None,
            "text_head": body[:512]}


async def cmd_fetch(host, msg):
    """Unduh satu byte-range tujuan ke staging. Byte tidak pernah pulang."""
    sess = host.get_session(msg.get("session"))
    ctx = sess.get("ctx")
    if ctx is None:
        raise PyhostError("NO_PAGE", "session tidak punya context aktif")

    url = _require_http_url(msg.get("url"), "url")
    target = _contained_target(msg.get("path"), msg.get("root"))
    timeout_ms = _timeout_ms(msg, 30000)

    headers = {}
    referer = msg.get("referer")
    if isinstance(referer, str) and referer.strip():
        headers["Referer"] = referer.strip()
    extra = msg.get("headers")
    if isinstance(extra, dict):
        for key, value in extra.items():
            if isinstance(key, str) and isinstance(value, str):
                headers[key] = value

    os.makedirs(os.path.dirname(target), exist_ok=True)
    temporary = "%s.%s.part" % (target, os.getpid())
    try:
        response = await ctx.request.get(url, headers=headers, timeout=timeout_ms)
        status = response.status
        if status >= 400:
            # Bukan error protokol: C# yang mengklasifikasikan 404/429/5xx.
            return {"status": status, "bytes": 0, "sha256": None,
                    "content_type": response.headers.get("content-type", ""),
                    "path": None}

        body = await response.body()
        if len(body) > MAX_PAGE_BYTES:
            raise PyhostError("PAGE_TOO_LARGE",
                              "page %d byte melebihi %d" % (len(body), MAX_PAGE_BYTES))

        with open(temporary, "wb") as handle:
            handle.write(body)
        os.replace(temporary, target)
    except PyhostError:
        raise
    except Exception as e:  # noqa: BLE001 - kelas jaringan dilaporkan apa adanya
        raise PyhostError("FETCH_FAILED", "%s: %s" % (type(e).__name__, e))
    finally:
        if os.path.exists(temporary):
            try:
                os.remove(temporary)
            except OSError:
                pass

    return {"status": status,
            "bytes": len(body),
            "sha256": hashlib.sha256(body).hexdigest(),
            "content_type": response.headers.get("content-type", ""),
            "path": target}


async def cmd_close(host, msg):
    """Tutup satu session Catalog; idempoten seperti session.close core."""
    sid = msg.get("session")
    if sid not in host.sessions:
        return {"closed": False, "session": sid}

    closed = await host._drop_session(sid)
    if not closed:
        raise PyhostError("BROWSER_CLOSE_FAILED",
                          "close gagal; session dipertahankan untuk retry")
    return {"closed": True, "session": sid}
