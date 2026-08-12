using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Ueci.Epic;

public sealed class EpicGitBlobStore : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly string? _tokenEnvironmentVariable;
    private readonly string _cacheRoot;
    private readonly Action<string>? _progress;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private Process? _batchProcess;
    private StreamWriter? _batchInput;
    private Stream? _batchOutput;

    public EpicGitBlobStore(
        string repositoryRoot,
        string cacheRoot,
        string? tokenEnvironmentVariable = null,
        Action<string>? progress = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _tokenEnvironmentVariable = tokenEnvironmentVariable;
        _progress = progress;
    }

    public async Task<string> EnsureAsync(
        EpicGitTreeEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string directory = Path.Combine(_cacheRoot, "git-blobs");
        string destination = Path.Combine(directory, ValidateObjectId(entry.ObjectId));
        if (IsPlausible(destination, entry.Size))
        {
            return destination;
        }

        SemaphoreSlim gate = _locks.GetOrAdd(entry.ObjectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPlausible(destination, entry.Size))
            {
                return destination;
            }

            Directory.CreateDirectory(directory);
            string temp = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                _progress?.Invoke($"[vfs/git] CAS miss: {entry.Path}");
                await MaterializeObjectWithBatchProcessAsync(entry, temp, cancellationToken).ConfigureAwait(false);
                if (!IsPlausible(temp, entry.Size))
                {
                    throw new InvalidDataException(
                        $"Epic Git blob '{entry.ObjectId}' materialized with unexpected size for '{entry.Path}'.");
                }
                File.Move(temp, destination, overwrite: true);
                ApplyMode(destination, entry.UnixMode);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryGetCachedSize(EpicGitTreeEntry entry, out long size)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path = Path.Combine(_cacheRoot, "git-blobs", ValidateObjectId(entry.ObjectId));
        if (File.Exists(path))
        {
            size = new FileInfo(path).Length;
            return true;
        }
        size = 0;
        return false;
    }

    private async Task MaterializeObjectWithBatchProcessAsync(
        EpicGitTreeEntry entry,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureBatchProcess();
                    await _batchInput!.WriteLineAsync(ValidateObjectId(entry.ObjectId)).ConfigureAwait(false);
                    await _batchInput.FlushAsync(cancellationToken).ConfigureAwait(false);

                    string header = await ReadAsciiLineAsync(_batchOutput!, cancellationToken).ConfigureAwait(false);
                    string[] fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != 3
                        || !string.Equals(fields[0], entry.ObjectId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(fields[1], "blob", StringComparison.Ordinal)
                        || !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out long size)
                        || size < 0)
                    {
                        throw new InvalidDataException(
                            $"Unexpected git cat-file --batch header for '{entry.Path}': {header}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                    await using FileStream output = new(
                        outputPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        useAsync: true);
                    await CopyExactlyAsync(_batchOutput!, output, size, cancellationToken).ConfigureAwait(false);
                    int terminator = await ReadByteAsync(_batchOutput!, cancellationToken).ConfigureAwait(false);
                    if (terminator != '\n')
                    {
                        throw new InvalidDataException("git cat-file --batch response was not newline terminated.");
                    }
                    return;
                }
                catch (Exception) when (attempt == 0)
                {
                    ResetBatchProcess();
                }
            }
        }
        finally
        {
            _batchGate.Release();
        }
    }

    private void EnsureBatchProcess()
    {
        if (_batchProcess is { HasExited: false } && _batchInput is not null && _batchOutput is not null)
        {
            return;
        }

        ResetBatchProcess();
        string token = GitHubReadOnlyCredential.GetRequiredToken(_tokenEnvironmentVariable);
        IReadOnlyDictionary<string, string> environment = GitHubReadOnlyCredential.CreateGitEnvironment(token);
        ProcessStartInfo info = GitProcess.CreateStartInfo(_repositoryRoot, ["cat-file", "--batch"], environment);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        // Let git/promisor diagnostics inherit stderr rather than risking a blocked unread pipe.
        info.RedirectStandardError = false;

        var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.NewLine = "\n";
        _batchProcess = process;
        _batchInput = process.StandardInput;
        _batchOutput = process.StandardOutput.BaseStream;
    }

    private void ResetBatchProcess()
    {
        try { _batchInput?.Dispose(); } catch { }
        try { _batchOutput?.Dispose(); } catch { }
        try
        {
            if (_batchProcess is { HasExited: false })
            {
                _batchProcess.Kill(entireProcessTree: true);
                _batchProcess.WaitForExit(1000);
            }
        }
        catch { }
        _batchProcess?.Dispose();
        _batchProcess = null;
        _batchInput = null;
        _batchOutput = null;
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(96);
        byte[] one = new byte[1];
        while (true)
        {
            int count = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("git cat-file --batch closed unexpectedly.");
            }
            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            if (bytes.Count >= 4096)
            {
                throw new InvalidDataException("git cat-file --batch returned an oversized header.");
            }
            bytes.Add(one[0]);
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        int count = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return count == 0 ? -1 : one[0];
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long bytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[128 * 1024];
        long remaining = bytes;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int count = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    $"git cat-file --batch ended with {remaining:N0} blob bytes still expected.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static bool IsPlausible(string path, long expectedSize)
        => File.Exists(path) && (expectedSize < 0 || new FileInfo(path).Length == expectedSize);

    private static string ValidateObjectId(string value)
    {
        if (value.Length != 40 || value.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException($"Invalid Git object id '{value}'.");
        }
        return value.ToLowerInvariant();
    }

    public void Dispose()
    {
        ResetBatchProcess();
        _batchGate.Dispose();
        foreach (SemaphoreSlim gate in _locks.Values)
        {
            gate.Dispose();
        }
    }

    private static void ApplyMode(string path, int unixMode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        int permissions = unixMode & 0x1ff;
        if (permissions == 0)
        {
            // A Git symlink is stored in CAS as a regular file containing its link target.
            // Keep that backing content readable even though Git mode 120000 has no permission bits.
            permissions = 0x1a4;
        }
        File.SetUnixFileMode(path, (UnixFileMode)permissions);
    }
}
