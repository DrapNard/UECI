using System.Runtime.InteropServices;
using Fsp;
using Fsp.Interop;
using FileInfo = Fsp.Interop.FileInfo;

namespace Ueci.Vfs.Windows;

public sealed record WindowsWinFspMountOptions(
    string MountPoint,
    bool Verbose = false,
    Action<string>? Progress = null);

public sealed class WindowsWinFspMountSession : IEngineMountSession
{
    private readonly FileSystemHost _host;
    private int _disposed;

    internal WindowsWinFspMountSession(string mountPoint, FileSystemHost host)
    {
        MountPoint = mountPoint;
        _host = host;
    }

    public string MountPoint { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _host.Unmount();
            _host.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>WinFsp mounted presentation backed by the shared lazy Engine/COW filesystem.</summary>
public sealed class WindowsWinFspMount
{
    public Task<WindowsWinFspMountSession> StartAsync(
        VirtualEngineFileSystem fileSystem,
        WindowsWinFspMountOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsWinFspAvailability availability = WindowsWinFspProbe.Detect();
        if (!availability.IsAvailable)
        {
            throw new PlatformNotSupportedException(availability.Diagnostic);
        }

        string mountPoint = Path.GetFullPath(options.MountPoint);
        if (Directory.Exists(mountPoint) && Directory.EnumerateFileSystemEntries(mountPoint).Any())
        {
            throw new InvalidOperationException($"WinFsp mount point must be empty: {mountPoint}");
        }

        var host = new FileSystemHost(new WindowsVirtualEngineFileSystem(fileSystem))
        {
            FileSystemName = "UECI",
            FileInfoTimeout = 10_000,
            DirInfoTimeout = 10_000,
            SecurityTimeout = 10_000,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PostCleanupWhenModifiedOnly = true,
        };
        int result = host.MountEx(mountPoint, 0, null, Synchronized: false, DebugLog: options.Verbose ? uint.MaxValue : 0);
        if (result < 0)
        {
            host.Dispose();
            throw new InvalidOperationException($"WinFsp could not mount '{mountPoint}' (NTSTATUS 0x{result:X8}).");
        }
        options.Progress?.Invoke($"Virtual Unreal Engine WinFsp mount ready at {host.MountPoint()}.");
        return Task.FromResult(new WindowsWinFspMountSession(host.MountPoint(), host));
    }
}

internal sealed class WindowsVirtualEngineFileSystem : FileSystemBase
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileDirectoryFile = 0x1;
    private const uint CleanupDelete = 0x1;
    private const uint WriteAccessMask = 0x0001_0006 | 0x0000_0100 | 0x0001_0000;
    private readonly VirtualEngineFileSystem _fileSystem;

    public WindowsVirtualEngineFileSystem(VirtualEngineFileSystem fileSystem) => _fileSystem = fileSystem;

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = new VolumeInfo { TotalSize = 1L << 40, FreeSize = 1L << 39 };
        volumeInfo.SetVolumeLabel("UECI");
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        VirtualEngineMetadata? metadata = Stat(Normalize(fileName));
        fileAttributes = metadata is null ? 0 : Attributes(metadata.Kind);
        return metadata is null ? STATUS_OBJECT_NAME_NOT_FOUND : STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        string path = Normalize(fileName);
        VirtualEngineMetadata? metadata = Stat(path);
        if (metadata is null)
        {
            fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        bool directory = metadata.Kind == VirtualEngineNodeKind.Directory;
        if (directory != ((createOptions & FileDirectoryFile) != 0) && (createOptions & FileDirectoryFile) != 0)
        {
            fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!;
            return STATUS_NOT_A_DIRECTORY;
        }
        var handle = OpenHandle(path, directory, (grantedAccess & WriteAccessMask) != 0, create: false);
        fileNode = fileDesc = handle;
        fileInfo = ToFileInfo(metadata);
        normalizedName = fileName;
        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc,
        out FileInfo fileInfo, out string normalizedName)
    {
        string path = Normalize(fileName);
        bool directory = (createOptions & FileDirectoryFile) != 0;
        try
        {
            if (directory)
            {
                Wait(_fileSystem.CreateDirectoryAsync(path, 0x1ED));
            }
            var handle = OpenHandle(path, directory, write: true, create: !directory);
            if (!directory && allocationSize != 0) handle.Stream!.SetLength((long)allocationSize);
            fileNode = fileDesc = handle;
            fileInfo = ToFileInfo(Stat(path) ?? throw new FileNotFoundException(path));
            normalizedName = fileName;
            return STATUS_SUCCESS;
        }
        catch (IOException) { fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!; return STATUS_OBJECT_NAME_COLLISION; }
        catch (Exception) { fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!; return STATUS_UNEXPECTED_IO_ERROR; }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if ((flags & CleanupDelete) != 0)
        {
            Handle handle = (Handle)fileNode;
            Wait(_fileSystem.DeleteAsync(handle.Path, handle.IsDirectory));
        }
    }

    public override void Close(object fileNode, object fileDesc) => ((Handle)fileNode).Dispose();

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        try
        {
            Handle handle = (Handle)fileNode;
            if (handle.Stream is null) { bytesTransferred = 0; return STATUS_FILE_IS_A_DIRECTORY; }
            byte[] data = new byte[checked((int)length)]; handle.Stream.Position = (long)offset;
            int read = handle.Stream.Read(data, 0, data.Length); Marshal.Copy(data, 0, buffer, read);
            bytesTransferred = (uint)read; return STATUS_SUCCESS;
        }
        catch { bytesTransferred = 0; return STATUS_UNEXPECTED_IO_ERROR; }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        try
        {
            Handle handle = (Handle)fileNode;
            if (handle.Stream is null) { bytesTransferred = 0; fileInfo = default; return STATUS_FILE_IS_A_DIRECTORY; }
            byte[] data = new byte[checked((int)length)]; Marshal.Copy(buffer, data, 0, data.Length);
            handle.Stream.Position = writeToEndOfFile ? handle.Stream.Length : (long)offset;
            handle.Stream.Write(data, 0, data.Length); handle.Stream.Flush();
            bytesTransferred = length; fileInfo = ToFileInfo(Stat(handle.Path)!); return STATUS_SUCCESS;
        }
        catch { bytesTransferred = 0; fileInfo = default; return STATUS_UNEXPECTED_IO_ERROR; }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        VirtualEngineMetadata? metadata = Stat(((Handle)fileNode).Path);
        fileInfo = metadata is null ? default : ToFileInfo(metadata);
        return metadata is null ? STATUS_OBJECT_NAME_NOT_FOUND : STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        try { Handle handle = (Handle)fileNode; handle.Stream!.SetLength((long)newSize); fileInfo = ToFileInfo(Stat(handle.Path)!); return STATUS_SUCCESS; }
        catch { fileInfo = default; return STATUS_UNEXPECTED_IO_ERROR; }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        try { Wait(_fileSystem.RenameAsync(Normalize(fileName), Normalize(newFileName))); ((Handle)fileNode).Path = Normalize(newFileName); return STATUS_SUCCESS; }
        catch { return STATUS_UNEXPECTED_IO_ERROR; }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        DirectoryCursor cursor = context as DirectoryCursor ?? new DirectoryCursor(Wait(_fileSystem.ListAsync(((Handle)fileNode).Path)).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray());
        context = cursor;
        while (cursor.Index < cursor.Entries.Length)
        {
            VirtualEngineDirectoryEntry entry = cursor.Entries[cursor.Index++];
            if (marker is not null && StringComparer.Ordinal.Compare(entry.Name, marker) <= 0) continue;
            fileName = entry.Name; fileInfo = ToFileInfo(entry); return true;
        }
        fileName = null!; fileInfo = default; return false;
    }

    private Handle OpenHandle(string path, bool directory, bool write, bool create)
    {
        if (directory) return new Handle(path, true, null);
        string physical = write ? Wait(_fileSystem.ResolveWriteBackingPathAsync(path, create)) : Wait(_fileSystem.ResolveReadBackingPathAsync(path));
        return new Handle(path, false, new FileStream(physical, create ? FileMode.CreateNew : FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }

    private VirtualEngineMetadata? Stat(string path) => Wait(_fileSystem.GetStatMetadataAsync(path));
    private static T Wait<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();
    private static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
    private static string Normalize(string path) => path.TrimStart('\\', '/').Replace('\\', '/');
    private static uint Attributes(VirtualEngineNodeKind kind) => kind == VirtualEngineNodeKind.Directory ? FileAttributeDirectory : FileAttributeNormal;
    private static FileInfo ToFileInfo(VirtualEngineMetadata metadata) => new() { FileAttributes = Attributes(metadata.Kind), FileSize = (ulong)metadata.Size, AllocationSize = (ulong)metadata.Size, HardLinks = 0 };
    private static FileInfo ToFileInfo(VirtualEngineDirectoryEntry entry) => new() { FileAttributes = Attributes(entry.Kind), FileSize = (ulong)entry.Size, AllocationSize = (ulong)entry.Size, HardLinks = 0 };

    private sealed class Handle(string path, bool isDirectory, FileStream? stream) : IDisposable
    {
        public string Path { get; set; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public FileStream? Stream { get; } = stream;
        public void Dispose() => Stream?.Dispose();
    }
    private sealed class DirectoryCursor(VirtualEngineDirectoryEntry[] entries) { public VirtualEngineDirectoryEntry[] Entries { get; } = entries; public int Index { get; set; } }
}
