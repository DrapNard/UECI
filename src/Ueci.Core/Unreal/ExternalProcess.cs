using System.Diagnostics;

namespace Ueci.Unreal;

public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

internal static class ExternalProcess
{
    public static async Task<ExternalProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? unsetEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                info.Environment[key] = value;
            }
        }
        if (unsetEnvironment is not null)
        {
            foreach (string key in unsetEnvironment)
            {
                info.Environment.Remove(key);
            }
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ExternalProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }
}
