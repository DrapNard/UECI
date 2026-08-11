using System.Diagnostics;
using System.Text;

namespace Ueci.Epic;

internal sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class GitProcess
{
    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var info = CreateStartInfo(workingDirectory, arguments, environment);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new GitProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    public static async Task RunBinaryToFileAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string outputPath,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var info = CreateStartInfo(workingDirectory, arguments, environment);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using FileStream output = File.Create(outputPath);
        using var process = new Process { StartInfo = info };
        process.Start();

        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            output.Close();
            File.Delete(outputPath);
            throw new InvalidOperationException($"git exited with code {process.ExitCode}: {stderr.Trim()}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
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

        return info;
    }
}
