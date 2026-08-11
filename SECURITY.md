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
