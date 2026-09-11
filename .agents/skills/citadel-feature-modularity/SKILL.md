---
name: citadel-feature-modularity
description: Plan, review, or implement modular features in Citadel while preserving plug-and-play architecture. Governs TWO levels — core↔module (machine-enforced) and module↔feature / feature↔feature (this skill, enforced by a mandatory per-citizen guard test). Use for any task that adds, modifies, or refactors feature logic within a module (like Manga Reader, token farming modules, etc.).
---

# Citadel Feature Modularity Contract

## Core Principle: Parent = Dumb Router, Features = Smart Workers

Think of the parent like a power strip—it provides sockets and passes electricity, but it doesn't know or care what you plug into it. Each device (feature) is self-contained and just works when plugged in.

**In code terms:**
- Parent knows features *exist* and *where they live*
- Parent does NOT know feature internals, data structures, or dependencies
- Adding/removing a feature touches only that feature's folder, not parent code

---

## Two Levels, One Contract

This contract governs **two** boundaries. They fail differently and are enforced
differently; both are mandatory.

### Level A — core ↔ module (the citizen boundary)

A citizen (`module/<name>/`) may reference only `Citadel.Core`, `Citadel.Contract`,
and `Citadel.Setting`; never `Citadel.Shell`, `Citadel.Ui`, and never another
citizen. This level is **machine-enforced**: `.agents/hooks/check-project-refs.mjs`
blocks disallowed `ProjectReference` edges, and `module/Citizen.targets`
(`VerifyCitizenIsolation`) fails the build if a shared `Citadel.*.dll` lands inside
a citizen folder. You cannot violate Level A silently.

### Level B — module ↔ feature, and feature ↔ feature

Inside one citizen, the parent (`*View.xaml.cs` / composition root) and its
`Features/*` obey Rules 1–5 below — **and features obey them toward each other**:
no feature imports another feature's internal types, reaches into another feature's
mutable state, or routes sibling logic through the parent. Cross-feature needs go
through the module's shared contracts, shared context, or hubs, or through a
feature's public contract. The same holds for a module's shared folder
(`shareLogic/`): it holds mechanisms and contracts, not one feature's internals,
and it must not import a feature (a shared→feature import creates a cycle).

Level B is **invisible to every project-level hook**, because it lives inside a
single assembly. Therefore **every citizen must carry a guard test** — grep/source-
based, in the style of `module/sharedLogic/tests/test_add_profile_architecture.py`
— that fails the build when any of these is violated:

1. no feature-internal type is referenced outside its own folder;
2. no feature → feature concrete-type import (contracts/hubs only);
3. the parent references feature **contracts** only, never internals;
4. the module shared folder imports no feature;
5. adding a feature touches only that feature's folder plus its one catalog line.

A citizen without this guard test is **non-compliant with this contract even if its
code currently looks clean**: Level B rots silently without it, and silent rot at
this level is what produces wrong-`using` defects that compile, pass tests, and
only surface as dead behavior in production.

---

## Architecture Rules

### Rule 1: Features Are Folders With Contracts

Each feature lives in its own folder with a clear entry point:

```
module/mymodule/Features/
├── TokenFarmer/
│   ├── TokenFarmerFeature.cs       ← contract/interface parent talks to
│   ├── FarmingEngine.cs            ← internal logic parent never touches
│   └── TokenStorage.cs             ← internal state parent never sees
└── AccountManager/
    ├── AccountManagerFeature.cs    ← another feature, same pattern
    └── [internals]/
```

**Parent only knows the feature contract exists.** It never imports or references internal implementation files (`FarmingEngine`, `TokenStorage`, etc.).

**Good structure:**
- Feature entry point implements `IFeature` or similar interface
- Internal logic stays in the feature folder
- Feature folder is self-contained (can be deleted without touching parent)

**Bad structure:**
- Parent knows about `FarmingEngine.StartAsync()`
- Parent passes feature-specific config directly
- Removing feature requires editing parent code

---

### Rule 2: Communication Through Events, Not Direct Calls

Features talk to parent and siblings through messages/events, not by calling each other's methods directly.

**Bad (tight coupling):**
```cs
// Parent knows too much about feature internals
if (tokenFarmer.IsRunning && accountManager.HasActiveSession()) {
    tokenFarmer.SetProxy(accountManager.GetCurrentProxy());
}
```

**Good (loose coupling):**
```cs
// Parent just routes messages
accountManager.SessionStarted += (proxy) => {
    eventBus.Publish(new ProxyAvailable(proxy));
};

// TokenFarmer listens independently, parent doesn't orchestrate
```

**Pattern:**
- Features publish events/commands to a hub
- Other features subscribe if they care
- Parent routes but doesn't interpret

---

### Rule 3: Parent Never Checks "Does Feature X Exist"

No `if (hasTokenFarmer) { ... }` logic in parent. Features register themselves on startup; parent provides the plug, features plug in.

**Bad:**
```cs
// Parent aware of specific features
if (modules.Contains("TokenFarmer")) {
    InitializeTokenFarmer();
}
if (modules.Contains("AccountManager")) {
    InitializeAccountManager();
}
```

**Good:**
```cs
// Parent provides registration, features register themselves
foreach (var feature in discoveredFeatures) {
    feature.Initialize(this.serviceContainer);
}
```

**Use a catalog pattern:**
```cs
// FeatureCatalog.cs
var catalog = new FeatureCatalog()
    .Add("TokenFarmer", () => new TokenFarmerFeature())
    .Add("AccountManager", () => new AccountManagerFeature());

// Parent just loads the catalog
foreach (var entry in catalog.Entries) {
    var feature = entry.Factory();
    feature.Attach(context);
}
```

---

### Rule 4: Shared State Through a Container, Not Parent Fields

When features need shared resources (database, HTTP client, config), parent provides a service container. Features pull what they need; parent doesn't hand-deliver to each feature.

**Bad:**
```cs
// Parent managing each feature's dependencies
tokenFarmer.SetDatabase(db);
tokenFarmer.SetHttpClient(http);
accountManager.SetDatabase(db);
accountManager.SetHttpClient(http);
```

**Good:**
```cs
// Parent provides container, features self-serve
var services = new ServiceContainer();
services.Register(db);
services.Register(http);
services.Register(config);

tokenFarmer.Initialize(services);      // pulls what it needs internally
accountManager.Initialize(services);   // same pattern
```

**Or use a context object:**
```cs
var context = new FeatureContext(
    State: sharedState,
    Commands: commandHub,
    Services: serviceContainer,
    Lifetime: cancellationToken);

feature.Attach(context);
```

---

### Rule 5: Before Adding Parent Logic, Ask "Which Feature Owns This?"

If you're about to add code to the parent, stop and ask: "Is this coordinating features, or is this feature logic that belongs in a feature folder?"

**Coordination (belongs in parent):**
- Starting all features
- Shutting down
- Routing messages between features
- Providing shared infrastructure (viewport, event bus, state container)

**Feature logic (belongs in feature folder):**
- Token claiming
- Login flows
- Retry logic
- Validation rules
- Business calculations

**Test:** If you can describe the logic without naming other features, it probably belongs in a feature folder.

---

## Feature-Specific UI Behavior

### Visual vs Behavior Separation

**Shared components** (`setting/Components/`) provide:
- Visual appearance (styles, templates, layout)
- Default universal behavior (standard click, hover, validation)

**Feature-specific behavior** can be added locally without modifying shared components.

### Pattern: Behavior Wrapper (Combo Component)

When a feature needs custom interaction (gestures, input handling, validation) but the warehouse visual is correct:

1. Create a wrapper UserControl in your feature folder
2. Import the shared component inside it
3. Attach custom event handlers
4. Keep visual style from warehouse
5. Name clearly: `[FeatureName][Purpose].xaml`

**Example:**
```xml
<!-- Features/Zoom/ZoomControl.xaml -->
<UserControl x:Class="Module.MyModule.Features.Zoom.ZoomControl"
             xmlns:setting="clr-namespace:Citadel.Setting.Components;assembly=Citadel.Setting">
    
    <setting:SettingButton 
        x:Name="ZoomButton"
        Content="Zoom"
        PreviewMouseWheel="OnZoomWheel"     ← custom behavior
        PreviewTouchGesture="OnPinchZoom">  ← custom behavior
        <!-- Uses warehouse visual style -->
    </setting:SettingButton>
    
</UserControl>
```

```cs
// Features/Zoom/ZoomControl.xaml.cs
public partial class ZoomControl : UserControl
{
    // Feature-specific behavior
    private void OnZoomWheel(object sender, MouseWheelEventArgs e) 
    {
        // Zoom logic here
    }
    
    private void OnPinchZoom(object sender, TouchEventArgs e)
    {
        // Gesture logic here
    }
}
```

**This is a combo component:**
- Wraps shared primitive (visual comes from warehouse)
- Custom behavior for this feature (logic stays local)
- Doesn't modify warehouse component
- Can be created without approval per citadel-shared-ui skill

**When to use:**
- Shared component visual is correct
- Need different interaction behavior
- Behavior is feature-specific, not universal

**When NOT to use:**
- Visual appearance needs to change (request new shared component)
- Behavior should be universal (extend warehouse component)

---

## Stop-and-Ask Boundary

Before adding logic to a parent file or creating cross-feature coupling, stop and report:

1. "I need to add [X] logic"
2. "Current features involved: [Y, Z]"
3. "Should this be in parent (coordination) or in feature [Y]'s folder?"

Then wait for approval.

**Exception:** Simple routing of existing events doesn't need approval.

---

## Example: Reader Module (Reference Implementation)

See `module/mangareader/Reader/REFACTOR-SPEC.md` for the canonical example of this pattern.

**Structure:**
```
Reader/
├── ReaderWindow.cs                    ← composition root (thin)
├── ReaderCore/                        ← infrastructure (power outlets)
│   ├── FrameContentHost.cs
│   ├── ReaderSessionState.cs
│   └── [hubs, routers, adapters]
└── Features/
    ├── ChapterLoading/                ← feature (pluggable)
    ├── Zoom/
    ├── Dim/
    └── [other features]/
```

**ReaderWindow:**
- Provides infrastructure (viewport, state, command hub)
- Loads feature catalog
- Routes events
- **Does NOT** know what ChapterLoading does internally

**ChapterLoading Feature:**
- Self-contained in its folder
- Exposes `IReaderChapterNavigation` contract
- Publishes events when chapter changes
- Parent doesn't know about 3-surface loading, z-index, preloading—that's all internal

---

## Benefits of This Pattern

1. **Easy to understand** — folder structure shows what's possible
2. **Easy to test** — features can be tested in isolation
3. **Easy to extend** — add new feature = new folder + catalog entry
4. **Easy to remove** — delete feature folder, remove from catalog, done
5. **Reusable** — copy infrastructure (`Core/` + parent) to new module, plug in different features

---

## Common Mistakes

### ❌ Parent knows feature internals
```cs
// BAD: Parent directly calling feature's internal method
tokenFarmer.StartHarvestLoop(interval: 60);
```

### ✅ Parent provides infrastructure, feature does its thing
```cs
// GOOD: Parent just registers feature
catalog.Add("TokenFarmer", () => new TokenFarmerFeature());
// Feature handles its own harvest loop internally
```

---

### ❌ Features calling each other directly
```cs
// BAD: Feature A reaching into Feature B
public class FeatureA {
    private FeatureB _featureB;
    
    void DoThing() {
        _featureB.GetSomeData();
    }
}
```

### ✅ Features publishing/subscribing via hub
```cs
// GOOD: Feature A publishes event
public class FeatureA {
    void DoThing() {
        _eventBus.Publish(new DataNeeded());
    }
}

// Feature B subscribes independently
public class FeatureB {
    void Attach(Context ctx) {
        ctx.EventBus.Subscribe<DataNeeded>(OnDataNeeded);
    }
}
```

---

### ❌ Parent has feature-specific logic
```cs
// BAD: Parent knows about token farming specifics
if (tokenCount > 100) {
    ShowSuccessMessage("Farming complete!");
}
```

### ✅ Feature handles its own logic, parent just routes
```cs
// GOOD: TokenFarmer publishes completion event
tokenFarmer.FarmingCompleted += (count) => {
    notificationHub.Show($"Earned {count} tokens");
};
// Parent doesn't interpret what "100 tokens" means
```

---

## Implementation Checklist

When building a new module with features:

- [ ] Create parent as composition root only
- [ ] Create `Core/` or `[ModuleName]Core/` for infrastructure
- [ ] Create `Features/` folder
- [ ] Each feature gets its own subfolder
- [ ] Features implement common interface (`IFeature`, `IModuleFeature`, etc.)
- [ ] Create feature catalog for registration
- [ ] Parent loads catalog, doesn't know feature names
- [ ] Features communicate via events/commands, not direct calls
- [ ] Shared state lives in context or service container
- [ ] UI behavior wrappers stay in feature folders
- [ ] Test each feature can be removed without breaking parent
- [ ] **Level B:** citizen carries the guard test with the five assertions from
      "Two Levels, One Contract"; extend it whenever a feature is added or moved
- [ ] **Level B:** module shared folder (`shareLogic/`) imports no feature
- [ ] **Level B:** adding a feature touched only that feature's folder plus its one
      catalog line (verify with the actual diff, not intent)

---

## Related Skills

- **citadel-shared-ui** — For UI components and visual behavior
- **Reader refactor spec** — Reference implementation of this pattern

---

## Notes for Future Development

This pattern should scale to:
- Token farming modules (gorouter, agent router)
- PDF/image viewer modules
- Any module with multiple distinct capabilities

The key is **parent stays dumb**, features stay **self-contained**, communication stays **loose**.
