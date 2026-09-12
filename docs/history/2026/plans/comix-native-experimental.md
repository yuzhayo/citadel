# PLAN: Comix Native Experimental Provider

Status: implementation-ready, additive-only
Decision date: 2026-09-06
Repository: `C:\VSCODE\citadel`
Owner: `module/mangareader/Features/Downloader/Sources/ComixNative/`

## 1. Goal

Add a second selectable provider named **Comix Native (Experimental)** that
implements the existing `IMangaSource` contract using this fixed transport
order:

```text
explicit user request
  -> one Camoufox bootstrap for runtime cipher material + cookies + user agent
  -> native signed HTTP request + native response decryption
  -> exactly one cipher refresh and native retry when the runtime material fails
  -> existing page-context Axios request as the final fallback
```

The existing provider named **Comix** remains the stable baseline and is not
edited, replaced, renamed, deleted, or made dependent on the experiment.

The experiment is successful only when:

1. both `Comix` and `Comix Native (Experimental)` appear in the provider
   dropdown, with legacy `Comix` still first/default;
2. warm Catalog, Detail, Group, Chapter and Manifest requests from the new
   provider run through native HTTP without another browser page-context API
   call;
3. protected page bytes are decoded into valid images before CBZ publication;
4. failure remains local to the experimental provider and never crashes
   MangaReader;
5. deleting the experimental folder and its single registry entry restores the
   product to its previous provider set without a migration.

Cold bootstrap may remain similar to, or slower than, legacy Comix. The
performance target is the **second and subsequent request in the same provider
session**, not an invented promise for first launch.

## 2. Locked product decisions

These decisions are final for this implementation. The execution agent must not
ask for or invent alternatives midway.

| Decision | Locked value |
|---|---|
| Existing provider | unchanged `Id = "comix"`, `DisplayName = "Comix"` |
| New provider ID | `comix-native` |
| New display name | `Comix Native (Experimental)` |
| Registry order | legacy Comix first, experimental provider second |
| Activation | only an explicit existing Catalog/Detail/Queue action may start work |
| Browser role | cipher/session bootstrap and final API/page fallback only |
| Native role | signer, API transport, encrypted JSON decode and page decode |
| Cipher persistence | memory-only; never written to disk, queue, logs or UI |
| Browser profile | existing Downloader profile root under provider ID `comix-native` |
| Filters | reuse the existing Comix filter UI and query values through a feature-local adapter |
| UI | no new screen, XAML, primitive, style or Catalog layout change |
| Cache | no new persistent catalog/query cache in this feature |
| Concurrency | preserve existing Downloader concurrency; add no request pool or worker queue |
| Fallback | one cipher refresh/native retry, then one existing Axios fallback |
| Dependencies | no new NuGet, Python package, database or service |
| Version | final additive feature bump: `2.0.11 -> 2.1.0`; if preflight finds a newer version, use the next minor `<major>.<minor+1>.0` without decrementing it |

## 3. Mandatory skills and execution protocol

Before touching code, the execution agent must read these files completely and
state their activation in its first progress message:

1. `.agents/skills/citadel-shared-ui/SKILL.md` — mandatory for every Citadel
   task; proves that this plan needs no new UI primitive.
2. `.agents/skills/citadel-feature-modularity/SKILL.md` — the new provider owns
   its state and behavior; parents remain routers.
3. `.agents/skills/planning-and-task-breakdown/SKILL.md` — execute the vertical
   slices and gates below in order.
4. `.agents/skills/api-and-interface-design/SKILL.md` — preserve
   `IMangaSource`; external responses are untrusted.
5. `.agents/skills/incremental-implementation/SKILL.md` — keep each slice
   buildable and rollback-friendly.
6. `.agents/skills/test-driven-development/SKILL.md` — focused RED/GREEN tests
   for signer, transport/fallback and decoder only.
7. `.agents/skills/git-workflow-and-versioning/SKILL.md` — inspect scoped diffs
   and bump version once at completion. It does not authorize commit/push.
8. `.agents/skills/mengquality/SKILL.md` — evidence-first, feature ownership and
   smallest safe change.

The agent must not substitute its remembered version of these skills. Repository
files are authoritative.

### Context7 / external documentation

No new library is planned, so Context7 is **not required** for ordinary
implementation. If an older-cutoff model is unsure about the installed .NET 10
`HttpClient`, `HttpRequestMessage`, `CookieContainer` or response-header APIs,
it may use Context7 only for the matching official Microsoft documentation.

Context7 is not the source of truth for Comix. Use the pinned upstream source
below and the live Comix response as evidence:

- keiyoushi `extensions-source` main observed at
  `064c1a0e8c58be4b36c21444c3d3df6840636950`;
- `src/en/comix/.../Comix.kt` for native-first/fallback and canonical query;
- `src/en/comix/.../Cipher.kt` for signing/decryption behavior;
- `src/en/comix/.../Descrambler.kt` for byte XOR and tile restoration;
- upstream license: Apache-2.0.

Do not follow future upstream changes silently during this task. If live Comix
differs, fail visibly within the experimental provider; do not change legacy
Comix to compensate.

## 4. Preflight — mandatory, read-only

Run this before implementation:

1. Confirm the real root with `git -C C:\VSCODE\citadel rev-parse --show-toplevel`.
2. Read root `AGENTS.md`, this plan, `docs/history/2026/plans/mangareader-downloader.md`, and
   `docs/history/2026/specifications/mangareader-library-catalog-features.md`.
3. Read the current implementations of:
   - `shareLogic/Sources/MangaSourceContracts.cs`;
   - `Sources/MangaSourceRegistry.cs`;
   - `Sources/Comix/ComixSource.cs` and `ComixPageDecoder.cs`;
   - `DownloaderPyHostClient.cs`;
   - `mangareader_downloader/{plugin.py,browser.py}`;
   - `Queue/{PageTransport.cs,ChapterDownloadPipeline.cs}`;
   - the Downloader test project and neighboring Comix tests.
4. Record `git status --short --branch`, `git rev-parse HEAD`, and the current
   version. Never require a clean worktree.
5. Inspect the unstaged diff of every existing allowlisted file before patching.
   Preserve those changes as the working baseline.
6. Run the focused Downloader test project once to establish the baseline. If
   it already fails, record the exact pre-existing failure and continue only
   with tests that can distinguish the new feature from that failure.
7. Build MangaReader Release once. Do not repair unrelated baseline failures.
8. Run one live legacy Comix smoke if tooling allows: Start -> results -> Detail
   -> chapters. This records the baseline; it does not authorize changes to it.

Planning snapshot, which must be rechecked rather than assumed current:

- HEAD was `696d56f89d6fddc1ae4454a5b49aa57f62f81fab`;
- branch was `main`, ahead of `origin/main` by two commits;
- version was `2.0.11`;
- the worktree contained many modified/untracked Downloader, Library and History
  files, including existing changes in the registry, contracts, queue pipeline,
  test project and version file.

Forbidden preflight actions: `git clean`, reset, checkout/restore of user files,
broad formatting, broad staging, moving to another checkout, or deleting any
untracked file.

## 5. Ownership and dependency direction

```text
Catalog / Queue / Update Checker
           |
           v
      IMangaSource
           |
           v
   ComixNativeSource
     |       |       |
     v       v       v
 Bootstrap  Native   Page decoder
 adapter    HTTP
     |
     v
 DownloaderPyHostClient -> existing pyhost plugin -> Camoufox
```

Rules:

- `ComixNativeSource` owns orchestration, cipher invalidation and fallback.
- `ComixNativeCipher` owns only deterministic sign/decrypt operations.
- `ComixNativeHttpClient` owns only bounded HTTP request/response handling.
- `ComixNativeBootstrapClient` owns the feature command/payload and validates
  the Python result.
- Python `comix_native.py` owns only browser extraction of transient material.
- `MangaSourceRegistry` adds one entry and contains no provider behavior.
- Catalog and Downloader parents remain unaware of ComixNative types.
- The optional response-header transform interface remains provider-neutral;
  Queue must never switch on `comix-native` or instantiate a Comix decoder.
- No feature may call legacy `ComixSource` instance methods. Reuse is limited to
  its already-public/internal immutable query/options/parser helpers without
  editing them.

## 6. File boundary

### 6.1 New production files allowed

Only this cohesive folder may be added on the C# side:

```text
module/mangareader/Features/Downloader/Sources/ComixNative/
|- ComixNativeSource.cs
|- ComixNativeFilterContribution.cs
|- ComixNativeBootstrapClient.cs
|- ComixNativeCipher.cs
|- ComixNativeHttpClient.cs
|- ComixNativePageDecoder.cs
|- LICENSE.keiyoushi.txt
`- NOTICE.md
```

One provider-local Python adapter may be added:

```text
module/mangareader/Features/Downloader/mangareader_downloader/comix_native.py
```

The six C# files are an upper bound, not a target. Combine small DTOs with their
owner; do not create folders for one file or speculative interfaces.

### 6.2 Existing production files allowed to receive surgical additive edits

| File | Only allowed edit |
|---|---|
| `Sources/MangaSourceRegistry.cs` | instantiate ComixNative and append one registration after Comix |
| `DownloaderPyHostClient.cs` | expose one internal provider-neutral session-command seam and add defaulted response-header evidence for browser page fetch; no Comix-specific method |
| `mangareader_downloader/plugin.py` | import/register exactly `downloader.comix_native.bootstrap` |
| `mangareader_downloader/browser.py` | include only the allowlisted scramble/encryption response headers in existing `downloader.fetch` evidence |
| `shareLogic/Sources/MangaSourceContracts.cs` | add one optional provider-neutral response-header page-transform interface; do not change existing `IMangaSource` members |
| `Queue/PageTransport.cs` | retain allowlisted response headers and allow an explicitly transformed page to reach its provider before final format validation |
| `Queue/ChapterDownloadPipeline.cs` | call the optional response-aware transform when implemented; otherwise execute the existing path unchanged |
| `version.props` | one final minor-version bump only after all gates pass |

The existing constructors of `PageFetchResult` and `BrowserFetchEvidence` must
remain source-compatible: add response headers as defaulted properties rather
than new required positional arguments.

### 6.3 Test files allowed

```text
tests/Module.Mangareader.Downloader.Tests/
|- ComixNativeCipherTests.cs
|- ComixNativeTransportTests.cs
|- ComixNativePageDecoderTests.cs
`- ComixNativeRegistryTests.cs
```

The existing test `.csproj` may receive only links for the new ComixNative
source files. Existing test files may be edited only if an additive defaulted
property still causes a compile error; do not rewrite legacy assertions.

Maximum: four new test files. Do not create a new test project, architecture
test suite, snapshot corpus, benchmark project, mock framework or live-network
unit suite.

### 6.4 Explicitly forbidden files/areas

- every file under `Sources/Comix/`;
- `CatalogScreen.xaml(.cs)`, `CatalogFeature.cs` and all Catalog card/detail UI;
- `DownloaderView.xaml(.cs)` and `MangaReaderView.xaml(.cs)`;
- `setting/Components/` and all shared UI resources;
- Library, History, Cover Builder, AutoCover, Lister and Update Checker;
- queue models/store/state machine, CBZ publisher, naming and publication rules;
- `module/sharedLogic/cs/PyHost.cs`, shared Python pyhost core and CamoProf;
- solution structure, citizen build targets and package dependencies;
- current Downloader plan/spec except this new plan document.

If a forbidden file seems necessary, the implementation is wrong for this
plan. Do not edit it; use the allowed seam or report the exact blocker.

## 7. Locked contracts

### 7.1 Filter reuse

`ComixNativeFilterContribution` composes the existing
`ComixFilterContribution`; it adds no XAML. It wraps the produced
`ComixBrowseQuery` in a provider-local filter whose `SourceId` is
`comix-native`. All option IDs, validation and lookup UI stay owned by the
existing Comix filter.

The new source unwraps only that provider-local wrapper. It must reject another
provider's filter rather than guessing.

### 7.2 Bootstrap command

Command name: `downloader.comix_native.bootstrap`.

Input is the current Downloader session ID plus the existing bounded timeout.
The handler:

1. resolves the existing `comix-native` session/page;
2. installs an init script that wraps `window.atob` before application modules
   execute;
3. reloads the already-open `/browse` page once;
4. captures exactly three 256-byte S-box arrays and three key arrays whose
   lengths are 24 or 32 bytes;
5. returns those arrays plus same-origin cookies and current user agent;
6. removes no page/session and writes no material to disk/logs.

The C# adapter validates counts, lengths and byte ranges. Invalid or incomplete
material is a local `ComixNativeContractException`.

### 7.3 Sign/decrypt

Port the behavior, not the Android framework:

- canonical parameters are sorted by raw key;
- repeated or `[]` parameters are signed as indexed names (`name[0]`,
  `name[1]`); a single non-array keeps its name;
- signature input is the API path without `/api/v1`, followed by `?query` only
  when query is non-empty;
- the three substitution rounds use the captured S-box/key material and the
  fixed previous-byte values `189`, `133`, `32`;
- `_` is URL-safe base64 without padding;
- encrypted `{ "e": "..." }` responses are reversed through the three rounds
  and decoded as UTF-8 JSON;
- unencrypted JSON remains valid and is parsed without a fake envelope.

Every external JSON shape is validated before normalization.

### 7.4 Native HTTP and fallback

Use the framework `HttpClient`; add no dependency. Each native request carries
only evidence obtained from bootstrap or existing Comix constants:

- captured User-Agent;
- `Accept: */*`;
- same-origin cookies;
- Comix Referer;
- signed `_` query parameter.

Do not invent browser fingerprint headers. Use the existing 45-second API bound
and no new parallel requests.

One logical call has this terminal sequence:

```text
no material -> bootstrap
native request succeeds -> return normalized payload
signature/decrypt/401/403 failure -> invalidate -> bootstrap once -> native retry once
retry still fails -> existing DownloaderPyHostClient.ApiAsync once
fallback fails -> return one provider-local error
```

Never retry 404, invalid input or locally invalid filter as a bootstrap problem.

### 7.5 Provider operations

Implement all existing `IMangaSource` methods before registry exposure:

- Browse;
- Author/Artist lookup;
- Title detail;
- Groups;
- Chapters with the existing one-based bounded pagination;
- Manifest;
- Page transform;
- alternate group lookup.

Reuse existing immutable `ComixContract`, `ComixOptions`, query serialization
and internal parser helpers where they already provide the exact behavior.
Normalize every returned identity to `SourceId = "comix-native"`; never leak
legacy `comix` identity into the experimental queue or source binding.

Do not copy the complete legacy `ComixSource.cs`. The new source should contain
transport orchestration and thin identity adaptation, not a second thousand-line
parser.

### 7.6 Protected page handling

The manifest marks only provider-declared protected pages with a non-null
`RemotePageTransform`. Generic transport may pass non-image bytes onward only
for such a page and only when the payload is not HTML/text. Final decoded bytes
must still pass the existing image-signature validation before publication.

The optional response-aware transform receives an allowlisted dictionary of:

- `x-scramble-seed`, `x-scramble-grid`, `x-scramble-algo`,
  `x-scramble-hash`;
- `x-enc-seed`, `x-enc-algo`, `x-enc-len`.

No cookie, token or arbitrary response header is persisted in this dictionary.

`ComixNativePageDecoder` performs:

1. legacy byte XOR based on declared `x-enc-*` values;
2. 5x5 tile restoration based on declared `x-scramble-*` values;
3. reuse of the existing `ComixPageDecoder` for its already-supported grid
   operation where compatible;
4. final byte-signature validation.

Unknown declared algorithms fail that page visibly. They must not publish
scrambled/unknown bytes and must not change legacy Comix behavior.

## 8. Implementation sequence

Execute sequentially. Do not parallelize edits because the current worktree has
overlapping uncommitted changes.

### Phase 1 — Cipher and bootstrap proof, not registered

Create the cipher, bootstrap C# adapter and Python handler. Add the smallest
generic internal session-command seam and one plugin registration.

TDD gate:

- a fixed independent vector proves signature output;
- a fixed encrypted vector proves UTF-8 JSON decryption;
- invalid material and malformed encrypted payload fail locally;
- Python bootstrap payload shape is validated by C#.

Acceptance:

- no provider is added to the dropdown yet;
- legacy Comix code and behavior are untouched;
- focused cipher/transport tests pass;
- MangaReader builds.

### Phase 2 — Native Catalog vertical slice, still not registered

Create `ComixNativeHttpClient`, provider-local filter adapter and the minimal
`ComixNativeSource` Browse path. Use the current Comix filter/query/parser
helpers and rewrite returned identity to `comix-native`.

Test native success, exactly one refresh/retry, final Axios fallback and terminal
failure. Tests use a fake `HttpMessageHandler`/command seam; no live network.

Acceptance:

- native Browse returns the same normalized contract as legacy Browse fixtures;
- a successful warm Browse performs no page-context API call;
- one failure cannot loop bootstrap or fallback;
- legacy Comix tests remain green.

### Phase 3 — Detail, groups, chapters and manifest

Complete the remaining read operations and alternate-group composition using the
same native transport. Preserve existing page bounds, ordering, identity and
provider response semantics.

Acceptance:

- all `IMangaSource` members have real implementations;
- no `NotImplementedException` or placeholder path remains;
- detail/group/chapter/manifest identities all use `comix-native`;
- no Queue, Catalog or Update Checker type depends on ComixNative.

### Phase 4 — Response-aware page decoding

Add the optional neutral transform interface, response-header evidence, and
decoder. Preserve the old `IMangaSource.TransformPageAsync` route as the default
for legacy providers.

TDD gate:

- ordinary image with no transform follows the old path byte-for-byte;
- transformed XOR fixture becomes a valid image;
- scrambled 5x5 fixture restores expected pixels;
- HTML, missing required headers and unknown algorithms cannot publish;
- response evidence contains only the allowlisted headers.

Acceptance:

- existing Comix page tests pass without editing its source;
- the new decoder receives headers without Queue knowing its provider type;
- existing CBZ atomic publication behavior is unchanged.

### Phase 5 — Register and expose

Only after Phases 1-4 are green, append the ComixNative registration after
legacy Comix. Reuse the existing filter UI through the provider-local adapter.

Acceptance:

- dropdown shows exactly the two expected Comix entries in locked order;
- opening Downloader or switching provider performs no request;
- selection of either provider keeps its own source identity;
- no screen/XAML change is needed.

### Phase 6 — Validation, version and local handoff

1. Run focused ComixNative tests.
2. Run the complete existing Downloader test project once.
3. Build MangaReader Release.
4. Run `git diff --check` on the allowlist.
5. Inspect `git diff --stat` and every changed path; remove only accidental files
   created by this task.
6. Confirm forbidden paths have no new diff attributable to this task.
7. Perform the live smoke matrix below.
8. Bump the version once using the deterministic minor-version rule in section 2.
9. Rebuild Release once after the version change.

Do not build an installer, install/update the machine copy, commit, tag, push or
publish a GitHub release unless the execution prompt separately authorizes it.

## 9. Proportional validation matrix

### Automated

| Gate | Required evidence |
|---|---|
| Cipher | fixed sign vector and fixed encrypted-response vector pass |
| Retry | one refresh/native retry maximum, one Axios fallback maximum |
| Contract | all normalized identities are `comix-native` |
| Decoder | XOR and grid fixtures produce recognized images |
| Isolation | legacy Comix source files have zero diff from this task |
| Registry | legacy first, experimental second; duplicate IDs still rejected |
| Regression | Downloader test project passes |
| Build | MangaReader Release: zero errors; report warnings honestly |

Do not add tests for .NET, WPF, `HttpClient`, JSON library or registry framework
behavior. Do not run the full repository suite unless an allowed shared contract
change produces evidence that Downloader-only coverage is insufficient.

### Live operator smoke

Use public/logged-out Comix and a disposable download target, never the user's
main manga collection.

1. Legacy `Comix`: Start -> results -> Detail -> chapter list still works.
2. Select `Comix Native (Experimental)`: switching alone causes no request.
3. First Start performs one bootstrap and returns catalog results.
4. Second Start/filter request in the same session returns results without a new
   bootstrap/page-context API call; record cold and two warm elapsed times.
5. Detail -> group -> chapters loads and selection works.
6. Queue one small public chapter; completed CBZ opens and every page is a valid,
   correctly ordered image.
7. Switch back to legacy Comix and repeat Start successfully.
8. Restart: cipher is absent by design, so the experimental provider bootstraps
   again only after explicit Start.

Build/test success is not visual/runtime proof. If live smoke is unavailable,
report `PARTIAL — live provider verification pending`, never PASS.

## 10. Scope and size guard

This is one experimental provider, not a platform rewrite.

Forbidden additions include:

- provider discovery/reflection or a new plugin framework;
- PyHost v2, cancellation protocol, worker queue or event bus;
- multiple browser processes or parallel API fan-out;
- catalog/query database, cache service or background prewarm;
- new settings, feature flags, dialogs or screens;
- migration of existing Comix jobs/source bindings;
- copied Comix XAML or duplicated shared controls;
- speculative provider-neutral crypto abstractions;
- broad refactor, rename, formatting or cleanup;
- dozens of generated fixtures/tests.

If production implementation grows beyond the six new C# files, one new Python
file, and eight surgical existing-file edits listed above, treat that as a scope
violation and simplify back to this plan. Do not expand the allowlist silently.

## 11. Failure and rollback behavior

- All runtime errors are labeled as `Comix Native` and stay in existing local
  loading/error state.
- Native failure never mutates the legacy Comix session/state.
- No runtime material is durable, so restart is a clean reset.
- No schema/migration is introduced.
- A failed download remains subject to existing Queue retry/publication rules;
  original archives are unchanged until atomic publication succeeds.

Rollback is deterministic:

1. remove the `comix-native` registry entry;
2. remove `Sources/ComixNative/` and `mangareader_downloader/comix_native.py`;
3. remove the single Python command registration;
4. revert only the additive generic session/header-transform seams introduced by
   this plan if nothing else uses them;
5. remove only tests and license notice belonging to ComixNative;
6. rebuild; legacy Comix remains available first/default.

Before rollback, clear experimental pending jobs from the Download List because
their provider ID intentionally becomes unresolved after provider removal.

## 12. Agent handoff report

The execution agent's final report must contain:

1. verdict: PASS, PARTIAL or FAIL;
2. exact changed/new/deleted files;
3. confirmation that `Sources/Comix/` has no task-attributable diff;
4. focused tests, full Downloader test result and MangaReader build result;
5. live legacy/new-provider smoke result and measured cold/warm timings;
6. version before/after;
7. browser QA status;
8. any pre-existing failures or dirty files explicitly left untouched;
9. statement that no commit/push/release occurred unless separately authorized.

## 13. Definition of done

Done means all of the following, not merely compilation:

- legacy Comix remains intact and usable;
- new provider is selectable and lazy;
- native signer/decryptor works from transient browser-derived material;
- all source operations work end to end with bounded fallback;
- protected pages decode and publish into a valid disposable CBZ;
- no forbidden file or dependency was added;
- proportional automated gates pass;
- live smoke passes, or the result is honestly marked PARTIAL;
- version is bumped exactly once at final integration;
- the whole experiment remains removable by the rollback steps above.
