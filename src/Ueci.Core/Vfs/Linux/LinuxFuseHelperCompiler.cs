using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Ueci.Vfs.Linux;

public sealed class LinuxFuseHelperCompiler
{
    public async Task<string> EnsureCompiledAsync(
        string cacheRoot,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The libfuse3 backend is Linux-only.");
        }

        byte[] sourceBytes = ReadEmbeddedSource();
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant()[..16];
        string buildRoot = Path.Combine(Path.GetFullPath(cacheRoot), "native", "fuse3", sourceHash);
        string sourcePath = Path.Combine(buildRoot, "ueci-fuse-helper.c");
        string binaryPath = Path.Combine(buildRoot, "ueci-fuse-helper");
        if (File.Exists(binaryPath))
        {
            return binaryPath;
        }

        Directory.CreateDirectory(buildRoot);
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, cancellationToken).ConfigureAwait(false);

        ProcessResult pkg = await RunAsync("pkg-config", buildRoot, ["--cflags", "--libs", "fuse3"], cancellationToken)
            .ConfigureAwait(false);
        if (pkg.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "libfuse3 development files are required for 'ueci mount'. Install fuse3/pkg-config for your distribution. " +
                pkg.StandardError.Trim());
        }

        string compiler = FindCompiler();
        string tempBinary = binaryPath + $".{Guid.NewGuid():N}.tmp";
        var args = new List<string>
        {
            "-std=c11",
            "-O2",
            "-Wall",
            "-Wextra",
            "-Werror=implicit-function-declaration",
            sourcePath,
            "-o",
            tempBinary,
        };
        args.AddRange(SplitFlags(pkg.StandardOutput));

        progress?.Invoke($"Compiling embedded libfuse3 helper with {Path.GetFileName(compiler)}...");
        ProcessResult build = await RunAsync(compiler, buildRoot, args, cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            TryDelete(tempBinary);
            throw new InvalidOperationException(
                $"Unable to compile the embedded UECI FUSE helper.{Environment.NewLine}{build.StandardError.Trim()}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempBinary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.Move(tempBinary, binaryPath, overwrite: true);
        return binaryPath;
    }

    private static byte[] ReadEmbeddedSource()
    {
        Assembly assembly = typeof(LinuxFuseHelperCompiler).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("ueci-fuse-helper.c", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded FUSE helper source is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string FindCompiler()
    {
        foreach (string candidate in new[] { "cc", "clang", "gcc" })
        {
            if (ExecutableExists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("A C compiler (cc, clang or gcc) is required to build the embedded FUSE helper.");
    }

    private static bool ExecutableExists(string executable)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable))
            .Any(File.Exists);
    }

    internal static IReadOnlyList<string> SplitFlags(string value)
    {
        // pkg-config's fuse3 output is normally simple -I/-L/-l switches. Handle quotes as well so
        // custom prefixes containing spaces still work without invoking a shell.
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool escape = false;
        foreach (char ch in value)
        {
            if (escape)
            {
                current.Append(ch);
                escape = false;
                continue;
            }
            if (ch == '\\' && quote != '\'')
            {
                escape = true;
                continue;
            }
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else current.Append(ch);
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (current.Length != 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (escape) current.Append('\\');
        if (quote != '\0') throw new InvalidDataException("Unterminated quote in pkg-config output.");
        if (current.Length != 0) result.Add(current.ToString());
        return result;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
