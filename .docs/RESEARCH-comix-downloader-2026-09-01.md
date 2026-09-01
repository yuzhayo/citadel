# Research record: Comix source for Citadel Manga Downloader

Status: **RECORDED — discovery only; implementation is not approved by this document**  
Observed: **2026-09-01, Windows host, logged out**  
Target: <https://comix.to/title/dy88-the-novels-extra?group_id=9897>

## 1. Purpose and boundary

This record preserves the evidence collected before designing the Citadel Manga
Downloader. The desired product remains a local-first manga reader plus
downloader. It is not a streaming-reader clone of Suwayomi.

The work behind this record was read-only against Citadel. It inspected the
target website, the installed Suwayomi runtime, the local Comix extension APK,
the current upstream extension source, ordinary Chromium, direct Camoufox, and
the existing `stealthB` wrapper. No Citadel source file was changed during the
probe. Temporary scripts, screenshots, downloaded page samples, and the sample
CBZ were deleted after their results were verified and transcribed here.

This document does not authorize:

- a downloader implementation;
- changes to `stealthB`;
- changes to machine DNS or the hosts file;
- copying Suwayomi or the extension wholesale;
- choosing a final UI, persistence schema, or deduplication policy.

## 2. Inputs and identity evidence

### Local APK

```text
Path    C:\VSCODE\ARTEFACT\tachiyomi-en.comix-v1.6.37.apk
SHA-256 0501006122FC1C419A497C7E8746F7BD7DA5288EF31B8605D89FF3D11A1F5353
```

The APK was inspected as a reference, not executed as Citadel code. The current
upstream implementation is available in:

- <https://github.com/keiyoushi/extensions-source/tree/main/src/en/comix>
- `Comix.kt`: <https://github.com/keiyoushi/extensions-source/blob/main/src/en/comix/src/eu/kanade/tachiyomi/extension/en/comix/Comix.kt>
- `Descrambler.kt`: <https://github.com/keiyoushi/extensions-source/blob/main/src/en/comix/src/eu/kanade/tachiyomi/extension/en/comix/Descrambler.kt>

The current upstream source and the v1.6.37 APK are related references, but this
record does not claim that the current branch is byte-for-byte identical to the
APK.

### Suwayomi runtime

The live process at observation time was:

```text
Process     javaw.exe, PID 23436
Executable  C:\Program Files\Suwayomi-Server\jre\bin\javaw.exe
Server JAR  C:\Program Files\Suwayomi-Server\bin\Suwayomi-Server.jar
Root dir    D:\[ MANGA ]
```

`D:\[ MANGA ]\logs\application.log` existed. Its captured content described
CEF/WebUI setup and one unrelated `Failed to get current version` error. It did
not contain a Comix, DNS, TLS, or chapter-loading exception. Therefore the
Suwayomi-specific cause is not proven from its application log; it is compared
below against independently reproduced failure modes.

## 3. Test matrix

| Surface | Result | Evidence |
|---|---|---|
| System DNS for `comix.to` | FAIL | Resolved to `43.173.57.48`; direct Windows HTTP/TLS failed or timed out. |
| Cloudflare and Google public DNS | PASS | Both returned `104.21.49.220` and `172.67.152.235`. |
| Forced request to either Cloudflare IP | PASS | HTTPS response was HTTP 200 with the expected title HTML. |
| Server-rendered target HTML | PASS | Valid `initial-data` contained manga identity, group data, and first/latest chapter URLs. |
| Plain Playwright Chromium, strict TLS | FAIL | `ERR_CONNECTION_CLOSED` on `comix.to`. |
| Plain Chromium forced to valid IP | FAIL | HTTP 200 shell but blank rendered body. |
| Plain Chromium on `comix.ws` | FAIL | Blank body and secure-bundle JavaScript exception. |
| Repeated plain Chromium reloads | FAIL | Same exception on all three reloads. |
| Direct Camoufox, fresh profile, no login | PASS | Full page rendered, API responses were HTTP 200, and there were no page errors. |
| Popular/latest/search | PASS under Camoufox | Catalog and both positive and zero-result search states rendered. |
| Detail/group/chapter selection | PASS under Camoufox | Multiple scanlation groups and chapter variants were observable. |
| Reader page manifest | PASS under Camoufox | Reader reported full ordered page lists for two source variants. |
| Image download | PASS | Tested image responses were valid WebP. |
| Scramble detection and decode | PASS | Two `5x5`, algorithm-3 samples decoded into coherent pages. |
| Ten-page CBZ packaging fixture | PASS | Archive integrity, entry ordering, and image decoding all passed. |
| Existing `stealthB` wrapper | FAIL before navigation | Async Camoufox context manager was used before being entered. |

## 4. DNS and TLS evidence

The system resolver and public resolvers disagreed during the same observation:

```text
System DNS     comix.to -> 43.173.57.48
Cloudflare DNS comix.to -> 172.67.152.235, 104.21.49.220
Google DNS     comix.to -> 104.21.49.220, 172.67.152.235
```

The repeatable commands were:

```powershell
Resolve-DnsName comix.to -Type A
Resolve-DnsName comix.to -Type A -Server 1.1.1.1 -DnsOnly
Resolve-DnsName comix.to -Type A -Server 8.8.8.8 -DnsOnly
curl.exe --resolve comix.to:443:104.21.49.220 https://comix.to/title/dy88-the-novels-extra?group_id=9897
curl.exe --resolve comix.to:443:172.67.152.235 https://comix.to/title/dy88-the-novels-extra?group_id=9897
```

The system-resolved route produced timeout/connection failures and, in one
Windows TLS path, `SEC_E_UNTRUSTED_ROOT`. Both forced Cloudflare addresses
returned HTTP 200 and the expected HTML.

Confirmed fact: the local/system DNS answer was inconsistent with two public
resolvers and led to a broken connection path. Likely explanation: stale,
intercepted, or poisoned router/ISP DNS. The exact upstream actor is not proven.

No hosts-file override or system DNS mutation was made.

## 5. Browser and secure API evidence

The target has useful server-rendered data, but catalog and reader data are
completed dynamically. On ordinary Chromium/WebView-like execution, the page
shell returned HTTP 200 while the body remained blank. The exact page error on
`comix.ws` was:

```text
TypeError: Cannot read properties of undefined (reading '0')
at secure-tknvlr-DqJQnhrN.js:1:63078
```

Three reloads reproduced the same result. An `atob` bootstrap capture based on
the current extension approach observed none of the cipher material before the
secure bundle failed.

The live JavaScript used an `/api/v1` contract, including these observed route
families:

```text
/api/v1/manga
/api/v1/manga/{hid}/chapters
/api/v1/chapters/{id}
```

Signed GET requests add an `_` query parameter. The current upstream extension
also bootstraps cipher material inside a WebView and provides `getSigned` logic
in `Comix.kt`.

Direct Camoufox was then run with a fresh persistent profile, no proxy, and no
login. The same site fully rendered; its API requests returned HTTP 200 and no
page errors occurred. This establishes that login is not required for the
tested read path and that the failure is sensitive to browser/runtime integrity,
not a general Comix outage.

## 6. Catalog, title, source, and chapter evidence

The server-rendered target data identified:

```text
Internal ID        12947
HID                dy88
Title              The Novel's Extra
Latest chapter     170
First chapter URL  /title/dy88-the-novels-extra/7824171-chapter-0
Latest chapter URL /title/dy88-the-novels-extra/11296417-chapter-170
```

Eight group records were observed:

| Group ID | Display name |
|---:|---|
| 0 | Unknown group |
| 4725 | Asura Scans |
| 4743 | Demonic Scans |
| 820 | Flame Comics |
| 10728 | Hades Scans |
| 9897 | Official |
| 9413 | Reaper Scans |
| 9934 | Tapas |

This proves that the supplied `group_id=9897` selects **Official**.

Observed list counts:

- Popular browse reported 71,866 items.
- A fuzzy search for `The Novel's Extra` reported 386 results and included the
  exact target.
- A deliberately impossible query reported 0 items and the correct empty state.
- Official exposed 143 chapter items.
- Asura exposed 170 chapter items.
- The all-groups view exposed 902 raw chapter rows.
- Chapter 170 had at least Flame Comics, Demonic Scans, and Asura Scans
  variants.

Conclusion: `chapter number` alone is not a safe remote identity. A provisional
source identity must retain at least:

```text
title identity + chapter identity/number + group_id
```

Whether variants should be merged is a future, configurable product decision;
the downloader must not silently discard them.

## 7. Page and descramble evidence

Two reader variants were tested:

- Official chapter 143 reported 199 pages.
- Asura chapter 170 reported 122 pages.

Initial pages loaded as ordinary WebP. Scrolling the real reader container
(`MAIN.rpage-main.rpage-main--long-strip`) exposed scrambled responses carrying
headers such as:

```text
x-scramble-seed: 3641158735
x-scramble-grid: 5x5
x-scramble-algo: 3
x-scramble-hash: 03632
```

and:

```text
x-scramble-seed: 2349690766
x-scramble-grid: 5x5
x-scramble-algo: 3
x-scramble-hash: 53866
```

No `x-enc-*` response was encountered in the tested chapter. Absence in this
sample does not prove encrypted variants never occur.

The probe reproduced the current `Descrambler.kt` behavior:

- xorshift32 permutation generation;
- inverse tile permutation;
- a `5x5` grid;
- algorithm 3 processing;
- known hash offset handling and the current fallback behavior.

The raw `03632` sample was visibly tiled/scrambled and its output was coherent.
The newer/unlisted `53866` sample also produced a coherent, readable page using
the current fallback. This is a two-sample algorithm proof, not universal proof
for every historical or future Comix image variant.

## 8. CBZ pipeline fixture

A transient ten-page packaging fixture exercised the intended local-output
boundary:

1. Download nine normal reader images with `https://comix.ws/` as Referer.
2. Validate each payload from its image bytes.
3. Decode one scrambled page.
4. Name entries `001.webp` through `009.webp`, then `010.png`.
5. Write a ZIP-compatible `.cbz`.
6. Run archive integrity validation.
7. Re-open and decode every archived entry.

Result:

```text
Status        PASS
Page count    10
Archive size  2,470,186 bytes
Bad ZIP entry none
Page formats  9 WebP + 1 decoded PNG
```

This proves the small packaging seam only. It does not claim that a complete
122- or 199-page chapter was downloaded, retried, resumed, or atomically
published.

## 9. Suwayomi and APK interpretation

The extension is not simply “dead”:

- server-rendered title parsing remains useful;
- the signed API model corresponds to the live `/api/v1` site;
- group and page logic remain conceptually relevant;
- the descrambler successfully decoded current live samples.

However, importing the APK/Suwayomi flow directly would preserve two observed
failure surfaces:

1. the runtime may inherit the broken system DNS path for `comix.to`;
2. current secure JavaScript fails in the tested plain Chromium/WebView-like
   environment before cipher bootstrap completes.

Camoufox succeeds against the same logged-out target, so the reusable part is
the data contract and decode behavior—not Suwayomi's entire runtime or UI.

## 10. `stealthB` evidence

The existing `C:\VSCODE\YUZZENI\core\stealthB` wrapper failed before navigation:

```text
AttributeError: 'AsyncCamoufox' object has no attribute 'new_page'
```

Cleanup also encountered missing connection state. The adapter creates an
`AsyncCamoufox` context manager but tries to use it as an entered browser/context.
Direct Camoufox with equivalent options passed, isolating this as a wrapper
lifecycle defect rather than a Camoufox or Comix failure.

`stealthB` must not be described as a ready reusable browser library until this
contract is fixed and lifecycle tests exist. This record does not authorize
that fix.

## 11. Evidence-bounded conclusion

### Confirmed

- The supplied target is readable without login under direct Camoufox.
- The local DNS route for `comix.to` is broken and differs from public DNS.
- Plain Chromium/WebView-like execution hits a repeatable secure-bundle error.
- Search, detail, groups, chapter variants, page discovery, image download,
  descrambling, and small CBZ packaging are individually feasible.
- Source/group identity must be retained to avoid false chapter deduplication.
- Direct Camoufox works today; the current `stealthB` adapter does not.

### Inferred, not directly proven

- The DNS mismatch and secure-bundle/WebView incompatibility are the most likely
  explanation for the observed Suwayomi result-loading failure.
- A narrow Camoufox bootstrap broker plus a native local downloader is likely
  simpler and more robust for Citadel than embedding Suwayomi.

### Still unverified

- Full-chapter download, retry, resume, cancellation, and atomic publication.
- Every scramble/encryption algorithm and hash variant.
- Rate limits, Cloudflare challenges, domain rotation, and long-duration health.
- Cover/metadata normalization for a broad catalog sample.
- The final IPC, persistence, download queue, source-selection UI, and updater
  contracts.

## 12. Provisional implementation direction — not approved

If implementation is authorized later, the evidence supports this narrow seam:

```text
Comix adapter
  -> domain/DNS health selection
  -> short-lived Camoufox bootstrap and signed API session
  -> normalized title/group/chapter/page manifest
  -> streamed native downloads to temporary storage
  -> header-driven decode/descramble
  -> completeness validation
  -> atomic CBZ finalization into the local Citadel library
```

The browser should negotiate catalog/session/API state; it should not become a
streaming reader. Local CBZ output remains the product boundary. Domain health,
source selection, decoder variants, and Comix contract versioning should remain
separate features so a site change does not require rewriting Manga Reader.

