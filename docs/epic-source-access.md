# Epic source access

UECI assumes the user is already entitled to access `EpicGames/UnrealEngine` through their linked GitHub account.

## Token model

Default variable:

```text
UECI_EPIC_GITHUB_TOKEN
```

The token must be capable of **read-only repository access** for `EpicGames/UnrealEngine`. UECI does not need write access to Epic's repository.

The token is injected into Git using transient process environment configuration (`http.https://github.com/.extraheader`). It is not embedded in:

- the remote URL;
- `.git/config`;
- `.ueci.yml`;
- command-line arguments emitted by UECI;
- normal command output.

A different secret variable can be selected with `--token-env NAME`.

## GitHub Actions

Pass a repository/organization secret into the action input or environment. Do not expose it to arbitrary fork pull-request code. Credentialed jobs should run only where the checked-out code and workflow are trusted.

## Caches

Any cache containing Unreal Engine source or dependency payloads should be treated as private licensed material. UECI should prefer local/self-hosted caches or access-controlled CI caches and must not publish engine payloads as public build artifacts.
