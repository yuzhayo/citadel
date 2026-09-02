# Citadel agent contract

Before planning, reviewing, or changing code in this repository, read
`.agents/skills/citadel-shared-ui/SKILL.md` completely. Its shared-component
inventory and approval boundary are mandatory for every agent and sub-agent.

# Citadel Agent Contract — Mandatory Skill Usage

## Core Rule: Skills First, Code Second

Before starting any task in this workspace, you **must**:

1. **Identify relevant skills** from `.agents/skills/`
2. **Read the skill** completely (if you haven't read it recently)
3. **State which skills you're using** in your first reply
4. **Follow the skill's rules** throughout the task

**Enforcement:** If you skip the skill check, the work will be rejected and you'll need to redo it following the skill.

---

## Skill Activation Matrix

Use this table to know which skills apply to your task:

| If you're doing... | You MUST use these skills | You MAY use these skills |
|-------------------|---------------------------|--------------------------|
| **Adding/modifying UI** | `citadel-shared-ui` | `incremental-implementation` |
| **Adding/modifying feature logic** | `citadel-feature-modularity` | `test-driven-development`, `api-and-interface-design` |
| **Refactoring existing code** | `incremental-implementation` | `test-driven-development`, `code-review-and-quality` |
| **Splitting files or moving code** | `citadel-feature-modularity`, `incremental-implementation` | `git-workflow-and-versioning` |
| **Designing module contracts** | `api-and-interface-design`, `citadel-feature-modularity` | `planning-and-task-breakdown` |
| **Fixing bugs** | `test-driven-development` | `incremental-implementation` |
| **Reviewing code** | `code-review-and-quality`, `citadel-shared-ui` (if UI), `citadel-feature-modularity` (if features) | — |
| **Planning features** | `planning-and-task-breakdown` | `api-and-interface-design` |
| **Writing tests** | `test-driven-development` | — |
| **Git commits/PRs** | `git-workflow-and-versioning` | — |

---

## Auto-Activation Triggers

Some skills should activate automatically based on file patterns:

### `citadel-shared-ui` auto-activates when:
- Touching any `.xaml` or `.xaml.cs` file
- Editing `setting/Components/`
- Creating new UI controls
- Modifying module views

### `citadel-feature-modularity` auto-activates when:
- Working in `module/*/Features/`
- Editing parent module files (like `*Module.cs`, `*View.cs`)
- Creating new features
- Modifying feature catalogs

### `test-driven-development` auto-activates when:
- Working in `tests/` folder
- Fixing reported bugs
- Adding new logic/behavior
- Modifying existing functionality

---

## Skill Activation Protocol

When you start a task, follow this protocol:

### Step 1: Identify Skills (First Thing)
Before analyzing the task or writing code, list which skills apply:

```markdown
**Skills Activated:**
- citadel-feature-modularity (modifying Reader features)
- incremental-implementation (multi-phase refactor)
- test-driven-development (maintaining test coverage)
```

### Step 2: Read Skills
If you haven't read the skill recently (within the last 5-10 turns), read it completely before proceeding.

Use `read_file` on `.agents/skills/<skill-name>/SKILL.md`.

### Step 3: State Activation
In your first reply, explicitly state which skills you're using and why:

```markdown
I'm using **citadel-feature-modularity** because we're splitting the coordinator into separate features, and **incremental-implementation** because this is a multi-phase refactor that needs to stay green at each step.
```

This creates a record so the next agent (or you after compaction) knows what rules were active.

### Step 4: Apply Rules
Follow the skill's rules throughout the task. If the skill says "no god objects", don't create god objects. If it says "tests first", write tests first.

---

## Why This Matters

**Without skills:** Agents improvise. Each agent has different assumptions about how Citadel should be structured. You end up with:
- UI components duplicated instead of reused
- Features tightly coupled to parents
- Inconsistent patterns across modules
- Expensive refactors to fix pattern violations

**With skills:** Agents follow documented patterns. The codebase stays consistent. New features plug in cleanly. You navigate by folder structure and everything makes sense.

---

## Cost Analysis

**Initial cost:** ~2-5% more tokens per task (reading skills, stating activation)

**Savings:** Prevents refactors that cost 100-200x more tokens to fix

**Example:** The Reader refactor (extracting chapter loading into a feature) costs ~50k tokens. If agents had followed `citadel-feature-modularity` from the start, chapter loading would already be a feature—no refactor needed. That's a 200:1 return.

**Rule:** Better to cost more tokens upfront than refactor because agents did it wrong.

---

## Violation Consequences

If an agent violates a mandatory skill:

1. **Work is rejected** — operator will point out the violation
2. **Agent must redo** — following the skill this time
3. **Cost doubles** — original work + redo work

To avoid this: **check skills first, every time**.

---

## Exception Protocol

If a skill's rule genuinely doesn't fit the task:

1. **State the conflict** in your reply: "The skill says X, but this task needs Y because..."
2. **Propose alternative** that preserves the skill's intent
3. **Wait for approval** before proceeding

Do NOT silently ignore skills. Do NOT skip skills because "this is a special case."

---

## Skill Summary

| Skill | Purpose | When Mandatory |
|-------|---------|----------------|
| `citadel-shared-ui` | Reuse UI components, get approval for new ones | Any UI work |
| `citadel-feature-modularity` | Keep features self-contained, parents dumb | Any feature/module work |
| `incremental-implementation` | Stay green at each step, ship in phases | Refactors, multi-file changes |
| `test-driven-development` | Write tests first, maintain coverage | Bugs, new logic, behavior changes |
| `api-and-interface-design` | Design contracts before implementation | New module contracts, public APIs |
| `planning-and-task-breakdown` | Break work into phases with verification gates | Large features, unclear scope |
| `code-review-and-quality` | Review against patterns, check for violations | Code review, post-implementation audit |
| `git-workflow-and-versioning` | Commit conventions, PR structure | Commits, PRs, versioning |

---

## Final Rule

**If you're unsure whether a skill applies, assume it does and read it.**

Reading a skill unnecessarily costs ~500 tokens. Violating a skill and having to redo costs ~50,000 tokens. The math is clear.