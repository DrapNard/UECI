# Security policy

## Reporting

Please report security-sensitive issues privately to the repository maintainers instead of attaching live credentials or private Unreal content to a public issue.

## Credential handling guarantees

UECI's Epic Git path is designed so that tokens are:

- accepted from an environment variable;
- passed to Git via transient process environment configuration;
- never deliberately printed;
- never written into Git remotes or UECI config.

UECI cannot protect a secret from a malicious process already running with the same user privileges. Treat credentialed CI workers as trusted execution environments.

## Fork pull requests

Never run untrusted pull-request code in a job that also has `UECI_EPIC_GITHUB_TOKEN`. Split public validation from credentialed Unreal builds.

## UnrealBuildTool rule execution

`ueci ubt run` executes Epic's UnrealBuildTool. UBT can compile and load C# target/module rule assemblies (`*.Target.cs`, `*.Build.cs`) from the engine or project being evaluated. Treat those rule files as executable build code. Do not combine an Epic credential (or any other high-value CI secret) with arbitrary untrusted plugin/project rules in the same job.
