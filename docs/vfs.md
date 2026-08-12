# Mounted VFS (v0.5 alpha)

UECI v0.5 introduces a real Linux/FUSE3 backend. It presents the selected Unreal commit as a complete filesystem namespace while source and dependency file contents remain lazy.

## Backend matrix

| Host | Preferred mounted backend | Fallback |
|---|---|---|
| Linux | FUSE 3 (implemented in v0.5 alpha) | materialized directory |
| Windows | WinFsp (planned) | materialized directory |
| macOS | macFUSE/FSKit path to be validated | materialized directory |

Mounted mode is optional. Hosted CI environments that do not expose `/dev/fuse` continue to use the materialized backend.

## Linux architecture

```text
                         UBT / clang / shell
                                  │
                      normal stat/open/read/write
                                  │
                                  ▼
                         FUSE kernel interface
                                  │
                         tiny native helper
                                  │ Unix socket
                                  ▼
                         Ueci.Core VFS server
                                  │
              ┌───────────────────┼────────────────────┐
              │                   │                    │
       writable upper       GitDependencies         Epic Git
       + whiteouts              provider             provider
              │                   │                    │
              │             verified blob CAS       Git blob CAS
              │                   │                    │
              └───────────────────┴────────────────────┘
```

The helper uses libfuse's synchronous high-level API. A filesystem request completes only when its callback returns, which naturally gives UECI the behavior needed for lazy fetches: the thread performing the first `open()` waits while UECI obtains the immutable backing blob, while unrelated FUSE requests may be handled by other helper threads.

The native helper does **not** contain Unreal/GitDependencies logic. It forwards metadata and path-resolution requests to the .NET daemon over a Unix-domain socket. Once an `open()` is resolved, the helper keeps a real backing file descriptor and serves `read`/`write` with `pread`/`pwrite`.

## Namespace construction

At mount preparation time UECI performs metadata-only work:

1. fetch the selected Epic ref with `--filter=blob:none`;
2. parse `git ls-tree -r -l -z` for all tracked blob names, object IDs, modes and sizes;
3. load `Commit.gitdeps.xml`;
4. merge both namespaces with **GitDependencies taking precedence over Git**, matching a normal Setup overlay;
5. infer directory entries from the indexed paths.

`stat()` and `readdir()` therefore do not need file content and do not trigger Engine source downloads.

## Lazy reads

```text
open("Engine/Source/.../Foo.h")
        │
        ├── writable upper exists ─────► open upper
        │
        └── immutable lower
                  │
          ┌───────┴────────┐
          │                │
       Git path      GitDependencies path
          │                │
      CAS hit?          CAS hit?
       │   │             │   │
      yes  no           yes  no
       │   │             │   │
       │ git cat-file     │ fetch gzip pack
       │   │             │ extract + SHA-1 verify
       └───┴─────────────┴────► backing blob fd
```

The Git CAS is keyed by the pinned Git object ID. The GitDependencies CAS reuses the existing verified blob cache and pack extractor. Compressed packs may still be discarded with `--no-pack-cache`; verified blobs remain reusable.

## Writable copy-on-write layer

The lower namespace is immutable. Generated files and mutations are stored outside the FUSE mount in a persistent upper directory.

- new file: create in upper;
- write existing lower file: fetch lower if needed, copy-up, then write upper;
- delete lower file/directory: persistent whiteout;
- recreate a whiteouted path: remove the whiteout and create upper content;
- rename an immutable lower file: copy-up then rename + source whiteout;
- symlinks and Unix modes are represented explicitly.

The first MVP intentionally rejects renaming an immutable lower **directory**; normal UBT build output does not need that operation, and implementing recursive directory copy-up would defeat lazy behavior.

## Native helper

`ueci mount` embeds the helper C source inside `Ueci.Core`. On Linux it is compiled once into the UECI cache with:

```text
cc -std=c11 ... $(pkg-config --cflags --libs fuse3)
```

No prebuilt native binary is committed. The runtime requirements are `pkg-config`, libfuse3 development files/library, and a C compiler.

The helper implements the operations needed by normal build tools in the first MVP: `getattr`, `readdir`, `readlink`, `open`, `create`, `read`, `write`, `flush`, `fsync`, `release`, `mkdir`, `unlink`, `rmdir`, `rename`, `symlink`, `chmod`, `truncate`, `utimens`, `access`, and `statfs`.

## Usage

```bash
export UECI_EPIC_GITHUB_TOKEN='...'
mkdir -p /tmp/ueci-engine

dotnet run --project src/Ueci.Cli -- \
  mount /tmp/ueci-engine \
  --metadata-dir .ueci/vfs-source \
  --state-dir .ueci/vfs-state \
  --ref release \
  --no-pack-cache
```

In another terminal:

```bash
ls /tmp/ueci-engine/Engine/Source/Runtime/Core/Public
head /tmp/ueci-engine/Engine/Source/Runtime/Core/Public/CoreMinimal.h
mkdir -p /tmp/ueci-engine/Engine/Saved/UECI
printf 'hello\n' >/tmp/ueci-engine/Engine/Saved/UECI/cow-test.txt
```

The first content read may block while its backing blob enters CAS. Repeating the same read should be a warm CAS hit with no network traffic.

## Security / deadlock boundary

The metadata repository, CAS, state, upper layer and Unix socket all live **outside** the mounted namespace. The FUSE daemon must never fetch/cache through its own mount, otherwise a request could recursively depend on itself and deadlock.

The GitHub token remains process-only and is not stored in the virtual tree, Git remote URL, config files or logs.

## Next integration step

`ueci mount` is deliberately shipped before switching `build-plugin` to mounted mode. The existing v0.4 materialized build path remains the correctness fallback. Once the standalone mount smoke is validated on a real Linux host, the plugin builder can use the mounted root directly and remove the UBT diagnostic/sparse-checkout retry loop for Linux mounted builds.
