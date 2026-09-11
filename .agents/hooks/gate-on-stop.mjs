// Stop hook: run the test suite when the turn ends, but only if C# changed.
//
// Checking the suite at turn end protects the expandable core. Skipping the run
// when no .cs/.csproj is dirty keeps documentation-only turns inexpensive.
//
// This catches regressions, not wrong assertions, and does not replace review.
//
// Non-blocking by design: it reports and lets the turn end. Blocking on Stop can
// trap the session in a loop it cannot exit.

import { execSync } from "node:child_process";
import path from "node:path";

// Resolve the repo root from this script's own location (<root>/.agents/hooks/),
// so the hook does not depend on the cwd it happens to be invoked with.
const root = path.resolve(import.meta.dirname, "..", "..");

const run = (cmd) =>
  execSync(cmd, { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });

try {
  let dirty = "";
  try {
    dirty = run("git status --porcelain");
  } catch {
    // No repo, or git unavailable: fall through and just run the suite.
    dirty = "dirty";
  }

  const touchedCode = dirty === "dirty" ||
    dirty.split("\n").some((line) => /\.(cs|csproj|slnx|props)\s*$/.test(line));
  if (!touchedCode) process.exit(0);

  run("dotnet test Citadel.slnx --nologo -v q");
  console.log(JSON.stringify({ systemMessage: "Gate: dotnet test green." }));
} catch (err) {
  const output = String(err.stdout ?? "") + String(err.stderr ?? "");
  const detail = output
    .split("\n")
    .filter((l) => /error|\[FAIL\]|Failed!/i.test(l))
    .slice(0, 8)
    .join("\n")
    .trim();

  console.log(
    JSON.stringify({
      systemMessage:
        "Gate FAILED — uncommitted C# changes leave the suite red:\n" +
        (detail || output.split("\n").slice(-8).join("\n").trim()),
    }),
  );
}
