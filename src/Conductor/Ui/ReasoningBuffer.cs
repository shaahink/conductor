namespace Conductor.Ui;

/// <summary>
/// Accumulates agent reasoning ("thinking") events. opencode emits reasoning as discrete
/// paragraphs, and sometimes as a growing snapshot of the same block — this buffer collapses
/// snapshot-growth into one entry (so the lane doesn't spam near-identical lines) while keeping
/// distinct paragraphs, and retains the full history for the scrollable pop-out pager.
/// Not thread-safe by itself; callers guard it.
/// </summary>
public sealed class ReasoningBuffer
{
    public readonly record struct Entry(DateTime Utc, string Text);

    private readonly List<Entry> _entries = new();
    private readonly int _cap;

    public ReasoningBuffer(int cap = 800) => _cap = cap;

    public int Count => _entries.Count;

    public void Add(string text, DateTime utc)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;

        if (_entries.Count > 0)
        {
            var last = _entries[^1];
            // Same block still growing (or a shorter re-emit of it) — keep the longer text.
            if (text.StartsWith(last.Text, StringComparison.Ordinal) ||
                last.Text.StartsWith(text, StringComparison.Ordinal))
            {
                _entries[^1] = last with { Text = text.Length >= last.Text.Length ? text : last.Text };
                return;
            }
        }
        _entries.Add(new Entry(utc, text));
        if (_entries.Count > _cap) _entries.RemoveRange(0, _entries.Count - _cap);
    }

    public IReadOnlyList<Entry> Recent(int n)
        => n >= _entries.Count ? _entries.ToArray() : _entries.Skip(_entries.Count - n).ToArray();

    public IReadOnlyList<Entry> All() => _entries.ToArray();
}
