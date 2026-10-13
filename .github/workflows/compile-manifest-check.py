#!/usr/bin/env python3
"""Job FCI1 (R44 finding 10): every <Compile> path resolves, at every commit a push adds.

Progenitor.Tests compiles ~230 files out of src/, samples/ and tools/ by explicit <Compile
Include> link, because it cannot ProjectReference Core (see the header of its csproj). Several
parallel jobs append to that one list on the same day, and it is easy to land the line that
names a file in one commit and the file itself in the next. The commit in between cannot build.

The tip of a push is checked by the compiler itself and by ProjectFileManifestTests. The commits
UNDER the tip are checked by nothing: GitHub Actions runs a push event once, at the head. That is
how fb9d7fa (the <Compile Include> for SampleConsentDemo.cs) reached main unbuildable while
cc84d95 (the file) made the tip green. The cost lands on whoever bisects through it later.

This reads csproj blobs and tree entries straight out of git, so it needs no toolchain, no
per-commit checkout, and no build. Same rule as ProjectFileManifestTests, deliberately restated
here rather than shared: the test cannot run at a commit whose test assembly does not compile,
which is precisely the commit that matters.
"""

import os
import posixpath
import subprocess
import sys
import xml.etree.ElementTree as ET

# The projects that name source files by hand. Everything else in the solution takes the SDK's
# default **/*.cs glob, where no path is written down and none can go stale.
PROJECTS = [
    "tests/Progenitor.Tests/Progenitor.Tests.csproj",
    "tools/driftmap/Progenitor.DriftMap.csproj",
    "src/Progenitor.Testing/Progenitor.Testing.csproj",
    "tests/Progenitor.Testing.Tests/Progenitor.Testing.Tests.csproj",
]

# A first push of a long branch should not turn into a thousand git calls. Anything past this is
# reported as unchecked rather than silently skipped.
MAX_COMMITS = 200

# Below this many resolved paths at the head commit, assume the reader has stopped seeing the
# file lists (an xmlns on <Project> would do it) rather than that the lists shrank by 50%.
VACUITY_FLOOR = 100


def git(*args):
    return subprocess.run(["git", *args], capture_output=True)


def tracked_paths(commit):
    """Every file in the commit's tree, as one set. One git call per commit rather than one per
    path: the tests project alone lists ~230, and a push of twenty commits would otherwise be
    thousands of process spawns."""
    result = git("ls-tree", "-r", "--name-only", "-z", commit)
    if result.returncode != 0:
        return set()
    return set(result.stdout.decode("utf-8").split("\0")) - {""}


def read_blob(commit, path):
    result = git("show", "%s:%s" % (commit, path))
    return result.stdout if result.returncode == 0 else None


def compile_paths(csproj_bytes, attribute):
    """Literal, project-relative paths in one attribute of the project's <Compile> items.

    Wildcards are skipped: a glob matching nothing is legal MSBuild and is how the Effects\\*.cs
    and Ui\\*.cs removes are written. So is any path holding an unexpanded MSBuild property, which
    this reader cannot evaluate and must not guess at.
    """
    root = ET.fromstring(csproj_bytes)
    for element in root.iter():
        if element.tag.split("}")[-1] != "Compile":
            continue
        value = element.get(attribute)
        if not value:
            continue
        for entry in value.split(";"):
            path = entry.strip()
            if not path or "*" in path or "?" in path or "$(" in path:
                continue
            yield path.replace("\\", "/")


def check_commit(commit):
    """Broken 'project -> path' strings at one commit, and how many paths resolved."""
    broken, resolved = [], 0
    tracked = tracked_paths(commit)
    for project in PROJECTS:
        if project not in tracked:
            continue  # the project did not exist yet at this commit
        csproj = read_blob(commit, project)
        if csproj is None:
            continue
        project_dir = posixpath.dirname(project)
        try:
            for attribute in ("Include", "Remove"):
                for path in compile_paths(csproj, attribute):
                    full = posixpath.normpath(posixpath.join(project_dir, path))
                    if full in tracked:
                        resolved += 1
                    else:
                        broken.append("%s: <Compile %s=\"%s\"> -> %s" % (project, attribute, path, full))
        except ET.ParseError as error:
            broken.append("%s: could not be parsed as XML (%s)" % (project, error))
    return broken, resolved


def commits_to_check():
    base = os.environ.get("RANGE_BASE", "").strip()
    head = os.environ.get("RANGE_HEAD", "").strip() or "HEAD"

    # A new branch pushes an all-zero 'before', and a force-push can leave one that is no longer
    # reachable. Neither is a reason to check nothing, so fall back to the head commit alone.
    usable_base = (
        base
        and set(base) != {"0"}
        and git("cat-file", "-e", base + "^{commit}").returncode == 0
    )
    if not usable_base:
        return [head], False

    listed = git("rev-list", "%s..%s" % (base, head)).stdout.decode().split()
    if not listed:
        return [head], False
    truncated = len(listed) > MAX_COMMITS
    return list(reversed(listed[:MAX_COMMITS])), truncated


def main():
    commits, truncated = commits_to_check()
    head_resolved = 0
    failures = []

    for commit in commits:
        broken, resolved = check_commit(commit)
        head_resolved = resolved  # commits are oldest-first, so this ends on the tip
        if broken:
            subject = git("log", "-1", "--format=%s", commit).stdout.decode().strip()
            failures.append((commit, subject, broken))

    print("checked %d commit(s); %d <Compile> paths resolved at the tip" % (len(commits), head_resolved))
    if truncated:
        print("note: only the newest %d commits of this push were checked" % MAX_COMMITS)

    if head_resolved < VACUITY_FLOOR:
        print(
            "\nFAIL: only %d <Compile> paths were found at the tip, below the floor of %d. This "
            "check is no longer reading the file lists it is meant to guard." % (head_resolved, VACUITY_FLOOR)
        )
        return 1

    if failures:
        print("\nFAIL: a <Compile> item names a file that does not exist at that commit, so the")
        print("project cannot build there. This is R44 finding 10 happening again: the usual cause")
        print("is one job's work split across two commits with the csproj line ahead of the file.")
        for commit, subject, broken in failures:
            print("\n  %s  %s" % (commit[:9], subject))
            for line in broken:
                print("      %s" % line)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
