using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ueci.Vfs.Linux;

public sealed record LinuxFuseMountOptions(
    string MountPoint,
    string CacheDirectory,
    bool Verbose = false,
    TimeSpan? StartupTimeout = null,
    Action<string>? Progress = null);

public sealed class LinuxFuseMountSession : IEngineMountSession
{
    private readonly Process _process;
    private readonly FuseProtocolServer _server;
    private readonly CancellationTokenSource _serverCancellation;
    private readonly Task _serverTask;
    private int _disposed;

    internal LinuxFuseMountSession(
        string mountPoint,
        Process process,
        FuseProtocolServer server,
        CancellationTokenSource serverCancellation,
        Task serverTask)
    {
        MountPoint = mountPoint;
        _process = process;
        _server = server;
        _serverCancellation = serverCancellation;
        _serverTask = serverTask;
    }

    public string MountPoint { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Try a clean unmount first. If the helper is wedged (for example because the build
        // exhausted its filesystem and the protocol server could no longer answer), terminate the
        // helper and retry with lazy detach. The old order killed the helper after a failed normal
        // unmount but never retried the mount itself, leaving stale engine-view mounts behind.
        await LinuxFuseMount.TryUnmountAsync(MountPoint, lazy: false).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch { }
        }

        await LinuxFuseMount.TryUnmountAsync(MountPoint, lazy: true).ConfigureAwait(false);
        _serverCancellation.Cancel();
        try { await _serverTask.ConfigureAwait(false); } catch { }
        await _server.DisposeAsync().ConfigureAwait(false);
        _serverCancellation.Dispose();
        _process.Dispose();
    }
}

public sealed class LinuxFuseMount
{
    public async Task<LinuxFuseMountSession> StartAsync(
        VirtualEngineFileSystem fileSystem,
        LinuxFuseMountOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The mounted backend requires Linux/FUSE3 or macOS/macFUSE.");
        }

        string mountPoint = Path.GetFullPath(options.MountPoint);
        Directory.CreateDirectory(mountPoint);
        if (IsMounted(mountPoint))
        {
            throw new InvalidOperationException($"FUSE mount point is already mounted: {mountPoint}");
        }
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
        {
            throw new InvalidOperationException($"FUSE mount point must be empty: {mountPoint}");
        }

        var compiler = new LinuxFuseHelperCompiler();
        string helper = await compiler.EnsureCompiledAsync(options.CacheDirectory, options.Progress, cancellationToken)
            .ConfigureAwait(false);
        string socket = CreateShortSocketPath(options.CacheDirectory, mountPoint);
        var server = new FuseProtocolServer(fileSystem, socket, options.Progress, options.Verbose);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task serverTask = server.RunAsync(serverCancellation.Token);

        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        info.ArgumentList.Add(socket);
        info.ArgumentList.Add(mountPoint);
        var process = new Process { StartInfo = info };
        options.Progress?.Invoke($"Mounting virtual Unreal Engine at {mountPoint}");

        try
        {
            process.Start();
            await WaitUntilMountedAsync(
                mountPoint,
                process,
                options.StartupTimeout ?? DefaultStartupTimeout,
                options.Progress,
                cancellationToken).ConfigureAwait(false);
            options.Progress?.Invoke($"Virtual Unreal Engine mount ready at {mountPoint}.");
            return new LinuxFuseMountSession(mountPoint, process, server, serverCancellation, serverTask);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch { }
            // Startup can fail after libfuse has already attached the mount (for example when the
            // caller is cancelled or the backing filesystem fills). Always detach defensively so
            // a failed matrix row cannot leave an undeletable engine-view behind.
            await TryUnmountAsync(mountPoint, lazy: true).ConfigureAwait(false);
            serverCancellation.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch { }
            await server.DisposeAsync().ConfigureAwait(false);
            serverCancellation.Dispose();
            process.Dispose();
            throw;
        }
    }

    public async Task<int> RunAsync(
        VirtualEngineFileSystem fileSystem,
        LinuxFuseMountOptions options,
        CancellationToken cancellationToken = default)
    {
        await using LinuxFuseMountSession session = await StartAsync(fileSystem, options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task WaitUntilMountedAsync(
        string mountPoint,
        Process process,
        TimeSpan timeout,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan nextProgress = TimeSpan.FromSeconds(5);
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                string macFuseHint = OperatingSystem.IsMacOS()
                    ? " macFUSE reported that its filesystem is unavailable; ensure its system component is approved and active, then restart the terminal session."
                    : string.Empty;
                throw new InvalidOperationException(
                    $"FUSE helper exited before the mount became ready (exit {process.ExitCode}).{macFuseHint}");
            }
            if (IsMounted(mountPoint))
            {
                return;
            }
            if (stopwatch.Elapsed >= nextProgress)
            {
                progress?.Invoke($"Waiting for FUSE mount readiness... {stopwatch.Elapsed.TotalSeconds:N0}s elapsed.");
                nextProgress += TimeSpan.FromSeconds(5);
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"FUSE mount did not become ready within {timeout.TotalSeconds:N0}s: {mountPoint}");
    }

    private static TimeSpan DefaultStartupTimeout
        => OperatingSystem.IsMacOS() ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(2);

    private static bool IsMounted(string mountPoint)
    {
        if (OperatingSystem.IsMacOS()) return IsMountedOnMacOs(mountPoint);
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) return false;

        string target = Path.GetFullPath(mountPoint);
        try
        {
            foreach (string line in File.ReadLines("/proc/self/mountinfo"))
            {
                string[] fields = line.Split(' ');
                if (fields.Length < 5)
                {
                    continue;
                }
                string candidate = UnescapeMountInfo(fields[4]);
                if (string.Equals(Path.GetFullPath(candidate), target, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsMountedOnMacOs(string mountPoint)
    {
        string target = Path.GetFullPath(mountPoint);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("/sbin/mount")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n').Any(line => line.Contains($" on {target} ", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string UnescapeMountInfo(string value)
        => value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static string CreateShortSocketPath(string cacheDirectory, string mountPoint)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.ProcessId}:{mountPoint}:{Guid.NewGuid():N}"));
        string token = Convert.ToHexString(digest).ToLowerInvariant()[..16];
        string directory = Path.Combine(Path.GetFullPath(cacheDirectory), "tmp", "sockets");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"ueci-{token}.sock");
    }

    internal static async Task TryUnmountAsync(string mountPoint, bool lazy = false)
    {
        string[] fuseArgs = lazy ? ["-u", "-z", mountPoint] : ["-u", mountPoint];
        string[] umountArgs = lazy ? ["-f", mountPoint] : [mountPoint];
        foreach ((string exe, string[] args) in new[]
        {
            OperatingSystem.IsLinux() ? ("fusermount3", fuseArgs) : ("/sbin/umount", umountArgs),
        })
        {
            try
            {
                var info = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using var process = new Process { StartInfo = info };
                process.Start();
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 0) return;
            }
            catch { }
        }
    }
}
