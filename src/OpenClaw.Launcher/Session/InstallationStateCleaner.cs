namespace OpenClaw.Launcher.Session;

/// <summary>Deletes only the contents of the two package-owned state roots.</summary>
internal sealed class InstallationStateCleaner : IInstallationStateCleaner
{
    private readonly string[] _roots;
    private readonly Dictionary<string, TrustedPath.FileIdentity?> _rootIdentities;
    private readonly HashSet<string> _preservedPaths;
    private readonly IInstallationFileSystem _fileSystem;
    private readonly Action<string>? _beforeTraversal;
    private readonly Action<string>? _beforeDeleteEntry;

    public InstallationStateCleaner(HostPaths paths, string productLocalStateRoot)
        : this(
            [paths.StateRoot, productLocalStateRoot],
            paths.PackageFamilyName,
            preservedPaths: [paths.GatewayIsolationStatePath])
    {
    }

    internal InstallationStateCleaner(
        IReadOnlyList<string> trustedRoots,
        string? packageFamilyName = "test",
        IInstallationFileSystem? fileSystem = null,
        Action<string>? beforeTraversal = null,
        Action<string>? beforeDeleteEntry = null,
        IReadOnlyList<string>? preservedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        _fileSystem = fileSystem ?? PhysicalInstallationFileSystem.Instance;
        _beforeTraversal = beforeTraversal;
        _beforeDeleteEntry = beforeDeleteEntry;
        _preservedPaths = new HashSet<string>(
            preservedPaths?.Select(Path.GetFullPath) ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (packageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so --fresh is unavailable.");
        }

        _roots = [.. trustedRoots.Select(root => ValidateRoot(root, _fileSystem))];
        _rootIdentities = _fileSystem is PhysicalInstallationFileSystem
            ? _roots.ToDictionary(
                root => root,
                root => TrustedPath.TryGetDirectoryIdentity(root),
                StringComparer.OrdinalIgnoreCase)
            : [];
        if (_roots[0].StartsWith(_roots[1] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            _roots[1].StartsWith(_roots[0] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException("The installation state roots overlap and are unsafe to clear.");
        }
    }

    public void Clear()
    {
        foreach (string root in _roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_fileSystem.DirectoryExists(root))
            {
                continue;
            }

            _beforeTraversal?.Invoke(root);
            if (_fileSystem is PhysicalInstallationFileSystem)
            {
                using TrustedPath.ValidatedDirectory? directory =
                    TrustedPath.TryOpenValidatedDirectory(root, _rootIdentities[root]);
                if (directory is null)
                {
                    continue;
                }

                foreach (string entry in _fileSystem.EnumerateFileSystemEntries(root))
                {
                    if (IsPreserved(entry))
                    {
                        continue;
                    }

                    _beforeDeleteEntry?.Invoke(entry);
                    TrustedPath.TryDeleteOwnedEntry(directory, entry);
                }

                continue;
            }

            if (!_fileSystem.DirectoryExists(root) ||
                (_fileSystem.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            foreach (string entry in _fileSystem.EnumerateFileSystemEntries(root))
            {
                if (IsPreserved(entry))
                {
                    continue;
                }

                DeleteEntry(entry);
            }
        }
    }

    private static string ValidateRoot(string root, IInstallationFileSystem fileSystem)
    {
        string fullRoot = Path.GetFullPath(root);
        string? parent = Directory.GetParent(fullRoot)?.FullName;
        if (parent is null || string.Equals(fullRoot, parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException("The installation state root is unsafe to clear.");
        }

        for (DirectoryInfo? current = new DirectoryInfo(fullRoot);
            current is not null;
            current = current.Parent)
        {
            if (fileSystem.DirectoryExists(current.FullName) &&
                (fileSystem.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    "The installation state root has a reparse-point ancestor and cannot be cleared.");
            }
        }

        return fullRoot;
    }

    private void DeleteEntry(string path)
    {
        if (IsPreserved(path))
        {
            return;
        }

        FileAttributes attributes = _fileSystem.GetAttributes(path);
        _beforeDeleteEntry?.Invoke(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                _fileSystem.DeleteDirectory(path);
            }
            else
            {
                _fileSystem.DeleteFile(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            _fileSystem.DeleteFile(path);
            return;
        }

        foreach (string child in _fileSystem.EnumerateFileSystemEntries(path))
        {
            DeleteEntry(child);
        }

        _fileSystem.DeleteDirectory(path);
    }

    private bool IsPreserved(string path) =>
        _preservedPaths.Contains(Path.GetFullPath(path));
}

internal interface IInstallationFileSystem
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    FileAttributes GetAttributes(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

internal sealed class PhysicalInstallationFileSystem : IInstallationFileSystem
{
    public static PhysicalInstallationFileSystem Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path);
}
