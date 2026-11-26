namespace Containers.Windows.Silos;

public static partial class WcifsWorkspace
{
    private static void BuildDirectoryLayer(string destination, string[] lowerPaths)
    {
        Directory.CreateDirectory(destination);

        var pending = new Stack<(string Destination, DirectoryInfo[] Sources)>();
        pending.Push((destination, Array.ConvertAll(lowerPaths, path => new DirectoryInfo(path))));

        while (pending.TryPop(out var level))
        {
            var entries = level.Sources.SelectMany(directory => directory.EnumerateFileSystemInfos()).GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var group in entries)
            {
                // A higher-priority file hides lower entries with the same name.
                var winner = group.First();
                if ((winner.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw UnsupportedReparse(winner);

                if (winner is not DirectoryInfo first)
                    continue;

                var sources = new List<DirectoryInfo>();
                foreach (var entry in group)
                {
                    if (entry is not DirectoryInfo directory)
                        throw new IOException($"WCIFS cannot merge directory '{first.FullName}' over file '{entry.FullName}'.");

                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw UnsupportedReparse(directory);

                    sources.Add(directory);
                }

                var child = Path.Combine(level.Destination, first.Name);
                Directory.CreateDirectory(child);
                pending.Push((child, sources.ToArray()));
            }
        }
    }

    private static IOException UnsupportedReparse(FileSystemInfo entry) => new IOException($"The multi-directory workspace requires ordinary files and directories. '{entry.FullName}' is a reparse point.");
}