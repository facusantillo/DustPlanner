using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DustPlanner.HdtExporter.Service;

/// <summary>
/// Starts <c>dustplanner-host.exe</c> (the DustPlanner service: panel, JSON API, OBS overlay, automatic sync) next to
/// the plugin and ties its lifetime to HDT's through a Job Object, so closing HDT — or HDT crashing — always ends the
/// service too. If a service for the same data folder is already answering (started by hand, or by a previous HDT
/// that is still shutting down), it is reused instead.
/// </summary>
internal sealed class ServiceLauncher : IDisposable
{
    private const string ExecutableName = "dustplanner-host.exe";
    private const string ServiceFileName = "service.json";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);

    private readonly string _rootDirectory;
    private readonly int _port;
    private readonly string? _configuredPath;
    private readonly object _gate = new object();
    private Process? _process;
    private JobObject? _job;
    private string? _url;

    public ServiceLauncher(string rootDirectory, int port, string? configuredPath)
    {
        _rootDirectory = rootDirectory;
        _port = port;
        _configuredPath = configuredPath;
    }

    /// <summary>Base URL of the running service (<c>http://127.0.0.1:&lt;port&gt;</c>), or null while it is not up.</summary>
    public string? Url => _url;

    public bool IsRunning => _url is not null && (_process is null || !_process.HasExited);

    /// <summary>Human-readable status for the plugin menu.</summary>
    public string Status { get; private set; } = "Service not started";

    /// <summary>Where the executable is expected: the configured path, else <c>service\dustplanner-host.exe</c> next to the plugin.</summary>
    public string ExecutablePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_configuredPath))
            {
                return Environment.ExpandEnvironmentVariables(_configuredPath!);
            }

            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.Combine(pluginDir, "service", ExecutableName);
        }
    }

    /// <summary>Starts (or adopts) the service in the background; <paramref name="onStatus"/> receives progress and the final verdict.</summary>
    public void StartInBackground(Action<string> onStatus)
    {
        Task.Run(async () =>
        {
            try
            {
                await StartAsync(onStatus).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Status = "Service failed to start: " + ex.Message;
                PluginLog.Error("Service start failed.", ex);
                onStatus(Status);
            }
        });
    }

    private async Task StartAsync(Action<string> onStatus)
    {
        // 1. A service for this data folder may already be running (service.json is written by the service itself).
        var existing = await ReadLiveServiceAsync().ConfigureAwait(false);
        if (existing is not null)
        {
            _url = existing;
            Status = "Service already running at " + existing;
            PluginLog.Info(Status);
            onStatus(Status);
            return;
        }

        var exe = ExecutablePath;
        if (!File.Exists(exe))
        {
            Status = "Service not found: " + exe + " (reinstall the plugin zip, which includes it)";
            PluginLog.Warn(Status);
            onStatus(Status);
            return;
        }

        // 2. Start it hidden, bound to a job that kills it when HDT's process goes away.
        var info = new ProcessStartInfo(exe)
        {
            Arguments = "--home \"" + _rootDirectory + "\" --port " + _port,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? _rootDirectory,
        };

        Process process;
        lock (_gate)
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
            _process = process;
            _job ??= JobObject.TryCreateKillOnClose();
            if (_job is not null && !_job.TryAssign(process))
            {
                PluginLog.Warn("Could not bind the service to HDT's lifetime (job object); it is stopped explicitly on unload instead.");
            }
        }

        Status = "Service starting…";
        onStatus(Status);
        PluginLog.Info("Started " + exe + " (pid " + process.Id + ") for " + _rootDirectory);

        // 3. Wait for it to answer.
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                Status = "Service exited with code " + process.ExitCode + " (see the service output; is the port in use?)";
                PluginLog.Warn(Status);
                onStatus(Status);
                return;
            }

            var url = await ReadLiveServiceAsync().ConfigureAwait(false);
            if (url is not null)
            {
                _url = url;
                Status = "Service running at " + url;
                PluginLog.Info(Status);
                onStatus(Status);
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        Status = "Service did not answer within " + StartupTimeout.TotalSeconds + " s";
        PluginLog.Warn(Status);
        onStatus(Status);
    }

    /// <summary>The URL in <c>service.json</c> when the service behind it answers <c>/api/service</c> for this data folder.</summary>
    private async Task<string?> ReadLiveServiceAsync()
    {
        var file = Path.Combine(_rootDirectory, ServiceFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = JObject.Parse(File.ReadAllText(file));
            var url = json.Value<string>("url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var answer = await http.GetStringAsync(url + "/api/service").ConfigureAwait(false);
            var service = JObject.Parse(answer);
            var home = service.Value<string>("home");
            return home is not null && string.Equals(Path.GetFullPath(home), Path.GetFullPath(_rootDirectory), StringComparison.OrdinalIgnoreCase) ? url : null;
        }
        catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException || ex is Newtonsoft.Json.JsonException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stops the service this plugin started (a reused one is left alone).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try
            {
                if (_process is not null && !_process.HasExited)
                {
                    _process.Kill();
                    PluginLog.Info("Service stopped.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                PluginLog.Warn("Could not stop the service: " + ex.Message);
            }

            _process?.Dispose();
            _process = null;
            _url = null;
            _job?.Dispose();
            _job = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Windows Job Object with KILL_ON_JOB_CLOSE: every process assigned to it dies when the last handle closes, i.e. with HDT.</summary>
    private sealed class JobObject : IDisposable
    {
        private IntPtr _handle;

        private JobObject(IntPtr handle)
        {
            _handle = handle;
        }

        public static JobObject? TryCreateKillOnClose()
        {
            try
            {
                var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!NativeMethods.SetInformationJobObject(handle, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)length))
                    {
                        NativeMethods.CloseHandle(handle);
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return new JobObject(handle);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return null;
            }
        }

        public bool TryAssign(Process process)
        {
            try
            {
                return NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
