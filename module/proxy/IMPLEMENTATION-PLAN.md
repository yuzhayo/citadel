# Proxy Module Implementation Plan

**Target:** Transform empty `module/proxy` shell into production-ready proxy pool service for Citadel.

**Primary consumers:** MangaReader downloader, Camoprof (future).

**Date:** 2026-09-12

---

## 1. Problem Statement

### Current State
- MangaReader downloader uses direct HttpClient without proxy support
- IP banned by Cloudflare (comix.to) → permanent block on repeated requests
- No proxy rotation → single point of failure
- No shared proxy infrastructure → each module would duplicate logic

### Requirements
- Shared proxy pool accessible by all Citadel modules
- Automatic proxy rotation (round-robin default)
- Health tracking (mark dead proxies, skip on next rotation)
- Thread-safe (concurrent access from multiple sources/threads)
- File-based config (proxy.txt in workspace root)
- Fallback to direct connection if all proxies dead
- Observable/debuggable (UI for monitoring)

---

## 2. Architecture

### Component Hierarchy

```
module/proxy/
├── Core/
│   ├── IProxyProvider.cs          [Public API - exported via DI]
│   ├── ProxyPool.cs                [Main implementation]
│   ├── ProxyEntry.cs               [Data model per proxy]
│   ├── ProxyConfig.cs              [Configuration model]
│   ├── ProxyHealthStatus.cs        [Enum: Healthy, Dead, Unknown]
│   └── ProxyRotationStrategy.cs    [Enum: RoundRobin, Random, LeastUsed]
│
├── Features/
│   └── Management/
│       ├── ProxyManagementViewModel.cs  [UI state]
│       ├── ProxyManagementView.xaml     [Monitor/control UI]
│       └── ProxyManagementView.xaml.cs
│
├── ProxyModule.cs                  [Module entry, DI registration]
├── ProxyView.xaml                  [Main view - hosts Management feature]
└── ProxyView.xaml.cs
```

### Core Classes Detail

#### **IProxyProvider** (Public Interface)
```csharp
public interface IProxyProvider
{
    /// <summary>Get next proxy for use. Returns null if all dead and fallback disabled.</summary>
    ProxyEntry? GetNext();
    
    /// <summary>Mark proxy as dead (called by consumer on request failure).</summary>
    void MarkDead(ProxyEntry proxy);
    
    /// <summary>Mark proxy as healthy (called by consumer on successful request).</summary>
    void MarkHealthy(ProxyEntry proxy);
    
    /// <summary>Get current pool snapshot (for monitoring UI).</summary>
    IReadOnlyList<ProxyEntry> GetAll();
    
    /// <summary>Reload proxy list from file.</summary>
    void Reload();
}
```

#### **ProxyEntry** (Data Model)
```csharp
public sealed class ProxyEntry
{
    public string Url { get; init; }              // Full URL: socks5://host:port
    public string Protocol { get; init; }         // socks5, socks4, http, https
    public string Host { get; init; }             // IP or hostname
    public int Port { get; init; }
    public ProxyHealthStatus Status { get; set; } // Healthy, Dead, Unknown
    public DateTime LastUsed { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}
```

#### **ProxyPool** (Implementation)
```csharp
public sealed class ProxyPool : IProxyProvider
{
    private readonly ProxyConfig _config;
    private readonly List<ProxyEntry> _proxies;
    private readonly ReaderWriterLockSlim _lock;
    private int _currentIndex; // For round-robin
    
    // Load from file, parse, validate
    // Rotation logic (round-robin, random, least-used)
    // Health tracking
    // Thread-safe operations
}
```

#### **ProxyConfig** (Configuration)
```csharp
public sealed class ProxyConfig
{
    public string FilePath { get; init; }                      // Default: workspace_root/proxy.txt
    public ProxyRotationStrategy Strategy { get; init; }       // Default: RoundRobin
    public bool FallbackToDirectConnection { get; init; }      // Default: true
    public bool AutoReloadOnFileChange { get; init; }          // Default: false
    public TimeSpan DeadProxyRetryInterval { get; init; }      // Default: never (manual reload only)
}
```

---

## 3. File Format: proxy.txt

### Format
```
# Lines starting with # are comments
# Format: protocol://host:port
# Supported protocols: socks5, socks4, http, https

socks5://103.152.112.162:1080
socks5://198.27.74.6:9300
http://45.76.97.174:3128
https://185.34.20.35:8080

# Blank lines ignored
# Invalid lines logged as warning, skipped
```

### Validation Rules
- Must have protocol prefix (socks5://, http://, etc)
- Must have valid host (IP or hostname)
- Must have valid port (1-65535)
- Invalid entries → log warning, skip

### Default Location
`C:\VSCODE\citadel\proxy.txt` (workspace root, git-ignored)

---

## 4. Integration with MangaReader

### Step 1: Inject IProxyProvider
```csharp
// In DynamicManualSource.cs
public sealed class DynamicManualSource : IMangaSource
{
    private readonly HttpClient _client;
    private readonly IProxyProvider? _proxyProvider; // Nullable for backward compat
    
    // Constructor with DI
    public DynamicManualSource(IProxyProvider? proxyProvider = null)
    {
        _proxyProvider = proxyProvider;
        _client = CreateClient();
    }
    
    private HttpClient CreateClient()
    {
        var handler = CreateHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        // ... existing headers
        return client;
    }
    
    private SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        
        // Inject proxy if available
        if (_proxyProvider is not null)
        {
            var proxy = _proxyProvider.GetNext();
            if (proxy is not null)
            {
                handler.Proxy = new WebProxy(proxy.Url);
                handler.UseProxy = true;
            }
        }
        
        return handler;
    }
}
```

### Step 2: Retry Logic with Proxy Rotation
```csharp
private async Task<string> SendHtmlAsync(
    HttpRequestMessage request, 
    CancellationToken cancellationToken)
{
    const int maxRetries = 3;
    ProxyEntry? lastProxy = null;
    
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            // Get fresh proxy per attempt (rotation)
            if (_proxyProvider is not null && attempt > 0)
            {
                // Mark previous proxy as dead if retry
                if (lastProxy is not null)
                    _proxyProvider.MarkDead(lastProxy);
                
                // Recreate client with new proxy
                var newProxy = _proxyProvider.GetNext();
                if (newProxy is not null)
                {
                    lastProxy = newProxy;
                    // Recreate HttpClient with new proxy
                    // (or use HttpClientFactory pattern for better pooling)
                }
            }
            
            var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (LooksLikeChallenge(html))
            {
                // Mark proxy as potentially blocked
                if (lastProxy is not null)
                    _proxyProvider.MarkDead(lastProxy);
                
                throw new DynamicManualContractException(
                    "Cloudflare challenge detected - proxy may be blocked");
            }
            
            // Success - mark proxy healthy
            if (lastProxy is not null)
                _proxyProvider.MarkHealthy(lastProxy);
            
            return html;
        }
        catch (Exception ex) when (attempt < maxRetries - 1)
        {
            // Log and retry
            // Mark proxy dead handled above
        }
    }
    
    throw new DynamicManualContractException("All proxy attempts failed");
}
```

### Step 3: DI Registration in MangaReader Module
```csharp
// In MangaReaderModule.cs or similar bootstrap
public void ConfigureServices(IServiceCollection services)
{
    // Proxy provider from proxy module (if available)
    // This assumes proxy module exports IProxyProvider via DI container
    
    // Sources now get IProxyProvider injected
    services.AddSingleton<DynamicManualSource>(sp => 
        new DynamicManualSource(sp.GetService<IProxyProvider>()));
    
    // Same for other sources (CucumberManga, DrakeScans, etc)
}
```

---

## 5. UI: Proxy Management Screen

### Features
- **Proxy list view** (DataGrid)
  - URL, Protocol, Status (Healthy/Dead/Unknown)
  - Success/Failure count
  - Last used timestamp
  
- **Controls**
  - Reload button (re-read proxy.txt)
  - Clear dead button (remove all dead proxies from pool)
  - Test all button (background ping test)
  - Add proxy textbox + button (runtime add)

- **Stats panel**
  - Total proxies
  - Healthy count
  - Dead count
  - Current rotation strategy

### ViewModel
```csharp
public sealed class ProxyManagementViewModel : ObservableObject
{
    private readonly IProxyProvider _provider;
    
    public ObservableCollection<ProxyEntryViewModel> Proxies { get; }
    public int HealthyCount => Proxies.Count(p => p.Status == ProxyHealthStatus.Healthy);
    public int DeadCount => Proxies.Count(p => p.Status == ProxyHealthStatus.Dead);
    
    public ICommand ReloadCommand { get; }
    public ICommand ClearDeadCommand { get; }
    public ICommand TestAllCommand { get; }
    public ICommand AddProxyCommand { get; }
    
    // Refresh UI from provider every N seconds
    // Or subscribe to provider events if implemented
}
```

---

## 6. Implementation Steps

### Phase 1: Core Infrastructure (Day 1)
1. ✅ Create folder structure (Core/, Features/)
2. ⏳ Implement ProxyEntry, ProxyConfig models
3. ⏳ Implement IProxyProvider interface
4. ⏳ Implement ProxyPool with:
   - File parsing (proxy.txt)
   - Round-robin rotation
   - Basic health tracking (mark dead/healthy)
   - Thread-safe operations (ReaderWriterLockSlim)
5. ⏳ Unit tests (ProxyPoolTests.cs in tests/)

### Phase 2: MangaReader Integration (Day 1-2)
1. ⏳ Modify DynamicManualSource:
   - Add IProxyProvider constructor param
   - Inject proxy to SocketsHttpHandler
   - Add retry logic with proxy rotation
2. ⏳ Apply same pattern to other sources:
   - CucumberMangaSource
   - DrakeScansSource
   - (Others as needed)
3. ⏳ Update DI registration in MangaReader module
4. ⏳ Integration test: download chapter with proxy

### Phase 3: UI & Management (Day 2)
1. ⏳ Build ProxyManagementView XAML
2. ⏳ Implement ProxyManagementViewModel
3. ⏳ Wire up commands (Reload, ClearDead, etc)
4. ⏳ Update ProxyView.xaml to host Management feature
5. ⏳ Manual smoke test in Citadel app

### Phase 4: Polish & Documentation (Day 3)
1. ⏳ Error handling improvements
2. ⏳ Logging (structured logs for proxy operations)
3. ⏳ README.md in module/proxy/ (usage guide)
4. ⏳ Update CITADEL-STRUCTURE.md (proxy no longer "kerang kosong")
5. ⏳ Create sample proxy.txt with comments

---

## 7. Testing Strategy

### Unit Tests
```csharp
// tests/Module.Proxy.Tests/ProxyPoolTests.cs

[Fact]
public void GetNext_RoundRobin_ReturnsInOrder()
{
    var pool = CreatePoolWithProxies(["socks5://1.1.1.1:1080", "socks5://2.2.2.2:1080"]);
    
    var first = pool.GetNext();
    var second = pool.GetNext();
    var third = pool.GetNext(); // Wraps around
    
    Assert.Equal("socks5://1.1.1.1:1080", first.Url);
    Assert.Equal("socks5://2.2.2.2:1080", second.Url);
    Assert.Equal("socks5://1.1.1.1:1080", third.Url);
}

[Fact]
public void GetNext_SkipsDeadProxies()
{
    var pool = CreatePoolWithProxies(["socks5://1.1.1.1:1080", "socks5://2.2.2.2:1080"]);
    
    var first = pool.GetNext();
    pool.MarkDead(first);
    
    var second = pool.GetNext();
    var third = pool.GetNext();
    
    Assert.Equal("socks5://2.2.2.2:1080", second.Url);
    Assert.Equal("socks5://2.2.2.2:1080", third.Url); // Only healthy proxy
}

[Fact]
public void GetNext_AllDead_FallbackEnabled_ReturnsNull()
{
    var config = new ProxyConfig { FallbackToDirectConnection = true };
    var pool = CreatePoolWithProxies(["socks5://1.1.1.1:1080"], config);
    
    var proxy = pool.GetNext();
    pool.MarkDead(proxy);
    
    Assert.Null(pool.GetNext()); // Consumer will use direct connection
}
```

### Integration Test
```csharp
// tests/Module.Mangareader.Tests/DownloaderProxyIntegrationTests.cs

[Fact]
public async Task DynamicManualSource_WithProxy_DownloadsChapter()
{
    // Arrange
    var proxyProvider = CreateTestProxyProvider(); // Uses real proxy.txt
    var source = new DynamicManualSource(proxyProvider);
    var chapterUrl = new Uri("https://comix.to/comic/test-chapter");
    
    // Act
    var manifest = await source.GetChapterManifestAsync(
        CreateChapterIdentity(chapterUrl), 
        CancellationToken.None);
    
    // Assert
    Assert.NotNull(manifest);
    Assert.NotEmpty(manifest.Pages);
    
    // Verify proxy was used
    var usedProxies = proxyProvider.GetAll().Where(p => p.SuccessCount > 0);
    Assert.NotEmpty(usedProxies);
}
```

### Manual Test Procedure
1. Create `proxy.txt` with 3 proxies (1 valid, 2 invalid)
2. Launch Citadel, navigate to Proxy module
3. Verify UI shows 3 proxies, 2 marked as Unknown
4. Navigate to MangaReader, add Manual URL (comix.to chapter)
5. Download chapter → observe proxy rotation in logs
6. Check Proxy module UI → 1 proxy marked Healthy, 2 marked Dead
7. Download another chapter → verify only healthy proxy used

---

## 8. Edge Cases & Error Handling

### Proxy File Missing
- **Behavior:** Log warning, start with empty pool
- **Fallback:** Direct connection (if enabled in config)
- **UI:** Show warning banner "Proxy file not found"

### All Proxies Dead
- **Behavior:** Return null from GetNext()
- **Fallback:** Consumer uses direct connection
- **UI:** Show warning "All proxies unavailable"

### Invalid Proxy Format
- **Behavior:** Log warning per line, skip entry
- **Example:** `"bad-format"` → logged, not added to pool
- **UI:** Show count of skipped entries

### Concurrent Access
- **Protection:** ReaderWriterLockSlim for thread-safety
- **Read operations:** Multiple threads can GetNext() concurrently
- **Write operations:** MarkDead/MarkHealthy acquires write lock

### Proxy Timeout
- **HttpClient level:** SocketsHttpHandler.ConnectTimeout
- **Consumer level:** HttpClient.Timeout (45s default)
- **Behavior:** Timeout treated as proxy failure → marked dead, retry with new proxy

---

## 9. Configuration

### appsettings.json (Future)
```json
{
  "Proxy": {
    "FilePath": "proxy.txt",
    "Strategy": "RoundRobin",
    "FallbackToDirectConnection": true,
    "AutoReloadOnFileChange": false
  }
}
```

### Environment Variable Override
```
CITADEL_PROXY_FILE=C:\custom\path\proxies.txt
```

---

## 10. Performance Considerations

### HttpClient Pooling
- **Problem:** Creating new HttpClient per proxy = overhead
- **Solution:** Use HttpClientFactory or pool of HttpClients
- **Alternative:** Reuse SocketsHttpHandler, only swap Proxy property (test viability)

### File Watching
- **AutoReloadOnFileChange:** FileSystemWatcher on proxy.txt
- **Debouncing:** Wait 500ms after file change before reload (avoid partial writes)
- **Optional:** Disabled by default (manual reload via UI)

### Memory
- **Proxy count:** Expected ~100-500 entries
- **Per entry:** ~200 bytes (URL, stats)
- **Total:** ~100KB max → negligible

---

## 11. Future Enhancements

### Automatic Proxy Testing
- Background thread pings proxies every N minutes
- HTTP HEAD request to test URL (configurable)
- Auto-mark dead/healthy based on response

### Rotation Strategies
- **Random:** Pick random healthy proxy (load balancing)
- **Least-used:** Track usage count, prefer least-used
- **Weighted:** Assign priority/weight to proxies

### Proxy Authentication
- Support user:pass@ in URL: `socks5://user:pass@host:port`
- Parse credentials, inject into SocketsHttpHandler.Credentials

### External Proxy Sources
- Fetch from API (ProxyScrape, etc) on-demand
- Merge with local proxy.txt
- Auto-refresh every N hours

### Per-Module Proxy Pools
- MangaReader uses pool A (fast proxies)
- Camoprof uses pool B (residential proxies)
- Shared infrastructure, separate pools

---

## 12. Success Criteria

### Functional
- ✅ Load proxies from proxy.txt
- ✅ Rotate proxies (round-robin)
- ✅ Skip dead proxies
- ✅ Fallback to direct connection
- ✅ MangaReader downloads chapters using proxy
- ✅ Cloudflare block bypassed with proxy rotation

### Quality
- ✅ Thread-safe (no race conditions)
- ✅ Unit test coverage >80%
- ✅ Integration test passes
- ✅ No memory leaks (dispose HttpClient properly)
- ✅ Logs proxy operations (info/warning level)

### User Experience
- ✅ UI shows proxy status in real-time
- ✅ Manual reload works
- ✅ Clear visual feedback (healthy/dead colors)
- ✅ Zero-config default (works without proxy.txt if fallback enabled)

---

## 13. Dependencies

### NuGet Packages
- None required (uses built-in System.Net.Http)
- Optional: Microsoft.Extensions.Http (for IHttpClientFactory if used)

### Citadel Core
- `Citadel.Core.Modules` (IModule interface)
- `Citadel.Core.Rpl` (Lifetime, if needed)
- Standard WPF (System.Windows, System.Xaml)

### External
- `proxy.txt` file (user-provided or generated by proxy_manager.py)

---

## 14. Rollout Plan

### Phase 1: Proxy Module Only (Isolated)
- Build Core infrastructure
- Build UI
- Manual testing in Proxy module
- **No impact on existing MangaReader** (proxy disabled)

### Phase 2: MangaReader Opt-In
- Add IProxyProvider? param (nullable)
- Default to null (existing behavior)
- Enable via config flag: `"MangaReader:UseProxy": true`
- **Gradual rollout:** Users opt-in

### Phase 3: Default Enabled
- After 1 week of stable operation
- Change default to UseProxy = true
- Users can disable via config if issues

---

## 15. Acceptance Test Scenario

**Scenario:** User downloads manga chapter from blocked site using proxy rotation.

**Steps:**
1. User blocked by Cloudflare on comix.to (verified via screenshot)
2. User creates `proxy.txt` with 5 proxies (3 valid, 2 invalid)
3. User launches Citadel, navigates to Proxy module
4. UI shows 5 proxies loaded, all status Unknown
5. User navigates to MangaReader → Manual URL
6. User pastes comix.to chapter URL, clicks Download
7. **Expected:** 
   - First 2 proxies fail → marked Dead
   - Third proxy succeeds → marked Healthy
   - Chapter downloads successfully
   - Logs show proxy rotation
8. User downloads another chapter from same site
9. **Expected:**
   - Only healthy proxies used (skip dead ones)
   - Download succeeds immediately
10. User returns to Proxy module
11. **Expected:**
    - 1 proxy Healthy (green)
    - 2 proxies Dead (red)
    - 2 proxies Unknown (gray, not tested yet)
    - Success/failure counts accurate

**Result:** ✅ Pass if all expected outcomes match.

---

**End of Implementation Plan**

**Next Action:** Begin Phase 1 implementation (Core infrastructure).
