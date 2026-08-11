using System.Text;

namespace Ueci.Epic;

public static class GitHubReadOnlyCredential
{
    public const string DefaultTokenEnvironmentVariable = "UECI_EPIC_GITHUB_TOKEN";

    public static string GetRequiredToken(string? environmentVariable = null)
    {
        string variable = string.IsNullOrWhiteSpace(environmentVariable)
            ? DefaultTokenEnvironmentVariable
            : environmentVariable;

        string? token = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"GitHub credential not found. Set {variable} to a read-only token that can access EpicGames/UnrealEngine.");
        }

        return token;
    }

    public static IReadOnlyDictionary<string, string> CreateGitEnvironment(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));

        // Git reads these values as ephemeral in-process config. The token is not put in the
        // repository config, remote URL, command-line arguments or UECI logs.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}",
        };
    }
}
