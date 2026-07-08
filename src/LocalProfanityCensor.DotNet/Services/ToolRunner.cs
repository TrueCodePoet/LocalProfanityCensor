using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ToolRunner
{
    public sealed record CommandProgressInfo(string Stage, string Message, string? Detail = null, string? FileName = null, string? Mode = null);

    public static void EnsureToolExists(string toolName)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (paths.SelectMany(path => CandidatePaths(path, toolName)).Any(File.Exists))
        {
            return;
        }

        throw new InvalidOperationException($"Required executable was not found on PATH: {toolName}");
    }

    public static async Task<CommandResult> RunCaptureAsync(string fileName, params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables: null, timeout: null, progressInfo: null, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(string fileName, TimeSpan timeout, params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables: null, timeout, progressInfo: null, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(string fileName, CommandProgressInfo progressInfo, params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables: null, timeout: null, progressInfo, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(string fileName, TimeSpan timeout, CommandProgressInfo progressInfo, params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables: null, timeout, progressInfo, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables, timeout: null, progressInfo: null, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        TimeSpan timeout,
        params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables, timeout, progressInfo: null, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CommandProgressInfo progressInfo,
        params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables, timeout: null, progressInfo, arguments);
    }

    public static async Task<CommandResult> RunCaptureAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        TimeSpan timeout,
        CommandProgressInfo progressInfo,
        params string[] arguments)
    {
        return await RunCaptureCoreAsync(fileName, environmentVariables, timeout, progressInfo, arguments);
    }

    private static async Task<CommandResult> RunCaptureCoreAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        TimeSpan? timeout,
        CommandProgressInfo? progressInfo,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var childProcessScope = ChildProcessScope.TryCreate(process);
        ReportProcessStatus(progressInfo, $"Started {Path.GetFileName(fileName)}", $"pid {process.Id}");

        var standardOutputBuilder = new StringBuilder();
        var standardErrorBuilder = new StringBuilder();
        var standardOutputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var standardErrorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                standardOutputClosed.TrySetResult();
                return;
            }

            standardOutputBuilder.AppendLine(eventArgs.Data);
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                standardErrorClosed.TrySetResult();
                return;
            }

            standardErrorBuilder.AppendLine(eventArgs.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ReportProcessStatus(progressInfo, progressInfo?.Message ?? $"Waiting for {Path.GetFileName(fileName)}", progressInfo?.Detail);

        if (timeout is { } effectiveTimeout)
        {
            var waitForExitTask = process.WaitForExitAsync();
            var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(effectiveTimeout));
            if (!ReferenceEquals(completedTask, waitForExitTask))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                TryCancelRead(process.CancelOutputRead);
                TryCancelRead(process.CancelErrorRead);
                var timedOutOutput = standardOutputBuilder.ToString();
                var timedOutError = standardErrorBuilder.ToString();
                var timeoutDetail = string.IsNullOrWhiteSpace(timedOutError) ? timedOutOutput : timedOutError;
                ReportProcessStatus(progressInfo, $"Timed out running {Path.GetFileName(fileName)}", $"timeout {effectiveTimeout.TotalSeconds:F0}s");
                throw new TimeoutException(
                    $"Command timed out after {effectiveTimeout.TotalSeconds:F0}s: {fileName} {string.Join(' ', arguments)}"
                    + (string.IsNullOrWhiteSpace(timeoutDetail) ? string.Empty : $"{Environment.NewLine}{timeoutDetail.Trim()}"));
            }

            await waitForExitTask;
        }
        else
        {
            await process.WaitForExitAsync();
        }

        ReportProcessStatus(progressInfo, $"Finished {Path.GetFileName(fileName)}", $"exit {process.ExitCode}");
        await Task.WhenAll(standardOutputClosed.Task, standardErrorClosed.Task);
        ReportProcessStatus(progressInfo, $"Collected output from {Path.GetFileName(fileName)}", progressInfo?.Detail);

        var result = new CommandResult(
            process.ExitCode,
            standardOutputBuilder.ToString(),
            standardErrorBuilder.ToString());

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"Command failed: {fileName} {string.Join(' ', arguments)}{Environment.NewLine}{detail.Trim()}");
        }

        return result;
    }

    public static async Task RunAsync(string fileName, params string[] arguments)
    {
        await RunCaptureAsync(fileName, arguments);
    }

    public static async Task RunAsync(string fileName, CommandProgressInfo progressInfo, params string[] arguments)
    {
        await RunCaptureAsync(fileName, progressInfo, arguments);
    }

    public static async Task RunAsync(
        string fileName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CommandProgressInfo progressInfo,
        params string[] arguments)
    {
        await RunCaptureAsync(fileName, environmentVariables, progressInfo, arguments);
    }

    private static void ReportProcessStatus(CommandProgressInfo? progressInfo, string fallbackMessage, string? fallbackDetail)
    {
        if (progressInfo is null)
        {
            ProgressReporter.Report(fallbackMessage + (string.IsNullOrWhiteSpace(fallbackDetail) ? string.Empty : $" ({fallbackDetail})"));
            return;
        }

        ProgressReporter.ReportStage(
            progressInfo.Stage,
            fallbackMessage,
            fileName: progressInfo.FileName,
            mode: progressInfo.Mode,
            detail: string.IsNullOrWhiteSpace(fallbackDetail) ? progressInfo.Detail : fallbackDetail);
    }

    private static IEnumerable<string> CandidatePaths(string directory, string toolName)
    {
        yield return Path.Combine(directory, toolName);
        yield return Path.Combine(directory, toolName + ".exe");
    }

    private static void TryCancelRead(Action cancelRead)
    {
        try
        {
            cancelRead();
        }
        catch
        {
        }
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ChildProcessScope : IDisposable
{
    private IntPtr _jobHandle;

    private ChildProcessScope(IntPtr jobHandle)
    {
        _jobHandle = jobHandle;
    }

    public static ChildProcessScope? TryCreate(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var infoLength = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPointer = Marshal.AllocHGlobal(infoLength);
            try
            {
                Marshal.StructureToPtr(info, infoPointer, false);
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, infoPointer, (uint)infoLength))
                {
                    CloseHandle(jobHandle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPointer);
            }

            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                CloseHandle(jobHandle);
                return null;
            }

            return new ChildProcessScope(jobHandle);
        }
        catch
        {
            CloseHandle(jobHandle);
            return null;
        }
    }

    public void Dispose()
    {
        if (_jobHandle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_jobHandle);
        _jobHandle = IntPtr.Zero;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int jobObjectInfoClass, IntPtr jobObjectInfo, uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}