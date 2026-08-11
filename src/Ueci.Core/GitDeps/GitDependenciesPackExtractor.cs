using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Ueci.GitDeps;

public static class GitDependenciesPackExtractor
{
    private static readonly byte[] KnownMagic = Encoding.ASCII.GetBytes("UEPACK00");

    public static async Task ExtractAsync(
        Stream compressedPack,
        GitDependencyPack pack,
        IReadOnlyCollection<GitDependencyBlob> blobs,
        Func<GitDependencyBlob, string> outputPathSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compressedPack);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(outputPathSelector);

        GitDependencyBlob[] ordered = blobs
            .GroupBy(blob => blob.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(blob => blob.PackOffset)
            .ToArray();

        if (ordered.Length == 0)
        {
            return;
        }

        foreach (GitDependencyBlob blob in ordered)
        {
            if (!string.Equals(blob.PackHash, pack.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Blob '{blob.Hash}' belongs to pack '{blob.PackHash}', not '{pack.Hash}'.");
            }

            long end;
            try
            {
                end = checked(blob.PackOffset + blob.Size);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"Blob '{blob.Hash}' has an invalid offset/size.", ex);
            }

            if (blob.PackOffset < KnownMagic.Length || blob.Size < 0 || end > pack.Size)
            {
                throw new InvalidDataException(
                    $"Blob '{blob.Hash}' range [{blob.PackOffset}, {end}) is outside pack '{pack.Hash}' ({pack.Size} bytes).");
            }
        }

        using var gzip = new GZipStream(compressedPack, CompressionMode.Decompress, leaveOpen: true);
        byte[] magic = new byte[KnownMagic.Length];
        await ReadExactlyAsync(gzip, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(KnownMagic))
        {
            string printable = Encoding.ASCII.GetString(magic);
            throw new InvalidDataException(
                $"Unsupported GitDependencies pack magic '{printable}'. Expected 'UEPACK00'.");
        }

        long position = KnownMagic.Length;
        foreach (GitDependencyBlob blob in ordered)
        {
            if (blob.PackOffset < position)
            {
                throw new InvalidDataException(
                    $"Selected blobs in pack '{pack.Hash}' overlap or are out of order near '{blob.Hash}'.");
            }

            await SkipExactlyAsync(gzip, blob.PackOffset - position, cancellationToken).ConfigureAwait(false);
            position = blob.PackOffset;

            string outputPath = outputPathSelector(blob);
            string? parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            string tempPath = outputPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream output = new(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    string actualHash = await CopyExactlyAndHashAsync(
                        gzip,
                        output,
                        blob.Size,
                        cancellationToken).ConfigureAwait(false);

                    if (!string.Equals(actualHash, blob.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"SHA-1 mismatch for blob '{blob.Hash}': got '{actualHash}'.");
                    }
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            position = checked(blob.PackOffset + blob.Size);
        }
    }

    private static async Task<string> CopyExactlyAndHashAsync(
        Stream input,
        Stream output,
        long count,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[128 * 1024];
        long remaining = count;

        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Pack ended with {remaining} blob bytes still expected.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task SkipExactlyAsync(
        Stream stream,
        long count,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Pack ended while skipping {remaining} bytes.");
            }
            remaining -= read;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Pack ended before its header was complete.");
            }
            offset += read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original extraction failure.
        }
    }
}
