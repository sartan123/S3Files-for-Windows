using Microsoft.Windows.ProjFS;
using OSVFS.ObjectStore;

/// <summary>
/// Per-enumeration cursor that holds the sorted child list, current filter, and
/// position so successive ProjFS Get/End callbacks can resume mid-listing.
/// </summary>
internal sealed class DirectoryEnumerationSession
{
    private readonly List<ObjectInfo> entries;
    private string filter = "*";
    private bool filterSet;
    private int index;

    /// <summary>
    /// Stores the entries and pre-sorts them with ProjFS's filename comparer so the
    /// listing order is consistent with what NTFS would return.
    /// </summary>
    public DirectoryEnumerationSession(List<ObjectInfo> entries)
    {
        this.entries = entries;
        this.entries.Sort(static (a, b) => Utils.FileNameCompare(GetLeafName(a), GetLeafName(b)));
    }

    /// <summary>
    /// Resets the cursor and replaces the active filter; called when ProjFS asks
    /// for a restart-scan.
    /// </summary>
    public void Restart(string? newFilter)
    {
        index = 0;
        filter = string.IsNullOrEmpty(newFilter) ? "*" : newFilter;
        filterSet = true;
    }

    /// <summary>
    /// Sets the filter on first use, but ignores subsequent filters per the ProjFS
    /// contract that the filter is fixed for the duration of an enumeration.
    /// </summary>
    public void EnsureFilter(string? newFilter)
    {
        if (filterSet) return;
        filter = string.IsNullOrEmpty(newFilter) ? "*" : newFilter;
        filterSet = true;
    }

    /// <summary>
    /// Advances past non-matching entries and returns the next match; false when
    /// the listing is exhausted.
    /// </summary>
    public bool TryGetCurrent(out ObjectInfo entry, out string leafName)
    {
        while (index < entries.Count)
        {
            var candidate = entries[index];
            var name = GetLeafName(candidate);
            if (Utils.IsFileNameMatch(name, filter))
            {
                entry = candidate;
                leafName = name;
                return true;
            }
            index++;
        }
        entry = default;
        leafName = string.Empty;
        return false;
    }

    /// <summary>
    /// Steps the cursor past the current entry, signaling acceptance.
    /// </summary>
    public void Advance() => index++;

    /// <summary>
    /// Extracts the leaf name from a backslash-separated relative path.
    /// </summary>
    private static string GetLeafName(ObjectInfo info)
    {
        var slash = info.RelativePath.LastIndexOf('\\');
        return slash >= 0 ? info.RelativePath[(slash + 1)..] : info.RelativePath;
    }
}
