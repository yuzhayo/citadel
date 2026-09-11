// PostToolUse guard on *.csproj: enforces Citadel's dependency graph.
//
// Why PostToolUse and not Pre: an Edit only hands the hook its new_string, not
// the resulting file. Reading from disk after the write is the only reliable way
// to see the real reference set. A bad line in a csproj is trivially revertible,
// so reporting after the fact costs nothing.
//
// Only ProjectReference elements are inspected, so comments and type names like
// ModuleDescriptor or IModuleGate never trip it.

import { readFileSync, existsSync } from "node:fs";
import path from "node:path";

// Core libraries never reference module projects. Key = project, value = what it may see.
const ALLOWED = {
  "Citadel.Core": [],
  "Citadel.Contract": ["Citadel.Core"],
  "Citadel.Ui": ["Citadel.Core"],
  "Citadel.Setting": ["Citadel.Core", "Citadel.Contract"],
  // Shell hosts App, the composition root — the single place allowed to know
  // both sides, so Searcher is permitted HERE and nowhere else in core/.
  "Citadel.Shell": [
    "Citadel.Core",
    "Citadel.Contract",
    "Citadel.Ui",
    "Citadel.Setting",
    "Citadel.Searcher",
  ],
  "Citadel.Searcher": ["Citadel.Contract"],
};

const MODULE_ALLOWED = ["Citadel.Core", "Citadel.Contract", "Citadel.Setting"];

let raw = "";
process.stdin.on("data", (d) => (raw += d)).on("end", () => {
  try {
    const file = JSON.parse(raw)?.tool_input?.file_path ?? "";
    if (!file.endsWith(".csproj") || !existsSync(file)) return;

    const norm = file.replace(/\\/g, "/");
    // A test project legitimately references whatever it tests.
    if (/(^|\/)tests\//.test(norm)) return;

    const name = path.basename(file, ".csproj");
    let allowed = /(^|\/)module\//.test(norm) ? MODULE_ALLOWED : ALLOWED[name];
    // C8: a core/ or setting/ project absent from the graph is NOT exempt. Treat it
    // as allowed-to-reference-nothing so a brand-new project cannot silently wire
    // itself into the dependency graph; adding it to ALLOWED is a deliberate act.
    if (!allowed && /(^|\/)(core|setting)\//.test(norm)) allowed = [];
    if (!allowed) return; // tests/ and locations outside core/setting/module stay exempt

    const refs = [
      ...readFileSync(file, "utf8").matchAll(
        /<ProjectReference\s+Include\s*=\s*"([^"]+)"/g,
      ),
    ].map((m) => path.basename(m[1].replace(/\\/g, "/"), ".csproj"));

    const bad = refs.filter((r) => !allowed.includes(r));
    if (bad.length === 0) return;

    console.log(
      JSON.stringify({
        decision: "block",
        reason:
          `Dependency graph violation in ${name}.csproj: references ` +
          `${bad.join(", ")}, which Citadel's dependency graph does not permit. ` +
          `${name} may reference ` +
          `${allowed.length ? allowed.join(", ") : "nothing"}. ` +
          `If this looks necessary, the design is wrong — the boundary does ` +
          `not move. See .docs/README.md for the current architecture.`,
      }),
    );
  } catch {}
});
