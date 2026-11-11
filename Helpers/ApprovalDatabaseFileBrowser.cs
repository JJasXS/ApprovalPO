namespace ApprovalPO.Helpers;

/// <summary>
/// Resolves company .FDB paths on the ApprovalPO host the same way SqlAccountSync does:
/// OS file dialog only gives a file name; the server searches known Acc folders for the real path.
/// </summary>
public static class ApprovalDatabaseFileBrowser
{
    private const int MaxMatches = 20;
    private const int MaxDirs = 8000;
    private static readonly TimeSpan SearchLimit = TimeSpan.FromSeconds(8);

    private static readonly string[] Preferred =
    [
        @"C:\eStream\SQLAccounting\Share",
        @"C:\eStream\SQLAccounting\DB",
        @"C:\SQLData",
        @"D:\ACC",
        @"C:\ACC",
    ];

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "AppData", "WinSxS",
        "node_modules", ".git",
    };

    public static IReadOnlyList<string> FindByName(string? fileName, IConfiguration? configuration = null)
    {
        var name = SafeName(fileName);
        if (name is null)
            return Array.Empty<string>();

        var state = new SearchState(name, DateTime.UtcNow + SearchLimit);
        foreach (var root in SearchRoots(configuration).Where(Directory.Exists))
            Search(root, state);
        return state.Found;
    }

    private static IEnumerable<string> SearchRoots(IConfiguration? configuration)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Preferred)
        {
            if (seen.Add(root))
                yield return root;
        }

        var configured = configuration?["ApprovalDatabases:SearchRoots"] ?? "";
        foreach (var part in configured.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part))
                yield return part;
        }

        var bootstrapPath = (configuration?["TenantBootstrap:FirebirdDatabasePath"]
            ?? Environment.GetEnvironmentVariable("FIREBIRD_DB_PATH")
            ?? Environment.GetEnvironmentVariable("DB_PATH")
            ?? "").Trim();
        if (string.IsNullOrWhiteSpace(bootstrapPath))
            yield break;

        string? bootstrapDir = null;
        try
        {
            bootstrapDir = Path.GetDirectoryName(Path.GetFullPath(bootstrapPath));
        }
        catch
        {
            // ignore bad bootstrap path
        }

        if (!string.IsNullOrWhiteSpace(bootstrapDir) && seen.Add(bootstrapDir))
            yield return bootstrapDir;
    }

    private static void Search(string dir, SearchState state)
    {
        if (state.Stop) return;
        state.Visited++;
        foreach (var file in SafeFiles(dir, state.Name))
        {
            state.Found.Add(file);
            if (state.Found.Count >= MaxMatches) return;
        }

        foreach (var child in SafeDirs(dir))
        {
            if (state.Stop) return;
            if (!CanEnter(child)) continue;
            Search(child, state);
        }
    }

    private static string? SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var ext = Path.GetExtension(name);
        if (!ext.Equals(".fdb", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".gdb", StringComparison.OrdinalIgnoreCase))
            return null;
        return name;
    }

    private static List<string> SafeFiles(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir, name).Take(MaxMatches).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new List<string>();
        }
    }

    private static List<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new List<string>();
        }
    }

    private static bool CanEnter(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name) || SkipNames.Contains(name))
            return false;
        try
        {
            var attr = File.GetAttributes(path);
            var blocked = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
            return (attr & blocked) == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private sealed class SearchState
    {
        public SearchState(string name, DateTime deadline)
        {
            Name = name;
            Deadline = deadline;
        }

        public string Name { get; }
        public DateTime Deadline { get; }
        public List<string> Found { get; } = new();
        public int Visited { get; set; }
        public bool Stop => Found.Count >= MaxMatches || Visited >= MaxDirs || DateTime.UtcNow >= Deadline;
    }
}
