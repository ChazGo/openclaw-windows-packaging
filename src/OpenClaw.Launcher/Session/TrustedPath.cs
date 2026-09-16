using Microsoft.Win32.SafeHandles;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Session;

/// <summary>Rejects path redirection below a caller-established root.</summary>
internal static partial class TrustedPath
{
    public static void EnsureNoReparsePoints(string trustedRoot, string candidatePath) =>
        EnsureNoReparsePoints(trustedRoot, candidatePath, File.GetAttributes);

    internal static void EnsureNoReparsePoints(
        string trustedRoot,
        string candidatePath,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(getAttributes);

        string root = Path.GetFullPath(trustedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath);
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionException(
                $"The path resolves outside its trusted root: {candidate}");
        }

        Check(root);
        string relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        string current = root;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        void Check(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = getAttributes(path);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    $"The path contains a reparse point and is unsafe for host access: {path}");
            }
        }
    }

    /// <summary>
    /// Opens a file only when the object opened by the host is still below the
    /// trusted root and is not a reparse point.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The FileStream constructor takes ownership of the SafeFileHandle.")]
    public static FileStream OpenRead(string trustedRoot, string candidatePath)
    {
        EnsureNoReparsePoints(trustedRoot, candidatePath);

        SafeFileHandle handle = CreateFile(
            candidatePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"The trusted file could not be opened: {candidatePath}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionException(
                    $"The opened path is a reparse point: {candidatePath}");
            }

            string root = Path.GetFullPath(trustedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string opened = GetFinalPath(handle)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root + Path.DirectorySeparatorChar;
            if (!opened.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !opened.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionException(
                    $"The opened file resolves outside its trusted root: {opened}");
            }

            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        char[] path = new char[260];
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Length, 0);
        if (length == 0)
        {
            throw new IOException("The opened file's final path could not be determined.");
        }

        if (length >= path.Length)
        {
            path = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Length, 0);
            if (length == 0 || length >= path.Length)
            {
                throw new IOException("The opened file's final path is too long.");
            }
        }

        const string extendedPathPrefix = @"\\?\";
        string value = new(path, 0, checked((int)length));
        return value.StartsWith(extendedPathPrefix, StringComparison.Ordinal)
            ? value[extendedPathPrefix.Length..]
            : value;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint length,
        uint flags);
}
