namespace Ueci.Vfs;

public interface IEngineMountSession : IAsyncDisposable
{
    string MountPoint { get; }
}
