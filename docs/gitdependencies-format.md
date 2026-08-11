# GitDependencies pack format notes

UECI v0.2 implements the pack behavior validated against an Epic Unreal Engine dependency manifest and a real CDN payload. The implementation does not assume blobs are contiguous; bytes between selected blob ranges are streamed past until the next absolute offset.

## Manifest graph

`Commit.gitdeps.xml` maps a materialized path to a content blob and then to a compressed pack:

```text
File.Name
   │
   └── File.Hash = Blob.Hash
                      │
                      ├── Size
                      ├── PackHash
                      └── PackOffset
                              │
                              ▼
                            Pack
                              ├── Size
                              ├── CompressedSize
                              └── RemotePath
```

The pack URL is:

```text
<BaseUrl>/<RemotePath>/<PackHash>
```

## Pack bytes supported by v0.2

The CDN payload is gzip-compressed. After gzip decompression, UECI expects the eight-byte ASCII header:

```text
UEPACK00
```

`Blob.PackOffset` is interpreted as an absolute byte offset in that **decompressed** pack stream, including the eight-byte header. A selected blob is reconstructed by reading exactly `Blob.Size` bytes at that offset and verifying that the SHA-1 digest equals `Blob.Hash`.

```text
HTTP GET pack
      │
      ▼
    gzip
      │
      ▼
+------------------------+
| UEPACK00               | bytes 0..7
+------------------------+
| blob A                 | PackOffset A
+------------------------+
| blob B                 | PackOffset B
+------------------------+
| ...                    |
+------------------------+
```

UECI fails closed on an unknown pack magic. This is intentional: support for another Epic pack version should be added explicitly rather than silently applying the wrong offset semantics.

## Streaming extraction

Gzip is not seekable in the way UECI needs. The materializer therefore groups requested blobs by `PackHash`, sorts each group by `PackOffset`, and walks a decompressed pack once from front to back. It does not decompress the same pack once per blob.

Every extracted blob is written to a temporary file, SHA-1 verified, and atomically promoted into the blob cache only after verification succeeds.
