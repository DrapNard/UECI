using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ueci.Vfs.Linux;

public sealed record LinuxFuseMountOptions(
    string MountPoint,
    string CacheDirectory,
    bool Verbose = false,
    Action<string>? Progress = null);

public sealed class LinuxFuseMount
{
    public async Task<int> RunAsync(
        VirtualEngineFileSystem fileSystem,
        LinuxFuseMountOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The first UECI mounted backend requires Linux + FUSE3.");
        }

        string mountPoint = Path.GetFullPath(options.MountPoint);
        Directory.CreateDirectory(mountPoint);
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
        {
            throw new InvalidOperationException($"FUSE mount point must be empty: {mountPoint}");
        }

        var compiler = new LinuxFuseHelperCompiler();
        string helper = await compiler.EnsureCompiledAsync(options.CacheDirectory, options.Progress, cancellationToken)
            .ConfigureAwait(false);
        string socket = CreateShortSocketPath(mountPoint);
        await using var server = new FuseProtocolServer(fileSystem, socket, options.Progress, options.Verbose);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task serverTask = server.RunAsync(linked.Token);

        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        info.ArgumentList.Add(socket);
        info.ArgumentList.Add(mountPoint);
        using var process = new Process { StartInfo = info };
        options.Progress?.Invoke($"Mounting virtual Unreal Engine at {mountPoint}");
        process.Start();

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            linked.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await TryUnmountAsync(mountPoint).ConfigureAwait(false);
        }
    }

    private static string CreateShortSocketPath(string mountPoint)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.ProcessId}:{mountPoint}:{Guid.NewGuid():N}"));
        string token = Convert.ToHexString(digest).ToLowerInvariant()[..16];
        return Path.Combine(Path.GetTempPath(), $"ueci-{token}.sock");
    }

    private static async Task TryUnmountAsync(string mountPoint)
    {
        foreach ((string exe, string[] args) in new[]
        {
            ("fusermount3", new[] { "-u", mountPoint }),
            ("umount", new[] { mountPoint }),
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
