// workspace.mjs - the one workspace resolver for Node tools (twin of tools/common/Workspace.ps1).
//
// The "workspace" is the folder that holds the MAIN checkout of this repository and its sibling
// folders (<workspace>/_testserver, <workspace>/_tools, ...).
// Nothing here hard-codes a machine path.
//
// Resolution order:
//   1. env PROGENITOR_WORKSPACE, when set.
//   2. git's common dir, read from git's own metadata (the same answer as
//      `git rev-parse --path-format=absolute --git-common-dir`): a `.git` DIRECTORY is the common
//      dir; a `.git` FILE (linked worktree) names its gitdir, whose `commondir` file names it.
//      The common dir's parent is the main checkout; that folder's parent is the workspace.
//      Read from files rather than by running git so this module stays importable by tools that
//      must never import node:child_process (the catalog's offline guard).
//   3. The parent of the repository root that contains this file.
// Returns null when none of these names an existing folder; callers then refuse, never guess.
//
// Read-only: it reads files and never writes.

import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = path.resolve(HERE, '..', '..');

function isDir(p) {
  try { return statSync(p).isDirectory(); } catch { return false; }
}

function firstLine(file) {
  try { return readFileSync(file, 'utf8').split(/\r?\n/)[0].trim(); } catch { return ''; }
}

/** The git common dir for `repoRoot`, from git's metadata files; null when there is none. */
export function gitCommonDir(repoRoot = REPO_ROOT) {
  const dotGit = path.join(repoRoot, '.git');
  if (!existsSync(dotGit)) return null;
  if (isDir(dotGit)) return path.resolve(dotGit);
  const m = /^gitdir:\s*(.+)$/.exec(firstLine(dotGit));
  if (!m) return null;
  const gitDir = path.resolve(repoRoot, m[1].trim());
  const commonFile = path.join(gitDir, 'commondir');
  if (!existsSync(commonFile)) return gitDir;
  const common = firstLine(commonFile);
  return common ? path.resolve(gitDir, common) : null;
}

let cached;

/** The workspace folder, or null when it cannot be resolved. */
export function workspaceRoot() {
  const env = process.env.PROGENITOR_WORKSPACE;
  if (env) return path.resolve(env);
  if (cached !== undefined) return cached;
  let candidate = null;
  const common = gitCommonDir(REPO_ROOT);
  if (common) candidate = path.dirname(path.dirname(common));
  if (!candidate) candidate = path.dirname(REPO_ROOT);
  cached = candidate && isDir(candidate) ? candidate : null;
  return cached;
}

/** The main checkout (the folder holding the git common dir), or null. */
export function mainCheckoutRoot() {
  const common = gitCommonDir(REPO_ROOT);
  return common ? path.dirname(common) : null;
}

/** <workspace>/<parts...>, or null when the workspace cannot be resolved. */
export function workspacePath(...parts) {
  const ws = workspaceRoot();
  return ws ? path.join(ws, ...parts) : null;
}

/** <workspace>/<parts...>, or throw naming the override the caller can set instead. */
export function requireWorkspacePath(override, ...parts) {
  const p = workspacePath(...parts);
  if (p) return p;
  throw new Error(`cannot resolve the Progenitor workspace (the folder that holds the main checkout) for '${parts.join('/')}'; set ${override ? override + ' or ' : ''}PROGENITOR_WORKSPACE`);
}

/** Test-server root: env PROGENITOR_TEST_ROOT, else <workspace>/_testserver, else null. */
export function testServerRoot() {
  return process.env.PROGENITOR_TEST_ROOT ?? workspacePath('_testserver');
}

/** The local evidence folder named by PROGENITOR_LOCAL_EVIDENCE, or null. Never committed. */
export function researchRoot() {
  return process.env.PROGENITOR_LOCAL_EVIDENCE || null;
}
