#!/usr/bin/env sh
# SC8.2 - print one release's section of CHANGELOG.md, or fail loudly.
#
# Why a script and not an inline awk in the workflow: this is the thing that makes "a CHANGELOG
# section per release" STRUCTURAL rather than aspirational. release.yml runs it as the first job of
# a tag build, so a tag pushed without a changelog entry stops before five platforms are compiled,
# and the section it prints becomes the release body. A rule nobody can run locally is a rule that
# rots, so it is a file you can drive by hand:
#
#   tools/changelog-section.sh v0.1.0        # prints the 0.1.0 section
#   tools/changelog-section.sh 9.9.9         # exits 1, names the heading it wanted
#
# POSIX sh + awk on purpose: it runs on the ubuntu release runner, and on Windows through Git Bash.
set -eu

VERSION="${1:-}"
FILE="${2:-CHANGELOG.md}"

if [ -z "$VERSION" ]; then
  echo "usage: changelog-section.sh <version> [changelog-path]" >&2
  echo "       version may be given as 1.2.3 or v1.2.3" >&2
  exit 2
fi

# Accept the tag form and the bare semver form; the file always uses the bare form.
VERSION="${VERSION#v}"

if [ ! -f "$FILE" ]; then
  echo "changelog-section: $FILE does not exist." >&2
  echo "  Every release needs a section. Create $FILE with a '## [$VERSION] - <date>' heading." >&2
  exit 1
fi

SECTION=$(awk -v want="$VERSION" '
  { sub(/\r$/, "") }
  # A section starts at "## [<version>]" and ends at the next "## " heading.
  /^## / {
    if (inside) exit
    heading = $0
    sub(/^## +/, "", heading)
    sub(/^\[/, "", heading)
    sub(/\].*$/, "", heading)
    if (heading == want) { inside = 1; next }
  }
  inside { print }
' "$FILE")

# Trim leading and trailing blank lines so the release body does not open with whitespace.
SECTION=$(printf '%s\n' "$SECTION" | sed -e '/./,$!d' | sed -e ':a' -e '/^\s*$/{$d;N;ba' -e '}')

if [ -z "$SECTION" ]; then
  echo "changelog-section: no section for $VERSION in $FILE." >&2
  echo "  Expected a heading '## [$VERSION] - <date>' with at least one line under it." >&2
  echo "  Sections found:" >&2
  grep -E '^## ' "$FILE" >&2 || true
  exit 1
fi

printf '%s\n' "$SECTION"
