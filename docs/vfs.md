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
2. stream `git ls-tree -r -z` for tracked blob names, object IDs and modes **without asking Git for blob sizes**;
3. load `Commit.gitdeps.xml`;
4. merge both namespaces with **GitDependencies taking precedence over Git**, matching a normal Setup overlay;
5. infer directory entries from the indexed paths.

`readdir()` is metadata-only and does not trigger Engine source downloads. Git tree objects do not carry blob sizes, so a targeted POSIX `stat()` on an uncached Git-backed file is treated as the first content demand: UECI hydrates that one blob into CAS and returns its exact size. This avoids both mass `ls-tree --long` hydration and false zero-length EOF behavior. GitDependencies entries always have exact sizes because the manifest contains them.

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

The helper implements the operations needed by normal build tools in the first MVP: `getattr`, `readdir`, `readlink`, `open`, `create`, `read`, `write`, `flush`, `fsync`, `release`, `mkdir`, `unlink`, `rmdir`, `rename`, `symlink`, `chmod`, `truncate`, `utimens`, `access`, `statfs`, `fallocate`, `copy_file_range`, and `lseek`. Local file locking remains handled by the kernel when no custom FUSE lock callback is supplied.

## Usage

```bash
export UECI_EPIC_GITHUB_TOKEN='...'
mkdir -p /tmp/ueci-engine

dotnet run --project src/Ueci.Cli -- \
  mount /tmp/ueci-engine \
  --metadata-dir .ueci/vfs-source \
  --state-dir .ueci/vfs-state \
  --ref release \
  --no-pack-cache \
  --verbose
```

In another terminal:

```bash
ls /tmp/ueci-engine/Engine/Source/Runtime/Core/Public
head /tmp/ueci-engine/Engine/Source/Runtime/Core/Public/CoreMinimal.h
mkdir -p /tmp/ueci-engine/Engine/Saved/UECI
printf 'hello\n' >/tmp/ueci-engine/Engine/Saved/UECI/cow-test.txt
```

The first content read may block while its backing blob enters CAS. Repeating the same read should be a warm CAS hit with no network traffic. Startup/index progress is always printed; `--verbose` additionally prints periodic FUSE request counters while cold CAS fills and mutations remain explicit. The real smoke prints a heartbeat every five seconds while it waits for the mount and defaults to a 10-minute startup timeout (`UECI_VFS_START_TIMEOUT`).

## Security / deadlock boundary

The metadata repository, CAS, state, upper layer and Unix socket all live **outside** the mounted namespace. The FUSE daemon must never fetch/cache through its own mount, otherwise a request could recursively depend on itself and deadlock.

The GitHub token remains process-only and is not stored in the virtual tree, Git remote URL, config files or logs.

## Build integration status

The standalone mount smoke is now validated on a real Linux host, and `build-plugin --backend fuse` consumes the mounted root directly. Linux mounted builds no longer use the UBT diagnostic/sparse-checkout retry loop: the virtual namespace is complete up front and individual Git/GitDependencies contents hydrate on ordinary filesystem demand. The v0.4 materialized build path remains the portable correctness fallback.

## POSIX size semantics

Git tree objects contain path/mode/object-id but not blob length. On the Epic/GitHub backend, UECI enriches that local tree with exact `size` values from GitHub's Git Trees API, which exposes blob metadata without transferring file contents. Recursive responses that report `truncated` are split into smaller child-tree requests; the final SHA→size map is cached by commit. As a result, both `readdir(2)` and `stat(2)` stay metadata-only while still returning a truthful `st_size`; the first `open/read` remains the content-hydration boundary. Non-GitHub repositories keep a targeted hydration fallback when an exact size cannot be obtained.

## Mounted plugin compilation (alpha.6)

`ueci build-plugin --backend fuse` now consumes the mount as an actual Unreal Engine root:

1. prepare Git/GitDependencies metadata and the COW state;
2. start a private FUSE mount;
3. locate Epic's bundled .NET SDK from `Commit.gitdeps.xml`;
4. execute that `dotnet` binary **through FUSE** and compile `UnrealBuildTool.csproj` in the virtual Engine;
5. create the synthetic plugin host outside the mount;
6. install the Linux clang/sysroot toolchain into persistent UECI state and project it into `Engine/Extras/...`;
7. invoke each UBT target once; UBT/clang resolve Engine sources and native libraries through normal filesystem calls;
8. package the plugin and unmount in a `finally`/async-dispose path.

The materialized backend remains the default because hosted CI cannot be assumed to expose `/dev/fuse`. The mounted backend is currently Linux x64 only.

### Mounted hot-path performance

Alpha.6 removes several per-syscall/per-blob costs exposed by real UBT scans:

- each FUSE worker keeps a persistent Unix-domain socket to the C# resolver instead of reconnecting for every request, and multi-entry directory responses are buffered/flushed once;
- directory enumeration returns child attributes so the kernel can prefill inode metadata, and the mount enables bounded attribute/entry/readdir caching for the immutable lower tree;
- lower-only directory listings are precomputed once in `VirtualEngineIndex` rather than rebuilt and resorted for every UBT scan;
- `--vfs-verbose` keeps cold-content/mutation boundaries explicit but aggregates high-volume metadata request counters;
- Git blob reads reuse one long-lived `git cat-file --batch` process instead of spawning Git per CAS miss;

```bash
./scripts/smoke-plugin-vfs.sh
```
