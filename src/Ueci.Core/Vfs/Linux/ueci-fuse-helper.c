#define FUSE_USE_VERSION 31
#define _GNU_SOURCE
#include <fuse.h>
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

static const char *g_socket_path = NULL;

static char *hex_encode(const char *input)
{
    static const char digits[] = "0123456789abcdef";
    size_t len = strlen(input);
    char *out = malloc(len * 2 + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < len; ++i) {
        unsigned char c = (unsigned char)input[i];
        out[i * 2] = digits[c >> 4];
        out[i * 2 + 1] = digits[c & 15];
    }
    out[len * 2] = '\0';
    return out;
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static char *hex_decode(const char *input)
{
    size_t len = strlen(input);
    if ((len & 1) != 0) return NULL;
    char *out = malloc(len / 2 + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < len; i += 2) {
        int hi = hex_value(input[i]);
        int lo = hex_value(input[i + 1]);
        if (hi < 0 || lo < 0) { free(out); return NULL; }
        out[i / 2] = (char)((hi << 4) | lo);
    }
    out[len / 2] = '\0';
    return out;
}

struct server_connection {
    int fd;
    FILE *reader;
    FILE *writer;
};

static __thread struct server_connection g_server = { .fd = -1, .reader = NULL, .writer = NULL };

static void disconnect_server(void)
{
    if (g_server.reader) {
        fclose(g_server.reader);
        g_server.reader = NULL;
    }
    if (g_server.writer) {
        fclose(g_server.writer);
        g_server.writer = NULL;
    }
    g_server.fd = -1;
}

static struct server_connection *connect_server(void)
{
    if (g_server.reader && g_server.writer) return &g_server;

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return NULL;
#ifdef SOCK_CLOEXEC
    (void)fcntl(fd, F_SETFD, FD_CLOEXEC);
#endif
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    if (strlen(g_socket_path) >= sizeof(addr.sun_path)) { close(fd); errno = ENAMETOOLONG; return NULL; }
    strcpy(addr.sun_path, g_socket_path);
    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) != 0) { close(fd); return NULL; }

    int writer_fd = dup(fd);
    if (writer_fd < 0) { close(fd); return NULL; }
    FILE *reader = fdopen(fd, "r");
    if (!reader) { close(fd); close(writer_fd); return NULL; }
    FILE *writer = fdopen(writer_fd, "w");
    if (!writer) { fclose(reader); close(writer_fd); return NULL; }

    // Separate stdio streams avoid undefined read/write direction changes on an update stream.
    // Each FUSE worker thread owns one connection, so requests remain ordered without a mutex.
    setvbuf(reader, NULL, _IOFBF, 16 * 1024);
    setvbuf(writer, NULL, _IOFBF, 16 * 1024);
    g_server.fd = fd;
    g_server.reader = reader;
    g_server.writer = writer;
    return &g_server;
}

static void trim_newline(char *line)
{
    size_t len = strlen(line);
    while (len && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = '\0';
}

static int parse_error(const char *line)
{
    if (strncmp(line, "ERR\t", 4) != 0) return -EIO;
    char *end = NULL;
    long value = strtol(line + 4, &end, 10);
    if (value <= 0 || value > INT_MAX) return -EIO;
    return -(int)value;
}

static int request_single_once(const char *request, char **response)
{
    struct server_connection *server = connect_server();
    if (!server) return -errno;
    if (fprintf(server->writer, "%s\n", request) < 0 || fflush(server->writer) != 0) return -EPIPE;
    char *line = NULL;
    size_t cap = 0;
    ssize_t got = getline(&line, &cap, server->reader);
    if (got < 0) { free(line); return -EPIPE; }
    trim_newline(line);
    if (strncmp(line, "ERR\t", 4) == 0) { int result = parse_error(line); free(line); return result; }
    *response = line;
    return 0;
}

static int request_single(const char *request, char **response)
{
    int result = request_single_once(request, response);
    if (result != -EPIPE && result != -ECONNRESET) return result;
    // A daemon restart or closed worker connection should not permanently poison this thread.
    disconnect_server();
    return request_single_once(request, response);
}

static int resolve_path(const char *path, int writable, int create, char **physical)
{
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 32;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "RESOLVE\t%c\t%d\t%s", writable ? 'W' : 'R', create ? 1 : 0, hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result != 0) return result;
    if (strncmp(response, "OK\t", 3) != 0) { free(response); return -EIO; }
    char *decoded = hex_decode(response + 3);
    free(response);
    if (!decoded) return -EIO;
    *physical = decoded;
    return 0;
}

static void *ueci_init(struct fuse_conn_info *conn, struct fuse_config *cfg)
{
    (void)conn;
    // The immutable lower Engine only changes by switching to a different mount/commit, while
    // writable changes go through this FUSE instance. Let the kernel retain metadata/data instead
    // of asking the userspace bridge the same questions on every UBT rules scan.
    cfg->kernel_cache = 1;
    cfg->auto_cache = 0;
    cfg->attr_timeout = 60.0;
    cfg->entry_timeout = 60.0;
    cfg->negative_timeout = 1.0;
    cfg->use_ino = 0;
    return NULL;
}

static int ueci_getattr_stat(const char *path, struct stat *st, struct fuse_file_info *fi)
{
    if (fi && fi->fh) {
        if (fstat((int)fi->fh, st) == 0) return 0;
    }
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 8;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "STAT\t%s", hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result != 0) return result;

    char kind = 0;
    long long size = 0;
    unsigned int mode = 0;
    if (sscanf(response, "OK\t%c\t%lld\t%u", &kind, &size, &mode) != 3) { free(response); return -EIO; }
    free(response);
    memset(st, 0, sizeof(*st));
    st->st_uid = getuid();
    st->st_gid = getgid();
    st->st_mode = mode & 07777;
    if (kind == 'D') { st->st_mode |= S_IFDIR; st->st_nlink = 2; }
    else if (kind == 'L') { st->st_mode |= S_IFLNK; st->st_nlink = 1; }
    else { st->st_mode |= S_IFREG; st->st_nlink = 1; }
    st->st_size = (off_t)size;
    st->st_blksize = 4096;
    st->st_blocks = (size + 511) / 512;
    return 0;
}

#ifdef __APPLE__
static void copy_darwin_attributes(struct fuse_darwin_attr *attr, const struct stat *st)
{
    attr->mode = st->st_mode;
    attr->nlink = st->st_nlink;
    attr->uid = st->st_uid;
    attr->gid = st->st_gid;
    attr->size = st->st_size;
    attr->blksize = st->st_blksize;
    attr->blocks = st->st_blocks;
}

static int ueci_getattr(const char *path, struct fuse_darwin_attr *attr, struct fuse_file_info *fi)
{
    struct stat st;
    int result = ueci_getattr_stat(path, &st, fi);
    if (result == 0) copy_darwin_attributes(attr, &st);
    return result;
}
#else
#define ueci_getattr ueci_getattr_stat
#endif

static int ueci_opendir(const char *path, struct fuse_file_info *fi)
{
    (void)path;
    // All namespace mutations are performed through this mount, so the kernel can safely reuse
    // directory entries between repeated UBT scans and update its own dcache on create/unlink.
    fi->cache_readdir = 1;
    fi->keep_cache = 1;
    return 0;
}

#ifdef __APPLE__
static int ueci_readdir(const char *path, void *buf, fuse_darwin_fill_dir_t filler, off_t offset,
                        struct fuse_file_info *fi, enum fuse_readdir_flags flags)
#else
static int ueci_readdir(const char *path, void *buf, fuse_fill_dir_t filler, off_t offset,
                        struct fuse_file_info *fi, enum fuse_readdir_flags flags)
#endif
{
    (void)offset; (void)fi;
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 8;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "LIST\t%s", hex);
    free(hex);

    struct server_connection *server = connect_server();
    if (!server) { free(request); return -errno; }
    if (fprintf(server->writer, "%s\n", request) < 0 || fflush(server->writer) != 0) {
        int e = errno ? errno : EPIPE;
        free(request);
        disconnect_server();
        return -e;
    }
    free(request);

    char *line = NULL;
    size_t cap = 0;
    if (getline(&line, &cap, server->reader) < 0) {
        free(line);
        disconnect_server();
        return -EPIPE;
    }
    trim_newline(line);
    if (strncmp(line, "ERR\t", 4) == 0) { int result = parse_error(line); free(line); return result; }
    if (strcmp(line, "OK") != 0) { free(line); return -EIO; }

    filler(buf, ".", NULL, 0, FUSE_FILL_DIR_DEFAULTS);
    filler(buf, "..", NULL, 0, FUSE_FILL_DIR_DEFAULTS);
    while (getline(&line, &cap, server->reader) >= 0) {
        trim_newline(line);
        if (strcmp(line, "END") == 0) break;
        if (strncmp(line, "E\t", 2) != 0) { free(line); disconnect_server(); return -EIO; }
        char *save = NULL;
        char *tag = strtok_r(line, "\t", &save);
        char *kind = strtok_r(NULL, "\t", &save);
        char *size_text = strtok_r(NULL, "\t", &save);
        char *mode_text = strtok_r(NULL, "\t", &save);
        char *name_hex = strtok_r(NULL, "\t", &save);
        (void)tag;
        if (!kind || !size_text || !mode_text || !name_hex) { free(line); return -EIO; }
        char *name = hex_decode(name_hex);
        if (!name) { free(line); return -EIO; }

        char *size_end = NULL, *mode_end = NULL;
        long long child_size = strtoll(size_text, &size_end, 10);
        unsigned long child_mode = strtoul(mode_text, &mode_end, 10);
        if (!size_end || *size_end != '\0' || !mode_end || *mode_end != '\0' || child_size < -1) {
            free(name); free(line); return -EIO;
        }

        struct stat child;
        memset(&child, 0, sizeof(child));
        child.st_uid = getuid();
        child.st_gid = getgid();
        child.st_mode = (mode_t)(child_mode & 07777);
        child.st_nlink = kind[0] == 'D' ? 2 : 1;
        if (kind[0] == 'D') child.st_mode |= S_IFDIR;
        else if (kind[0] == 'L') child.st_mode |= S_IFLNK;
        else child.st_mode |= S_IFREG;
        child.st_size = child_size >= 0 ? (off_t)child_size : 0;
        child.st_blksize = 4096;
        child.st_blocks = child_size >= 0 ? (child_size + 511) / 512 : 0;

        // Providing complete attributes allows READDIRPLUS to prefill the kernel inode cache and
        // avoids the getattr storm Unreal otherwise generates immediately after each directory scan.
        // A non-GitHub lower may still have an unknown (-1) size; don't poison the inode cache with
        // a fake zero in that fallback case, so a later getattr can hydrate the exact size.
        enum fuse_fill_dir_flags fill_flags = child_size >= 0 ? FUSE_FILL_DIR_PLUS : FUSE_FILL_DIR_DEFAULTS;
#ifdef __APPLE__
        struct fuse_darwin_attr attr;
        memset(&attr, 0, sizeof(attr));
        copy_darwin_attributes(&attr, &child);
        int full = filler(buf, name, &attr, 0, fill_flags);
#else
        int full = filler(buf, name, &child, 0, fill_flags);
#endif
        free(name);
        if (full) break;
    }
    free(line);
    (void)flags;
    return 0;
}

static int ueci_readlink(const char *path, char *buf, size_t size)
{
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 12;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "READLINK\t%s", hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result != 0) return result;
    if (strncmp(response, "OK\t", 3) != 0) { free(response); return -EIO; }
    char *target = hex_decode(response + 3);
    free(response);
    if (!target) return -EIO;
    if (size > 0) {
        strncpy(buf, target, size - 1);
        buf[size - 1] = '\0';
    }
    free(target);
    return 0;
}

static int open_backing(const char *path, struct fuse_file_info *fi, mode_t create_mode, int force_create)
{
    int writable = (fi->flags & O_ACCMODE) != O_RDONLY;
    char *physical = NULL;
    int result = resolve_path(path, writable || force_create, force_create, &physical);
    if (result != 0) return result;
    int flags = fi->flags | O_CLOEXEC;
    int fd = force_create ? open(physical, flags | O_CREAT, create_mode) : open(physical, flags);
    int saved = errno;
    free(physical);
    if (fd < 0) return -saved;
    fi->fh = (uint64_t)fd;
    return 0;
}

static int ueci_open(const char *path, struct fuse_file_info *fi) { return open_backing(path, fi, 0666, 0); }
static int ueci_create(const char *path, mode_t mode, struct fuse_file_info *fi) { return open_backing(path, fi, mode, 1); }

static int ueci_read(const char *path, char *buf, size_t size, off_t offset, struct fuse_file_info *fi)
{
    (void)path;
    ssize_t count = pread((int)fi->fh, buf, size, offset);
    return count < 0 ? -errno : (int)count;
}

static int ueci_write(const char *path, const char *buf, size_t size, off_t offset, struct fuse_file_info *fi)
{
    (void)path;
    ssize_t count = pwrite((int)fi->fh, buf, size, offset);
    return count < 0 ? -errno : (int)count;
}

static int ueci_flush(const char *path, struct fuse_file_info *fi)
{
    (void)path;
    int dupfd = dup((int)fi->fh);
    if (dupfd < 0) return -errno;
    return close(dupfd) == 0 ? 0 : -errno;
}

static int ueci_fsync(const char *path, int datasync, struct fuse_file_info *fi)
{
    (void)path;
    int result = datasync ? fdatasync((int)fi->fh) : fsync((int)fi->fh);
    return result == 0 ? 0 : -errno;
}

static int ueci_release(const char *path, struct fuse_file_info *fi)
{
    (void)path;
    int fd = (int)fi->fh;
    fi->fh = 0;
    return close(fd) == 0 ? 0 : -errno;
}

static int simple_path_request(const char *verb, const char *path)
{
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(verb) + strlen(hex) + 2;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "%s\t%s", verb, hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result == 0 && strcmp(response, "OK") != 0) result = -EIO;
    free(response);
    return result;
}

static int ueci_mkdir(const char *path, mode_t mode)
{
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 32;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "MKDIR\t%u\t%s", (unsigned int)mode, hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result == 0 && strcmp(response, "OK") != 0) result = -EIO;
    free(response);
    return result;
}

static int ueci_unlink(const char *path) { return simple_path_request("UNLINK", path); }
static int ueci_rmdir(const char *path) { return simple_path_request("RMDIR", path); }

static int ueci_rename(const char *from, const char *to, unsigned int flags)
{
    if (flags != 0) return -EINVAL;
    char *from_hex = hex_encode(from), *to_hex = hex_encode(to);
    if (!from_hex || !to_hex) { free(from_hex); free(to_hex); return -ENOMEM; }
    size_t needed = strlen(from_hex) + strlen(to_hex) + 12;
    char *request = malloc(needed);
    if (!request) { free(from_hex); free(to_hex); return -ENOMEM; }
    snprintf(request, needed, "RENAME\t%s\t%s", from_hex, to_hex);
    free(from_hex); free(to_hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result == 0 && strcmp(response, "OK") != 0) result = -EIO;
    free(response);
    return result;
}

static int ueci_symlink(const char *target, const char *linkpath)
{
    char *target_hex = hex_encode(target), *path_hex = hex_encode(linkpath);
    if (!target_hex || !path_hex) { free(target_hex); free(path_hex); return -ENOMEM; }
    size_t needed = strlen(target_hex) + strlen(path_hex) + 12;
    char *request = malloc(needed);
    if (!request) { free(target_hex); free(path_hex); return -ENOMEM; }
    snprintf(request, needed, "SYMLINK\t%s\t%s", target_hex, path_hex);
    free(target_hex); free(path_hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result == 0 && strcmp(response, "OK") != 0) result = -EIO;
    free(response);
    return result;
}

static int ueci_chmod(const char *path, mode_t mode, struct fuse_file_info *fi)
{
    if (fi && fi->fh) return fchmod((int)fi->fh, mode) == 0 ? 0 : -errno;
    char *hex = hex_encode(path);
    if (!hex) return -ENOMEM;
    size_t needed = strlen(hex) + 32;
    char *request = malloc(needed);
    if (!request) { free(hex); return -ENOMEM; }
    snprintf(request, needed, "CHMOD\t%u\t%s", (unsigned int)mode, hex);
    free(hex);
    char *response = NULL;
    int result = request_single(request, &response);
    free(request);
    if (result == 0 && strcmp(response, "OK") != 0) result = -EIO;
    free(response);
    return result;
}

static int ueci_truncate(const char *path, off_t size, struct fuse_file_info *fi)
{
    if (fi && fi->fh) return ftruncate((int)fi->fh, size) == 0 ? 0 : -errno;
    char *physical = NULL;
    int result = resolve_path(path, 1, 0, &physical);
    if (result != 0) return result;
    int fd = open(physical, O_WRONLY | O_CLOEXEC);
    int saved = errno;
    free(physical);
    if (fd < 0) return -saved;
    result = ftruncate(fd, size) == 0 ? 0 : -errno;
    close(fd);
    return result;
}

static int ueci_utimens(const char *path, const struct timespec tv[2], struct fuse_file_info *fi)
{
    if (fi && fi->fh) return futimens((int)fi->fh, tv) == 0 ? 0 : -errno;
    char *physical = NULL;
    int result = resolve_path(path, 1, 0, &physical);
    if (result != 0) return result;
    int rc = utimensat(AT_FDCWD, physical, tv, 0);
    int saved = errno;
    free(physical);
    return rc == 0 ? 0 : -saved;
}


#ifndef __APPLE__
static int ueci_fallocate(const char *path, int mode, off_t offset, off_t length, struct fuse_file_info *fi)
{
    (void)path;
    if (!fi || !fi->fh) return -EBADF;
    if (mode != 0) return -EOPNOTSUPP;
    int result = posix_fallocate((int)fi->fh, offset, length);
    return result == 0 ? 0 : -result;
}

static ssize_t ueci_copy_file_range(const char *path_in, struct fuse_file_info *fi_in, off_t offset_in,
                                    const char *path_out, struct fuse_file_info *fi_out, off_t offset_out,
                                    size_t size, int flags)
{
    (void)path_in; (void)path_out;
    if (!fi_in || !fi_out || !fi_in->fh || !fi_out->fh) return -EBADF;
    ssize_t result = copy_file_range((int)fi_in->fh, &offset_in, (int)fi_out->fh, &offset_out, size, flags);
    return result < 0 ? -errno : result;
}
#endif

static off_t ueci_lseek(const char *path, off_t offset, int whence, struct fuse_file_info *fi)
{
    (void)path;
    if (!fi || !fi->fh) return -EBADF;
    off_t result = lseek((int)fi->fh, offset, whence);
    return result < 0 ? (off_t)-errno : result;
}

static int ueci_access(const char *path, int mask)
{
    (void)mask;
    struct stat st;
    return ueci_getattr_stat(path, &st, NULL);
}

#ifdef __APPLE__
static int ueci_statfs(const char *path, struct statfs *st)
#else
static int ueci_statfs(const char *path, struct statvfs *st)
#endif
{
    (void)path;
    char *response = NULL;
    int result = request_single("STATFS", &response);
    if (result != 0) return result;
    if (strncmp(response, "OK\t", 3) != 0) { free(response); return -EIO; }
    char *physical = hex_decode(response + 3);
    free(response);
    if (!physical) return -EIO;
#ifdef __APPLE__
    int rc = statfs(physical, st);
#else
    int rc = statvfs(physical, st);
#endif
    int saved = errno;
    free(physical);
    return rc == 0 ? 0 : -saved;
}

static const struct fuse_operations ueci_ops = {
    .init = ueci_init,
    .getattr = ueci_getattr,
    .readlink = ueci_readlink,
    .mkdir = ueci_mkdir,
    .unlink = ueci_unlink,
    .rmdir = ueci_rmdir,
    .symlink = ueci_symlink,
    .rename = ueci_rename,
    .chmod = ueci_chmod,
    .truncate = ueci_truncate,
    .open = ueci_open,
    .read = ueci_read,
    .write = ueci_write,
    .statfs = ueci_statfs,
    .flush = ueci_flush,
    .release = ueci_release,
    .fsync = ueci_fsync,
    .opendir = ueci_opendir,
    .readdir = ueci_readdir,
    .access = ueci_access,
    .create = ueci_create,
    .utimens = ueci_utimens,
#ifndef __APPLE__
    .fallocate = ueci_fallocate,
    .copy_file_range = ueci_copy_file_range,
#endif
    .lseek = ueci_lseek,
};

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: %s <ueci-socket> <mountpoint>\n", argv[0]);
        return 2;
    }
    g_socket_path = argv[1];
#ifdef __APPLE__
    char *fuse_argv[] = {
        argv[0],
        "-f",
        "-o",
        "default_permissions,fsname=ueci",
        argv[2],
        NULL,
    };
#else
    char *fuse_argv[] = {
        argv[0],
        "-f",
        "-o",
        "default_permissions,auto_unmount,fsname=ueci",
        argv[2],
        NULL,
    };
#endif
    return fuse_main(5, fuse_argv, &ueci_ops, NULL);
}
