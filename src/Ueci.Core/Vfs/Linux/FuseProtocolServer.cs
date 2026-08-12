using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Ueci.Vfs.Linux;

internal sealed class FuseProtocolServer : IAsyncDisposable
{
    private readonly VirtualEngineFileSystem _fileSystem;
    private readonly string _socketPath;
    private readonly Action<string>? _progress;
    private readonly bool _verbose;
    private Socket? _listener;
    private readonly List<Task> _connections = [];
    private readonly object _connectionGate = new();

    public FuseProtocolServer(
        VirtualEngineFileSystem fileSystem,
        string socketPath,
        Action<string>? progress = null,
        bool verbose = false)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _socketPath = Path.GetFullPath(socketPath);
        _progress = progress;
        _verbose = verbose;
    }

    public string SocketPath => _socketPath;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null)
        {
            throw new InvalidOperationException("FUSE protocol server is already running.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
        TryDeleteSocket();
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(128);
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Socket listener = _listener ?? throw new InvalidOperationException("Call StartAsync first.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                Task task = HandleConnectionAsync(client, cancellationToken);
                lock (_connectionGate)
                {
                    _connections.Add(task);
                    _connections.RemoveAll(item => item.IsCompleted);
                }
            }
        }
        finally
        {
            Task[] pending;
            lock (_connectionGate)
            {
                pending = _connections.ToArray();
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            string? request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            try
            {
                await DispatchAsync(request, writer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                int errno = MapErrno(ex);
                await writer.WriteLineAsync($"ERR\t{errno}\t{FuseProtocol.Encode(ex.Message)}").ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(string request, StreamWriter writer, CancellationToken cancellationToken)
    {
        string[] fields = request.Split('\t');
        if (fields.Length == 0)
        {
            throw new InvalidDataException("Empty FUSE request.");
        }

        if (_verbose)
        {
            _progress?.Invoke(DescribeRequest(fields));
        }

        switch (fields[0])
        {
            case "STATFS":
            {
                await writer.WriteLineAsync($"OK\t{FuseProtocol.Encode(_fileSystem.UpperRoot)}").ConfigureAwait(false);
                return;
            }
            case "STAT":
            {
                RequireFields(fields, 2);
                string path = FuseProtocol.Decode(fields[1]);
                VirtualEngineMetadata? metadata = await _fileSystem.GetMetadataAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (metadata is null)
                {
                    await writer.WriteLineAsync("ERR\t2\t").ConfigureAwait(false);
                    return;
                }
                await writer.WriteLineAsync(
                    $"OK\t{FuseProtocol.Kind(metadata.Kind)}\t{FuseProtocol.Number(metadata.Size)}\t{FuseProtocol.Mode(metadata.UnixMode)}")
                    .ConfigureAwait(false);
                return;
            }
            case "LIST":
            {
                RequireFields(fields, 2);
                string path = FuseProtocol.Decode(fields[1]);
                IReadOnlyList<VirtualEngineDirectoryEntry> entries = await _fileSystem.ListAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                foreach (VirtualEngineDirectoryEntry entry in entries)
                {
                    await writer.WriteLineAsync(
                        $"E\t{FuseProtocol.Kind(entry.Kind)}\t{FuseProtocol.Number(entry.Size)}\t{FuseProtocol.Mode(entry.UnixMode)}\t{FuseProtocol.Encode(entry.Name)}")
                        .ConfigureAwait(false);
                }
                await writer.WriteLineAsync("END").ConfigureAwait(false);
                return;
            }
            case "RESOLVE":
            {
                RequireFields(fields, 4);
                bool write = fields[1] == "W";
                bool create = fields[2] == "1";
                string path = FuseProtocol.Decode(fields[3]);
                string physical = write
                    ? await _fileSystem.ResolveWriteBackingPathAsync(path, create, cancellationToken).ConfigureAwait(false)
                    : await _fileSystem.ResolveReadBackingPathAsync(path, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync($"OK\t{FuseProtocol.Encode(physical)}").ConfigureAwait(false);
                return;
            }
            case "READLINK":
            {
                RequireFields(fields, 2);
                string target = await _fileSystem.ReadSymbolicLinkAsync(FuseProtocol.Decode(fields[1]), cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync($"OK\t{FuseProtocol.Encode(target)}").ConfigureAwait(false);
                return;
            }
            case "MKDIR":
            {
                RequireFields(fields, 3);
                int mode = int.Parse(fields[1], CultureInfo.InvariantCulture);
                await _fileSystem.CreateDirectoryAsync(FuseProtocol.Decode(fields[2]), mode, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
            case "UNLINK":
            case "RMDIR":
            {
                RequireFields(fields, 2);
                await _fileSystem.DeleteAsync(
                    FuseProtocol.Decode(fields[1]),
                    directory: fields[0] == "RMDIR",
                    cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
            case "RENAME":
            {
                RequireFields(fields, 3);
                await _fileSystem.RenameAsync(
                    FuseProtocol.Decode(fields[1]),
                    FuseProtocol.Decode(fields[2]),
                    cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
            case "SYMLINK":
            {
                RequireFields(fields, 3);
                await _fileSystem.CreateSymbolicLinkAsync(
                    FuseProtocol.Decode(fields[1]),
                    FuseProtocol.Decode(fields[2]),
                    cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
            case "CHMOD":
            {
                RequireFields(fields, 3);
                int mode = int.Parse(fields[1], CultureInfo.InvariantCulture);
                await _fileSystem.ChmodAsync(FuseProtocol.Decode(fields[2]), mode, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
            default:
                throw new InvalidDataException($"Unknown FUSE protocol command '{fields[0]}'.");
        }
    }

    private static string DescribeRequest(string[] fields)
    {
        try
        {
            return fields[0] switch
            {
                "STATFS" => "[vfs/fuse] STATFS",
                "STAT" or "LIST" or "READLINK" or "UNLINK" or "RMDIR"
                    => $"[vfs/fuse] {fields[0]} {FuseProtocol.Decode(fields[1])}",
                "RESOLVE" => $"[vfs/fuse] RESOLVE {(fields[1] == "W" ? "write" : "read")} {FuseProtocol.Decode(fields[3])}",
                "MKDIR" or "CHMOD" => $"[vfs/fuse] {fields[0]} {FuseProtocol.Decode(fields[2])}",
                "RENAME" => $"[vfs/fuse] RENAME {FuseProtocol.Decode(fields[1])} -> {FuseProtocol.Decode(fields[2])}",
                "SYMLINK" => $"[vfs/fuse] SYMLINK {FuseProtocol.Decode(fields[2])} -> {FuseProtocol.Decode(fields[1])}",
                _ => $"[vfs/fuse] {fields[0]}",
            };
        }
        catch
        {
            return $"[vfs/fuse] {fields[0]} (malformed request details)";
        }
    }

    private static int MapErrno(Exception ex) => ex switch
    {
        FileNotFoundException => 2,       // ENOENT
        DirectoryNotFoundException => 2,
        UnauthorizedAccessException => 13, // EACCES
        NotSupportedException => 95,      // EOPNOTSUPP
        ArgumentException => 22,          // EINVAL
        IOException io when io.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => 17, // EEXIST
        IOException io when io.Message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => 39,       // ENOTEMPTY
        IOException io when io.Message.Contains("is a directory", StringComparison.OrdinalIgnoreCase) => 21,  // EISDIR
        IOException io when io.Message.Contains("not a directory", StringComparison.OrdinalIgnoreCase) => 20, // ENOTDIR
        IOException => 5,                 // EIO
        _ => 5,
    };

    private static void RequireFields(string[] fields, int count)
    {
        if (fields.Length < count)
        {
            throw new InvalidDataException($"Malformed FUSE protocol request '{fields[0]}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            try { _listener.Dispose(); } catch { }
            _listener = null;
        }
        Task[] pending;
        lock (_connectionGate)
        {
            pending = _connections.ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        TryDeleteSocket();
    }

    private void TryDeleteSocket()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch { }
    }
}
