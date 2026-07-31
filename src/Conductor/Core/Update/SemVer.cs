using System.Globalization;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — semver 2.0.0 precedence, because <c>conductor update</c> has to answer "is the release
/// newer than me?" and string comparison gets that wrong in the one case this repo lives in.
///
/// <para>Since SC8.2 the running engine is almost always a tag-height PRERELEASE:
/// <c>2.1.1-alpha.0.7</c> means "seven commits past v2.1.0, heading for 2.1.1". Semver says a
/// prerelease sorts BELOW the release it precedes, so <c>2.1.1-alpha.0.7 &lt; 2.1.1</c> — which is
/// exactly right, and exactly what <c>string.CompareOrdinal</c> gets backwards (it reads '-' as
/// less than '\0'-terminated only by accident, and orders <c>alpha.0.10</c> below <c>alpha.0.7</c>
/// because it compares digits as text). Numeric prerelease identifiers compare numerically here.</para>
///
/// <para>Build metadata (<c>+abc123def456</c>) is parsed and then IGNORED for precedence, per the
/// spec. Two binaries from the same version and different commits are not "newer" than one another —
/// that question is what the commit sha in <c>conductor version</c> is for.</para>
/// </summary>
public readonly record struct SemVer(
    int Major,
    int Minor,
    int Patch,
    string Prerelease,
    string BuildMetadata) : IComparable<SemVer>
{
    /// <summary>True when this is a prerelease (<c>-alpha.0.7</c>) rather than a released version.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>Round-trips to the canonical string, build metadata included.</summary>
    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease.Length > 0) core += "-" + Prerelease;
        if (BuildMetadata.Length > 0) core += "+" + BuildMetadata;
        return core;
    }

    /// <summary>Parses <c>[v]MAJOR.MINOR.PATCH[-prerelease][+build]</c>. A leading <c>v</c> is accepted
    /// because git tags carry one and the release feed reports the tag verbatim; everything downstream
    /// would otherwise have to remember to strip it, and one caller always forgets.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        if (s[0] is 'v' or 'V') s = s[1..];

        var plus = s.IndexOf('+', StringComparison.Ordinal);
        var build = plus >= 0 ? s[(plus + 1)..] : "";
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        if (dash >= 0) s = s[..dash];

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out var major)) return false;
        if (!TryNumber(parts[1], out var minor)) return false;
        if (!TryNumber(parts[2], out var patch)) return false;

        version = new SemVer(major, minor, patch, pre, build);
        return true;
    }

    /// <summary>Parses or throws — for call sites that own the literal (tests, constants).</summary>
    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"not a semantic version: '{text}'");

    private static bool TryNumber(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Semver 11.3–11.4. Absent prerelease outranks present; otherwise dot-separated
    /// identifiers compare left to right, numeric ones numerically and below alphanumeric ones, and a
    /// longer identifier list outranks its own prefix.</summary>
    private static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;   // 1.0.0 > 1.0.0-alpha
        if (b.Length == 0) return -1;

        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length) return -1;   // alpha.1 < alpha.1.1
            if (i >= right.Length) return 1;

            var lNum = TryNumber(left[i], out var ln);
            var rNum = TryNumber(right[i], out var rn);
            int c;
            if (lNum && rNum) c = ln.CompareTo(rn);          // alpha.0.7 < alpha.0.10
            else if (lNum) c = -1;                            // numeric < alphanumeric
            else if (rNum) c = 1;
            else c = string.CompareOrdinal(left[i], right[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}
