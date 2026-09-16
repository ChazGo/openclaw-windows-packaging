using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Windows process operations for the signed-in-user gateway.</summary>
internal sealed class WindowsNativeGatewayProcess : INativeGatewayProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint TokenQuery = 0x0008;
    private const int ProcessCommandLineInformation = 60;
    private const int AppModelErrorNoPackage = 15700;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    public Task<NativeGatewayLaunchOutcome> LaunchAsync(
        NativeGatewayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string? logDirectory = Path.GetDirectoryName(request.LogPath);
        if (!string.IsNullOrEmpty(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        ProcessStartInfo startInfo = CreateStartInfo(request);
        string commandLine = WindowsKillOnCloseJob.BuildCommandLine(startInfo);
        char[] commandLineBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, commandLineBuffer, 0, commandLine.Length);

        using SafeFileHandle log = File.OpenHandle(
            request.LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        using SafeFileHandle inheritedLog = DuplicateForChild(log);
        using var handles = new InheritedHandles([inheritedLog.DangerousGetHandle()]);
        IntPtr environment = WindowsKillOnCloseJob.BuildEnvironmentBlock(startInfo);
        var startupInfo = new ExtendedStartupInfo
        {
            Startup = new StartupInfo
            {
                Size = Marshal.SizeOf<ExtendedStartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardOutput = inheritedLog.DangerousGetHandle(),
                StandardError = inheritedLog.DangerousGetHandle()
            },
            Attributes = handles.Attributes
        };

        try
        {
            if (!CreateProcessW(
                    startInfo.FileName,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateNoWindow | CreateUnicodeEnvironment |
                    ExtendedStartupInfoPresent,
                    environment,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out ProcessInformation processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to start the signed-in-user gateway.");
            }

            try
            {
                DateTimeOffset creationTime = ReadCreationTime(processInformation.Process);
                return Task.FromResult(new NativeGatewayLaunchOutcome(
                    checked((int)processInformation.ProcessId),
                    creationTime));
            }
            finally
            {
                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    public Task<NativeGatewayProcessSnapshot> InspectAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Inspect(processId));
    }

    public async Task<NativeGatewayProcessSnapshot> StopAsync(
        NativeGatewayRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        try
        {
            process = Process.GetProcessById(record.ProcessId);
        }
        catch (ArgumentException)
        {
            return new NativeGatewayProcessSnapshot();
        }

        using (process)
        {
            NativeGatewayProcessSnapshot snapshot = Inspect(process);
            string? mismatch = FindMismatch(record, snapshot);
            if (mismatch is not null)
            {
                return snapshot with { Error = mismatch };
            }

            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new NativeGatewayProcessSnapshot();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                return snapshot with
                {
                    Error = $"The verified gateway process could not be terminated: " +
                        exception.Message
                };
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(NativeGatewayLaunchRequest request)
    {
        if (!File.Exists(request.NodePath))
        {
            throw new FileNotFoundException(
                "The extracted Node.js runtime was not found.",
                request.NodePath);
        }
        if (!File.Exists(request.EntryPointPath))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                request.EntryPointPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.NodePath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        OpenClawRuntimeEnvironment.ApplyTo(
            startInfo.Environment,
            GatewayIsolationMode.Disabled);
        string? nodeDirectory = Path.GetDirectoryName(request.NodePath);
        if (!string.IsNullOrEmpty(nodeDirectory))
        {
            startInfo.Environment.TryGetValue("PATH", out string? inheritedPath);
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
                ? nodeDirectory
                : $"{nodeDirectory}{Path.PathSeparator}{inheritedPath}";
        }

        startInfo.ArgumentList.Add(request.EntryPointPath);
        startInfo.ArgumentList.Add("gateway");
        startInfo.ArgumentList.Add("run");
        if (request.Port is int port)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(
                port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    private static NativeGatewayProcessSnapshot Inspect(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return Inspect(process);
        }
        catch (ArgumentException)
        {
            return new NativeGatewayProcessSnapshot();
        }
    }

    private static NativeGatewayProcessSnapshot Inspect(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return new NativeGatewayProcessSnapshot();
            }

            return new NativeGatewayProcessSnapshot
            {
                ProcessFound = true,
                ProcessCreationTimeUtc = process.StartTime.ToUniversalTime(),
                OwnerSid = ReadOwnerSid(process),
                WindowsSessionId = process.SessionId,
                ExecutablePath = process.MainModule?.FileName,
                PackageGeneration = ReadPackageFullName(process.Handle),
                CommandLineArguments = ParseCommandLine(
                    ReadCommandLine(process.Handle)),
                ListeningPorts = ReadListeningPorts(process.Id)
            };
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return new NativeGatewayProcessSnapshot();
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or
            UnauthorizedAccessException)
        {
            return new NativeGatewayProcessSnapshot
            {
                ProcessFound = true,
                Error = $"The gateway process could not be inspected: {exception.Message}"
            };
        }
    }

    private static string ReadOwnerSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to inspect the gateway process owner.");
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User?.Value
                ?? throw new InvalidOperationException(
                    "The gateway process owner SID is unavailable.");
        }
    }

    private static string ReadCommandLine(IntPtr processHandle)
    {
        int length = 0;
        int status = NtQueryInformationProcess(
            processHandle,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            ref length);
        if (length <= 0)
        {
            throw new Win32Exception(
                status,
                "Unable to determine the gateway process command-line length.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                length,
                ref length);
            if (status != 0)
            {
                throw new Win32Exception(
                    status,
                    "Unable to inspect the gateway process command line.");
            }

            UnicodeString commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            return Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int[] ReadListeningPorts(int processId)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            AddressFamily.InterNetwork,
            TcpTableOwnerPidListener,
            0);
        if (result is not ErrorInsufficientBuffer and not 0)
        {
            throw new Win32Exception(checked((int)result));
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                order: false,
                AddressFamily.InterNetwork,
                TcpTableOwnerPidListener,
                0);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            IntPtr row = IntPtr.Add(buffer, sizeof(int));
            List<int> ports = [];
            for (int index = 0; index < count; index++)
            {
                TcpRowOwnerPid value = Marshal.PtrToStructure<TcpRowOwnerPid>(row);
                if (value.ProcessId == processId)
                {
                    int port = (ushort)IPAddress.NetworkToHostOrder(
                        unchecked((short)value.LocalPort));
                    ports.Add(port);
                }
                row = IntPtr.Add(row, rowSize);
            }

            return [.. ports.Distinct().Order()];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTimeOffset ReadCreationTime(IntPtr processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out FileTime creation,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to read the gateway process creation time.");
        }

        long ticks = ((long)creation.High << 32) | creation.Low;
        return DateTimeOffset.FromFileTime(ticks);
    }

    private static string? FindMismatch(
        NativeGatewayRecord record,
        NativeGatewayProcessSnapshot snapshot)
    {
        if (!snapshot.ProcessFound)
        {
            return null;
        }
        if (snapshot.ProcessCreationTimeUtc != record.ProcessCreationTimeUtc)
        {
            return "The process identifier was reused; no process was terminated.";
        }
        if (!string.Equals(snapshot.OwnerSid, record.OwnerSid, StringComparison.Ordinal))
        {
            return "The process owner SID did not match; no process was terminated.";
        }
        if (snapshot.WindowsSessionId != record.WindowsSessionId)
        {
            return "The Windows session did not match; no process was terminated.";
        }
        if (!PathsEqual(snapshot.ExecutablePath, record.NodePath))
        {
            return "The Node.js executable did not match; no process was terminated.";
        }
        if (!string.Equals(
                snapshot.PackageGeneration,
                record.PackageGeneration,
                StringComparison.Ordinal))
        {
            return "The package generation did not match; no process was terminated.";
        }
        if (snapshot.CommandLineArguments is null ||
            !snapshot.CommandLineArguments.Any(
                argument => PathsEqual(argument, record.EntryPointPath)))
        {
            return "The packaged entry point did not match; no process was terminated.";
        }

        return snapshot.Error;
    }

    private static bool PathsEqual(string? left, string right) =>
        left is not null &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string[] ParseCommandLine(string commandLine)
    {
        IntPtr arguments = CommandLineToArgvW(commandLine, out int count);
        if (arguments == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to parse the gateway process command line.");
        }

        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                IntPtr value = Marshal.ReadIntPtr(arguments, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(arguments);
        }
    }

    private static string? ReadPackageFullName(IntPtr processHandle)
    {
        uint length = 0;
        int result = GetPackageFullName(processHandle, ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(
                result,
                "Unable to determine the gateway process package generation.");
        }

        var value = new char[length];
        result = GetPackageFullName(processHandle, ref length, value);
        if (result != 0)
        {
            throw new Win32Exception(
                result,
                "Unable to inspect the gateway process package generation.");
        }

        return new string(value, 0, checked((int)length - 1));
    }

    private static SafeFileHandle DuplicateForChild(SafeHandle source)
    {
        if (!DuplicateHandle(
                new IntPtr(-1),
                source,
                new IntPtr(-1),
                out SafeFileHandle copy,
                0,
                inherit: true,
                2))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to prepare the gateway log handle.");
        }

        return copy;
    }

    private sealed class InheritedHandles : IDisposable
    {
        private IntPtr _values;

        public InheritedHandles(IntPtr[] handles)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(Attributes, 1, 0, ref size))
            {
                int error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(Attributes);
                Attributes = 0;
                throw new Win32Exception(error);
            }

            try
            {
                _values = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _values, handles.Length);
                if (!UpdateProcThreadAttribute(
                        Attributes,
                        0,
                        0x00020002,
                        _values,
                        (nuint)(handles.Length * IntPtr.Size),
                        0,
                        0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Attributes { get; private set; }

        public void Dispose()
        {
            if (Attributes != 0)
            {
                DeleteProcThreadAttributeList(Attributes);
                Marshal.FreeHGlobal(Attributes);
                Attributes = 0;
            }
            Marshal.FreeHGlobal(_values);
            _values = 0;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        ref int returnLength);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        AddressFamily addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(
        IntPtr process,
        ref uint packageFullNameLength,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        char[]? packageFullName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        SafeHandle source,
        IntPtr targetProcess,
        out SafeFileHandle target,
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint options);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref ExtendedStartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr list,
        int count,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previous,
        IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedStartupInfo
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}

internal sealed class LoopbackGatewayHealthProbe : INativeGatewayHealthProbe
{
    public async Task<NativeGatewayHealthResult> ProbeAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                cancellationToken).ConfigureAwait(false);
            return new NativeGatewayHealthResult(Healthy: true);
        }
        catch (Exception exception) when (
            exception is SocketException or IOException)
        {
            return new NativeGatewayHealthResult(
                Healthy: false,
                $"The loopback gateway endpoint on port {port} was not healthy: " +
                exception.Message);
        }
    }
}
