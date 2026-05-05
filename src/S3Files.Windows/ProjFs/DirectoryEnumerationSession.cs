using Microsoft.Windows.ProjFS;
using S3Files.Windows.S3;

namespace S3Files.Windows.ProjFs;

internal sealed class DirectoryEnumerationSession
{
    private readonly List<S3ObjectInfo> entries;
    private string filter = "*";
    private bool filterSet;
    private int index;

    public DirectoryEnumerationSession(List<S3ObjectInfo> entries)
    {
        this.entries = entries;
        this.entries.Sort(static (a, b) => Utils.FileNameCompare(GetLeafName(a), GetLeafName(b)));
    }

    public void Restart(string? newFilter)
    {
        index = 0;
        filter = string.IsNullOrEmpty(newFilter) ? "*" : newFilter;
        filterSet = true;
    }

    public void EnsureFilter(string? newFilter)
    {
        if (filterSet) return;
        filter = string.IsNullOrEmpty(newFilter) ? "*" : newFilter;
        filterSet = true;
    }

    public bool TryGetCurrent(out S3ObjectInfo entry, out string leafName)
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

    public void Advance() => index++;

    private static string GetLeafName(S3ObjectInfo info)
    {
        var slash = info.RelativePath.LastIndexOf('\\');
        return slash >= 0 ? info.RelativePath[(slash + 1)..] : info.RelativePath;
    }
}
