#include <arpa/inet.h>
#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <signal.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define LISTEN_PORT 8080
#define MAX_URL 2048
#define MAX_PATH_LEN 2048
#define IO_BUF 16384
#define MAX_BODY (32 * 1024 * 1024)
#define MAX_EDIT (1024 * 1024)

static int g_log_fd = -1;

static void log_line(const char *fmt, ...) {
    char line[1024];
    char stamp[64];
    time_t now = time(NULL);
    struct tm tmv;
    localtime_r(&now, &tmv);
    strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", &tmv);

    int n = snprintf(line, sizeof(line), "[%s] ", stamp);
    if (n < 0 || n >= (int)sizeof(line)) {
        return;
    }

    va_list ap;
    va_start(ap, fmt);
    int m = vsnprintf(line + n, sizeof(line) - (size_t)n, fmt, ap);
    va_end(ap);
    if (m < 0) {
        return;
    }

    size_t len = strnlen(line, sizeof(line));
    if (len + 1 < sizeof(line)) {
        line[len++] = '\n';
    }
    if (g_log_fd >= 0) {
        write(g_log_fd, line, len);
    }
}

static int write_all(int fd, const void *buf, size_t len) {
    const char *p = (const char *)buf;
    while (len > 0) {
        ssize_t n = write(fd, p, len);
        if (n < 0) {
            if (errno == EINTR) {
                continue;
            }
            return -1;
        }
        if (n == 0) {
            return -1;
        }
        p += n;
        len -= (size_t)n;
    }
    return 0;
}

static const unsigned char *find_bytes(const unsigned char *haystack, size_t haystack_len,
                                       const unsigned char *needle, size_t needle_len) {
    if (needle_len == 0) {
        return haystack;
    }
    if (haystack_len < needle_len) {
        return NULL;
    }
    for (size_t i = 0; i <= haystack_len - needle_len; ++i) {
        if (memcmp(haystack + i, needle, needle_len) == 0) {
            return haystack + i;
        }
    }
    return NULL;
}

static int ascii_lower(int c) {
    if (c >= 'A' && c <= 'Z') {
        return c + ('a' - 'A');
    }
    return c;
}

static bool ascii_starts_with_ci(const char *s, const char *prefix) {
    while (*prefix) {
        if (ascii_lower((unsigned char)*s++) != ascii_lower((unsigned char)*prefix++)) {
            return false;
        }
    }
    return true;
}

static bool is_under_data(const char *path) {
    return strcmp(path, "/data") == 0 || strncmp(path, "/data/", 6) == 0;
}

static bool is_safe_name(const char *name) {
    if (!name || !*name || !strcmp(name, ".") || !strcmp(name, "..")) {
        return false;
    }
    for (const char *p = name; *p; ++p) {
        if (*p == '/' || *p == '\\' || (unsigned char)*p < 32) {
            return false;
        }
    }
    return true;
}

static bool join_child(char *out, size_t out_sz, const char *dir, const char *name) {
    if (!is_safe_name(name)) {
        return false;
    }
    int n;
    if (strcmp(dir, "/") == 0) {
        n = snprintf(out, out_sz, "/%s", name);
    } else {
        n = snprintf(out, out_sz, "%s/%s", dir, name);
    }
    return n > 0 && (size_t)n < out_sz;
}

static int sendf(int fd, const char *fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n < 0) {
        return -1;
    }
    if (n >= (int)sizeof(buf)) {
        n = (int)sizeof(buf) - 1;
    }
    return write_all(fd, buf, (size_t)n);
}

static void html_escape(int fd, const char *s) {
    for (; *s; ++s) {
        switch (*s) {
            case '&': write_all(fd, "&amp;", 5); break;
            case '<': write_all(fd, "&lt;", 4); break;
            case '>': write_all(fd, "&gt;", 4); break;
            case '"': write_all(fd, "&quot;", 6); break;
            default: write_all(fd, s, 1); break;
        }
    }
}

static void url_encode_component(char *out, size_t out_sz, const char *in) {
    static const char hex[] = "0123456789ABCDEF";
    size_t pos = 0;
    for (; *in && pos + 1 < out_sz; ++in) {
        unsigned char c = (unsigned char)*in;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '-' || c == '_' ||
            c == '.' || c == '~') {
            out[pos++] = (char)c;
        } else if (pos + 3 < out_sz) {
            out[pos++] = '%';
            out[pos++] = hex[c >> 4];
            out[pos++] = hex[c & 15];
        } else {
            break;
        }
    }
    out[pos] = '\0';
}

static int hex_value(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + c - 'a';
    if (c >= 'A' && c <= 'F') return 10 + c - 'A';
    return -1;
}

static bool url_has_action(const char *url, const char *action) {
    const char *q = strchr(url, '?');
    if (!q) {
        return false;
    }
    ++q;
    char needle[96];
    snprintf(needle, sizeof(needle), "action=%s", action);
    size_t needle_len = strlen(needle);
    while (*q && *q != '#') {
        const char *next = strchr(q, '&');
        size_t len = next ? (size_t)(next - q) : strlen(q);
        if (len == needle_len && memcmp(q, needle, needle_len) == 0) {
            return true;
        }
        if (!next) {
            break;
        }
        q = next + 1;
    }
    return false;
}

static bool url_has_flag(const char *url, const char *flag) {
    const char *q = strchr(url, '?');
    if (!q) {
        return false;
    }
    ++q;
    size_t flag_len = strlen(flag);
    while (*q && *q != '#') {
        const char *next = strchr(q, '&');
        size_t len = next ? (size_t)(next - q) : strlen(q);
        if (len == flag_len && memcmp(q, flag, flag_len) == 0) {
            return true;
        }
        if (!next) {
            break;
        }
        q = next + 1;
    }
    return false;
}

static bool decode_url_path(const char *url, char *decoded, size_t decoded_sz) {
    size_t pos = 0;
    for (size_t i = 0; url[i] && url[i] != '?' && url[i] != '#'; ++i) {
        char c = url[i];
        if (c == '%') {
            int hi = hex_value(url[i + 1]);
            int lo = hex_value(url[i + 2]);
            if (hi < 0 || lo < 0) {
                return false;
            }
            c = (char)((hi << 4) | lo);
            i += 2;
        }
        if (c == '\0' || pos + 1 >= decoded_sz) {
            return false;
        }
        decoded[pos++] = c;
    }
    decoded[pos] = '\0';
    return true;
}

static bool decode_form_value(const char *in, size_t in_len, char *out, size_t out_sz) {
    size_t pos = 0;
    for (size_t i = 0; i < in_len; ++i) {
        char c = in[i];
        if (c == '+') {
            c = ' ';
        } else if (c == '%') {
            if (i + 2 >= in_len) {
                return false;
            }
            int hi = hex_value(in[i + 1]);
            int lo = hex_value(in[i + 2]);
            if (hi < 0 || lo < 0) {
                return false;
            }
            c = (char)((hi << 4) | lo);
            i += 2;
        }
        if (pos + 1 >= out_sz) {
            return false;
        }
        out[pos++] = c;
    }
    out[pos] = '\0';
    return true;
}

static bool form_field(const char *body, size_t body_len, const char *name,
                       char *out, size_t out_sz) {
    size_t name_len = strlen(name);
    size_t pos = 0;
    while (pos < body_len) {
        size_t end = pos;
        while (end < body_len && body[end] != '&') {
            ++end;
        }
        const char *eq = memchr(body + pos, '=', end - pos);
        if (eq && (size_t)(eq - (body + pos)) == name_len &&
            memcmp(body + pos, name, name_len) == 0) {
            return decode_form_value(eq + 1, end - (size_t)(eq + 1 - body), out, out_sz);
        }
        pos = end + 1;
    }
    return false;
}

static bool normalize_path(const char *decoded, char *out, size_t out_sz) {
    char temp[MAX_PATH_LEN];
    char *parts[256];
    int count = 0;

    if (decoded[0] != '/') {
        return false;
    }
    strncpy(temp, decoded, sizeof(temp) - 1);
    temp[sizeof(temp) - 1] = '\0';

    char *save = NULL;
    for (char *tok = strtok_r(temp, "/", &save); tok; tok = strtok_r(NULL, "/", &save)) {
        if (strcmp(tok, ".") == 0 || tok[0] == '\0') {
            continue;
        }
        if (strcmp(tok, "..") == 0) {
            if (count > 0) {
                --count;
            }
            continue;
        }
        if (count >= (int)(sizeof(parts) / sizeof(parts[0]))) {
            return false;
        }
        parts[count++] = tok;
    }

    size_t pos = 0;
    out[pos++] = '/';
    for (int i = 0; i < count; ++i) {
        size_t len = strlen(parts[i]);
        if (pos + len + 2 >= out_sz) {
            return false;
        }
        memcpy(out + pos, parts[i], len);
        pos += len;
        if (i != count - 1) {
            out[pos++] = '/';
        }
    }
    out[pos] = '\0';
    return true;
}

static void url_for_path(char *out, size_t out_sz, const char *path) {
    if (strcmp(path, "/") == 0) {
        strncpy(out, "/", out_sz);
        out[out_sz - 1] = '\0';
        return;
    }

    size_t pos = 0;
    out[pos++] = '/';
    char temp[MAX_PATH_LEN];
    strncpy(temp, path, sizeof(temp) - 1);
    temp[sizeof(temp) - 1] = '\0';

    char *save = NULL;
    bool first = true;
    for (char *tok = strtok_r(temp, "/", &save); tok; tok = strtok_r(NULL, "/", &save)) {
        char enc[768];
        url_encode_component(enc, sizeof(enc), tok);
        size_t len = strlen(enc);
        if (!first && pos + 1 < out_sz) {
            out[pos++] = '/';
        }
        first = false;
        if (pos + len + 1 >= out_sz) {
            break;
        }
        memcpy(out + pos, enc, len);
        pos += len;
    }
    out[pos] = '\0';
}

static const char *content_type(const char *path) {
    const char *dot = strrchr(path, '.');
    if (!dot) return "application/octet-stream";
    if (!strcmp(dot, ".txt") || !strcmp(dot, ".log") || !strcmp(dot, ".prop") ||
        !strcmp(dot, ".conf") || !strcmp(dot, ".rc") || !strcmp(dot, ".sh") ||
        !strcmp(dot, ".list")) return "text/plain; charset=utf-8";
    if (!strcmp(dot, ".html") || !strcmp(dot, ".htm")) return "text/html; charset=utf-8";
    if (!strcmp(dot, ".xml")) return "application/xml; charset=utf-8";
    if (!strcmp(dot, ".json")) return "application/json; charset=utf-8";
    if (!strcmp(dot, ".png")) return "image/png";
    if (!strcmp(dot, ".jpg") || !strcmp(dot, ".jpeg")) return "image/jpeg";
    if (!strcmp(dot, ".gif")) return "image/gif";
    return "application/octet-stream";
}

static void send_error(int fd, int code, const char *status, const char *detail) {
    sendf(fd,
          "HTTP/1.1 %d %s\r\nConnection: close\r\nContent-Type: text/html; charset=utf-8\r\n\r\n"
          "<!doctype html><title>%d %s</title><h1>%d %s</h1><p>",
          code, status, code, status, code, status);
    html_escape(fd, detail);
    write_all(fd, "</p>\n", 5);
}

static void send_redirect(int fd, const char *location) {
    sendf(fd,
          "HTTP/1.1 303 See Other\r\nConnection: close\r\nLocation: %s\r\n"
          "Content-Type: text/html; charset=utf-8\r\n\r\n"
          "<!doctype html><title>See Other</title><a href=\"%s\">Continue</a>\n",
          location, location);
}

static void parent_url(char *out, size_t out_sz, const char *path) {
    if (strcmp(path, "/") == 0) {
        strncpy(out, "/", out_sz);
        out[out_sz - 1] = '\0';
        return;
    }
    char parent[MAX_PATH_LEN];
    strncpy(parent, path, sizeof(parent) - 1);
    parent[sizeof(parent) - 1] = '\0';
    char *slash = strrchr(parent, '/');
    if (slash == parent) {
        parent[1] = '\0';
    } else if (slash) {
        *slash = '\0';
    }
    url_for_path(out, out_sz, parent);
}

static void send_rw_denied(int fd) {
    send_error(fd, 403, "Forbidden", "Write operations are only enabled under /data.");
}

static bool extract_filename(const char *headers, char *filename, size_t filename_sz) {
    const char *p = strstr(headers, "filename=\"");
    if (!p) {
        return false;
    }
    p += 10;
    const char *end = strchr(p, '"');
    if (!end || end == p) {
        return false;
    }
    size_t len = (size_t)(end - p);
    if (len >= filename_sz) {
        len = filename_sz - 1;
    }
    memcpy(filename, p, len);
    filename[len] = '\0';

    char *slash = strrchr(filename, '/');
    char *backslash = strrchr(filename, '\\');
    char *base = slash;
    if (backslash && (!base || backslash > base)) {
        base = backslash;
    }
    if (base) {
        memmove(filename, base + 1, strlen(base + 1) + 1);
    }
    return is_safe_name(filename);
}

static int write_file_bytes(const char *path, const unsigned char *data, size_t len) {
    int out = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out < 0) {
        return -1;
    }
    int rc = write_all(out, data, len);
    close(out);
    return rc;
}

static bool header_value(const char *headers, const char *name, char *out, size_t out_sz) {
    size_t name_len = strlen(name);
    const char *line = headers;
    while (*line) {
        const char *end = strstr(line, "\r\n");
        if (!end) {
            end = line + strlen(line);
        }
        if ((size_t)(end - line) > name_len && line[name_len] == ':' &&
            ascii_starts_with_ci(line, name)) {
            const char *value = line + name_len + 1;
            while (value < end && (*value == ' ' || *value == '\t')) {
                ++value;
            }
            size_t len = (size_t)(end - value);
            if (len >= out_sz) {
                len = out_sz - 1;
            }
            memcpy(out, value, len);
            out[len] = '\0';
            return true;
        }
        if (*end == '\0') {
            break;
        }
        line = end + 2;
    }
    return false;
}

static bool boundary_from_content_type(const char *content_type, char *boundary, size_t boundary_sz) {
    const char *p = strstr(content_type, "boundary=");
    if (!p) {
        return false;
    }
    p += 9;
    if (*p == '"') {
        ++p;
        const char *end = strchr(p, '"');
        if (!end) {
            return false;
        }
        size_t len = (size_t)(end - p);
        if (len >= boundary_sz) {
            return false;
        }
        memcpy(boundary, p, len);
        boundary[len] = '\0';
        return true;
    }
    size_t len = 0;
    while (p[len] && p[len] != ';' && p[len] != ' ' && p[len] != '\t' && p[len] != '\r' && p[len] != '\n') {
        ++len;
    }
    if (len == 0 || len >= boundary_sz) {
        return false;
    }
    memcpy(boundary, p, len);
    boundary[len] = '\0';
    return true;
}

static void handle_upload(int fd, const char *dir, const char *headers,
                          const unsigned char *body, size_t body_len) {
    if (!is_under_data(dir)) {
        send_rw_denied(fd);
        return;
    }

    struct stat st;
    if (stat(dir, &st) != 0 || !S_ISDIR(st.st_mode)) {
        send_error(fd, 400, "Bad Request", "Upload target is not a directory.");
        return;
    }

    char content_type_value[256];
    char boundary[160];
    if (!header_value(headers, "Content-Type", content_type_value, sizeof(content_type_value)) ||
        !boundary_from_content_type(content_type_value, boundary, sizeof(boundary))) {
        send_error(fd, 400, "Bad Request", "Upload requires multipart/form-data.");
        return;
    }

    char marker[200];
    snprintf(marker, sizeof(marker), "--%s", boundary);
    size_t marker_len = strlen(marker);

    const unsigned char *pos = body;
    const unsigned char *end = body + body_len;
    while (pos < end) {
        const unsigned char *m = find_bytes(pos, (size_t)(end - pos), (const unsigned char *)marker, marker_len);
        if (!m) {
            break;
        }
        m += marker_len;
        if (m + 2 <= end && m[0] == '-' && m[1] == '-') {
            break;
        }
        if (m + 2 > end || m[0] != '\r' || m[1] != '\n') {
            pos = m;
            continue;
        }
        const unsigned char *part_headers_start = m + 2;
        const unsigned char *part_headers_end = find_bytes(part_headers_start, (size_t)(end - part_headers_start),
                                                           (const unsigned char *)"\r\n\r\n", 4);
        if (!part_headers_end) {
            break;
        }

        size_t part_headers_len = (size_t)(part_headers_end - part_headers_start);
        char *part_headers = (char *)malloc(part_headers_len + 1);
        if (!part_headers) {
            send_error(fd, 500, "Internal Server Error", "Out of memory.");
            return;
        }
        memcpy(part_headers, part_headers_start, part_headers_len);
        part_headers[part_headers_len] = '\0';

        const unsigned char *part_data = part_headers_end + 4;
        char next_marker[204];
        snprintf(next_marker, sizeof(next_marker), "\r\n--%s", boundary);
        const unsigned char *next = find_bytes(part_data, (size_t)(end - part_data),
                                               (const unsigned char *)next_marker, strlen(next_marker));
        if (!next) {
            free(part_headers);
            break;
        }

        char filename[256];
        bool have_file = strstr(part_headers, "name=\"file\"") != NULL &&
                         extract_filename(part_headers, filename, sizeof(filename));
        free(part_headers);
        if (have_file) {
            char out_path[MAX_PATH_LEN];
            if (!join_child(out_path, sizeof(out_path), dir, filename)) {
                send_error(fd, 400, "Bad Request", "Unsafe filename.");
                return;
            }
            if (write_file_bytes(out_path, part_data, (size_t)(next - part_data)) != 0) {
                send_error(fd, 500, "Internal Server Error", strerror(errno));
                return;
            }
            log_line("uploaded %s (%lld bytes)", out_path, (long long)(next - part_data));
            char here[MAX_PATH_LEN * 3];
            url_for_path(here, sizeof(here), dir);
            send_redirect(fd, here);
            return;
        }
        pos = next + 2;
    }

    send_error(fd, 400, "Bad Request", "No uploaded file was found.");
}

static void handle_mkdir(int fd, const char *dir, const char *body, size_t body_len) {
    if (!is_under_data(dir)) {
        send_rw_denied(fd);
        return;
    }
    char name[256];
    char child[MAX_PATH_LEN];
    if (!form_field(body, body_len, "name", name, sizeof(name)) ||
        !join_child(child, sizeof(child), dir, name)) {
        send_error(fd, 400, "Bad Request", "Invalid directory name.");
        return;
    }
    if (mkdir(child, 0755) != 0) {
        send_error(fd, 500, "Internal Server Error", strerror(errno));
        return;
    }
    log_line("created directory %s", child);
    char here[MAX_PATH_LEN * 3];
    url_for_path(here, sizeof(here), dir);
    send_redirect(fd, here);
}

static void handle_create_file(int fd, const char *dir, const char *body, size_t body_len) {
    if (!is_under_data(dir)) {
        send_rw_denied(fd);
        return;
    }
    char name[256];
    char content[MAX_BODY > 65536 ? 65536 : MAX_BODY];
    char child[MAX_PATH_LEN];
    if (!form_field(body, body_len, "name", name, sizeof(name)) ||
        !join_child(child, sizeof(child), dir, name)) {
        send_error(fd, 400, "Bad Request", "Invalid filename.");
        return;
    }
    if (!form_field(body, body_len, "content", content, sizeof(content))) {
        content[0] = '\0';
    }
    if (write_file_bytes(child, (const unsigned char *)content, strlen(content)) != 0) {
        send_error(fd, 500, "Internal Server Error", strerror(errno));
        return;
    }
    log_line("created file %s", child);
    char here[MAX_PATH_LEN * 3];
    url_for_path(here, sizeof(here), dir);
    send_redirect(fd, here);
}

static void handle_save_file(int fd, const char *path, const char *body, size_t body_len) {
    if (!is_under_data(path)) {
        send_rw_denied(fd);
        return;
    }
    char *content = (char *)malloc(body_len + 1);
    if (!content) {
        send_error(fd, 500, "Internal Server Error", "Out of memory.");
        return;
    }
    bool ok = form_field(body, body_len, "content", content, body_len + 1);
    if (!ok) {
        free(content);
        send_error(fd, 400, "Bad Request", "Missing content field.");
        return;
    }
    if (write_file_bytes(path, (const unsigned char *)content, strlen(content)) != 0) {
        free(content);
        send_error(fd, 500, "Internal Server Error", strerror(errno));
        return;
    }
    free(content);
    log_line("saved file %s", path);
    char here[MAX_PATH_LEN * 3];
    url_for_path(here, sizeof(here), path);
    send_redirect(fd, here);
}

static void handle_delete(int fd, const char *path) {
    if (!is_under_data(path) || strcmp(path, "/data") == 0) {
        send_rw_denied(fd);
        return;
    }
    struct stat st;
    if (lstat(path, &st) != 0) {
        send_error(fd, 404, "Not Found", strerror(errno));
        return;
    }
    int rc = S_ISDIR(st.st_mode) ? rmdir(path) : unlink(path);
    if (rc != 0) {
        send_error(fd, 500, "Internal Server Error", strerror(errno));
        return;
    }
    log_line("deleted %s", path);
    char parent[MAX_PATH_LEN * 3];
    parent_url(parent, sizeof(parent), path);
    send_redirect(fd, parent);
}

static bool read_http_request(int fd, char **req_out, char **headers_out,
                              unsigned char **body_out, size_t *body_len_out) {
    size_t cap = 8192;
    size_t len = 0;
    char *buf = (char *)malloc(cap + 1);
    if (!buf) {
        send_error(fd, 500, "Internal Server Error", "Out of memory.");
        return false;
    }

    const unsigned char *header_end = NULL;
    while (!header_end) {
        if (len == cap) {
            if (cap >= 65536) {
                free(buf);
                send_error(fd, 431, "Request Header Fields Too Large", "Request headers are too large.");
                return false;
            }
            cap *= 2;
            char *next = (char *)realloc(buf, cap + 1);
            if (!next) {
                free(buf);
                send_error(fd, 500, "Internal Server Error", "Out of memory.");
                return false;
            }
            buf = next;
        }
        ssize_t n = recv(fd, buf + len, cap - len, 0);
        if (n <= 0) {
            free(buf);
            return false;
        }
        len += (size_t)n;
        header_end = find_bytes((const unsigned char *)buf, len, (const unsigned char *)"\r\n\r\n", 4);
    }

    size_t header_len = (size_t)(header_end - (const unsigned char *)buf) + 4;
    char *headers = (char *)malloc(header_len + 1);
    if (!headers) {
        free(buf);
        send_error(fd, 500, "Internal Server Error", "Out of memory.");
        return false;
    }
    memcpy(headers, buf, header_len);
    headers[header_len] = '\0';

    char cl_value[64];
    size_t content_length = 0;
    if (header_value(headers, "Content-Length", cl_value, sizeof(cl_value))) {
        content_length = (size_t)strtoul(cl_value, NULL, 10);
        if (content_length > MAX_BODY) {
            free(headers);
            free(buf);
            send_error(fd, 413, "Payload Too Large", "Request body is limited to 32 MiB.");
            return false;
        }
    }

    size_t total = header_len + content_length;
    if (total > cap) {
        char *next = (char *)realloc(buf, total + 1);
        if (!next) {
            free(headers);
            free(buf);
            send_error(fd, 500, "Internal Server Error", "Out of memory.");
            return false;
        }
        buf = next;
        cap = total;
    }
    while (len < total) {
        ssize_t n = recv(fd, buf + len, total - len, 0);
        if (n <= 0) {
            free(headers);
            free(buf);
            return false;
        }
        len += (size_t)n;
    }
    buf[total] = '\0';

    *req_out = buf;
    *headers_out = headers;
    *body_out = (unsigned char *)buf + header_len;
    *body_len_out = content_length;
    return true;
}

static void print_parent_link(int fd, const char *path) {
    if (strcmp(path, "/") == 0) {
        return;
    }
    char parent[MAX_PATH_LEN];
    strncpy(parent, path, sizeof(parent) - 1);
    parent[sizeof(parent) - 1] = '\0';
    char *slash = strrchr(parent, '/');
    if (slash == parent) {
        parent[1] = '\0';
    } else if (slash) {
        *slash = '\0';
    }
    char href[MAX_PATH_LEN * 3];
    url_for_path(href, sizeof(href), parent);
    sendf(fd, "<tr><td><a href=\"%s\">..</a></td><td>dir</td><td></td><td></td><td></td></tr>\n", href);
}

static void serve_directory(int fd, const char *path, bool head_only) {
    DIR *dir = opendir(path);
    if (!dir) {
        send_error(fd, 403, "Forbidden", strerror(errno));
        return;
    }
    bool writable = is_under_data(path);

    sendf(fd,
          "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: text/html; charset=utf-8\r\n\r\n");
    if (head_only) {
        closedir(dir);
        return;
    }

    sendf(fd,
          "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
          "<title>");
    html_escape(fd, path);
    sendf(fd,
          "</title><style>body{font:14px sans-serif;margin:24px;background:#f7f7f4;color:#181818}"
          "a{color:#064f8f;text-decoration:none}a:hover{text-decoration:underline}"
          "table{border-collapse:collapse;width:100%%;background:white}th,td{border-bottom:1px solid #ddd;padding:7px 9px;text-align:left}"
          "th{background:#eceae4}.path{font-size:20px;margin-bottom:16px}.muted{color:#666}"
          ".tools{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 16px}.tools form{background:white;border:1px solid #ddd;padding:8px}"
          "input,textarea,button{font:inherit}button{cursor:pointer}</style></head><body><div class=\"path\">");
    html_escape(fd, path);
    sendf(fd, "</div>");

    if (writable) {
        char here[MAX_PATH_LEN * 3];
        url_for_path(here, sizeof(here), path);
        sendf(fd,
              "<div class=\"tools\">"
              "<form method=\"post\" enctype=\"multipart/form-data\" action=\"%s?action=upload\">"
              "<input type=\"file\" name=\"file\"><button>Upload</button></form>"
              "<form method=\"post\" action=\"%s?action=mkdir\">"
              "<input name=\"name\" placeholder=\"Folder name\"><button>New folder</button></form>"
              "<form method=\"post\" action=\"%s?action=create\">"
              "<input name=\"name\" placeholder=\"File name\"><textarea name=\"content\" rows=\"1\" cols=\"28\" placeholder=\"Text\"></textarea><button>New file</button></form>"
              "</div>",
              here, here, here);
    }

    sendf(fd, "<table><thead><tr><th>Name</th><th>Type</th><th>Size</th><th>Mode</th><th>Modified</th>");
    if (writable) {
        sendf(fd, "<th>Actions</th>");
    }
    sendf(fd, "</tr></thead><tbody>\n");

    print_parent_link(fd, path);

    struct dirent *de;
    while ((de = readdir(dir)) != NULL) {
        if (!strcmp(de->d_name, ".") || !strcmp(de->d_name, "..")) {
            continue;
        }

        char child[MAX_PATH_LEN];
        if (strcmp(path, "/") == 0) {
            snprintf(child, sizeof(child), "/%s", de->d_name);
        } else {
            snprintf(child, sizeof(child), "%s/%s", path, de->d_name);
        }

        struct stat st;
        bool have_stat = (lstat(child, &st) == 0);
        bool is_dir = have_stat && S_ISDIR(st.st_mode);
        bool is_link = have_stat && S_ISLNK(st.st_mode);

        char href[MAX_PATH_LEN * 3];
        url_for_path(href, sizeof(href), child);
        if (is_dir && strlen(href) + 2 < sizeof(href)) {
            strcat(href, "/");
        }

        char timestr[64] = "";
        if (have_stat) {
            struct tm tmv;
            localtime_r(&st.st_mtime, &tmv);
            strftime(timestr, sizeof(timestr), "%Y-%m-%d %H:%M", &tmv);
        }

        sendf(fd, "<tr><td><a href=\"%s\">", href);
        html_escape(fd, de->d_name);
        if (is_dir) {
            write_all(fd, "/", 1);
        }
        sendf(fd, "</a>");
        if (is_link) {
            char target[MAX_PATH_LEN];
            ssize_t n = readlink(child, target, sizeof(target) - 1);
            if (n > 0) {
                target[n] = '\0';
                sendf(fd, " <span class=\"muted\">-&gt; ");
                html_escape(fd, target);
                sendf(fd, "</span>");
            }
        }
        sendf(fd, "</td><td>%s</td><td>", is_dir ? "dir" : (is_link ? "link" : "file"));
        if (have_stat && !is_dir) {
            sendf(fd, "%lld", (long long)st.st_size);
        }
        sendf(fd, "</td><td>");
        if (have_stat) {
            sendf(fd, "%o", st.st_mode & 07777);
        }
        sendf(fd, "</td><td>");
        html_escape(fd, timestr);
        sendf(fd, "</td>");
        if (writable) {
            sendf(fd, "<td>");
            if (have_stat && is_under_data(child)) {
                if (!is_dir && S_ISREG(st.st_mode)) {
                    sendf(fd, "<a href=\"%s?edit\">Edit</a> ", href);
                }
                sendf(fd, "<form method=\"post\" action=\"%s?action=delete\" style=\"display:inline\"><button>Delete</button></form>", href);
            }
            sendf(fd, "</td>");
        }
        sendf(fd, "</tr>\n");
    }

    sendf(fd, "</tbody></table></body></html>\n");
    closedir(dir);
}

static void serve_edit_file(int fd, const char *path, const struct stat *st, bool head_only) {
    if (!is_under_data(path)) {
        send_rw_denied(fd);
        return;
    }
    if (st->st_size > MAX_EDIT) {
        send_error(fd, 413, "Payload Too Large", "Browser editing is limited to 1 MiB files.");
        return;
    }
    int in = open(path, O_RDONLY);
    if (in < 0) {
        send_error(fd, 403, "Forbidden", strerror(errno));
        return;
    }
    char *content = (char *)malloc((size_t)st->st_size + 1);
    if (!content) {
        close(in);
        send_error(fd, 500, "Internal Server Error", "Out of memory.");
        return;
    }
    size_t got = 0;
    while (got < (size_t)st->st_size) {
        ssize_t n = read(in, content + got, (size_t)st->st_size - got);
        if (n < 0) {
            if (errno == EINTR) {
                continue;
            }
            free(content);
            close(in);
            send_error(fd, 500, "Internal Server Error", strerror(errno));
            return;
        }
        if (n == 0) {
            break;
        }
        got += (size_t)n;
    }
    close(in);
    content[got] = '\0';

    sendf(fd,
          "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: text/html; charset=utf-8\r\n\r\n");
    if (head_only) {
        free(content);
        return;
    }

    char here[MAX_PATH_LEN * 3];
    char parent[MAX_PATH_LEN * 3];
    url_for_path(here, sizeof(here), path);
    parent_url(parent, sizeof(parent), path);
    sendf(fd,
          "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
          "<title>Edit ");
    html_escape(fd, path);
    sendf(fd,
          "</title><style>body{font:14px sans-serif;margin:24px;background:#f7f7f4;color:#181818}"
          "textarea{box-sizing:border-box;width:100%%;height:70vh;font:13px monospace}button{font:inherit;margin-top:10px}"
          "a{color:#064f8f;text-decoration:none}</style></head><body><p><a href=\"%s\">Back</a></p><h1>",
          parent);
    html_escape(fd, path);
    sendf(fd, "</h1><form method=\"post\" action=\"%s?action=save\"><textarea name=\"content\">", here);
    html_escape(fd, content);
    sendf(fd, "</textarea><br><button>Save</button></form></body></html>\n");
    free(content);
}

static void serve_file(int fd, const char *path, const struct stat *st, bool head_only) {
    int in = open(path, O_RDONLY);
    if (in < 0) {
        send_error(fd, 403, "Forbidden", strerror(errno));
        return;
    }

    sendf(fd,
          "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: %s\r\nContent-Length: %lld\r\n\r\n",
          content_type(path), (long long)st->st_size);
    if (!head_only) {
        char buf[IO_BUF];
        ssize_t n;
        while ((n = read(in, buf, sizeof(buf))) > 0) {
            if (write_all(fd, buf, (size_t)n) < 0) {
                break;
            }
        }
    }
    close(in);
}

static void handle_client(int fd, struct sockaddr_in *peer) {
    char *req = NULL;
    char *headers = NULL;
    unsigned char *body = NULL;
    size_t body_len = 0;
    if (!read_http_request(fd, &req, &headers, &body, &body_len)) {
        return;
    }

    char method[16], url[MAX_URL], version[32];
    if (sscanf(req, "%15s %2047s %31s", method, url, version) != 3) {
        send_error(fd, 400, "Bad Request", "Could not parse request line.");
        goto done;
    }

    bool head_only = !strcmp(method, "HEAD");
    bool post = !strcmp(method, "POST");
    if (strcmp(method, "GET") && !head_only && !post) {
        send_error(fd, 405, "Method Not Allowed", "Only GET, HEAD, and POST are supported.");
        goto done;
    }

    char decoded[MAX_PATH_LEN];
    char path[MAX_PATH_LEN];
    if (!decode_url_path(url, decoded, sizeof(decoded)) ||
        !normalize_path(decoded, path, sizeof(path))) {
        send_error(fd, 400, "Bad Request", "Invalid path.");
        goto done;
    }

    char addr[64];
    inet_ntop(AF_INET, &peer->sin_addr, addr, sizeof(addr));
    log_line("%s %s from %s", method, path, addr);

    if (post) {
        if (url_has_action(url, "upload")) {
            handle_upload(fd, path, headers, body, body_len);
        } else if (url_has_action(url, "mkdir")) {
            handle_mkdir(fd, path, (const char *)body, body_len);
        } else if (url_has_action(url, "create")) {
            handle_create_file(fd, path, (const char *)body, body_len);
        } else if (url_has_action(url, "save")) {
            handle_save_file(fd, path, (const char *)body, body_len);
        } else if (url_has_action(url, "delete")) {
            handle_delete(fd, path);
        } else {
            send_error(fd, 400, "Bad Request", "Unknown write action.");
        }
        goto done;
    }

    struct stat st;
    if (stat(path, &st) != 0) {
        send_error(fd, 404, "Not Found", strerror(errno));
        goto done;
    }
    if (S_ISDIR(st.st_mode)) {
        serve_directory(fd, path, head_only);
    } else if (url_has_flag(url, "edit")) {
        serve_edit_file(fd, path, &st, head_only);
    } else if (S_ISREG(st.st_mode) || S_ISLNK(st.st_mode)) {
        serve_file(fd, path, &st, head_only);
    } else {
        send_error(fd, 403, "Forbidden", "This filesystem object is not a regular file or directory.");
    }

done:
    free(headers);
    free(req);
}

int main(void) {
    signal(SIGPIPE, SIG_IGN);
    signal(SIGCHLD, SIG_IGN);

    g_log_fd = open("/data/karma-file-browser.log", O_WRONLY | O_CREAT | O_APPEND, 0644);
    log_line("starting karma-file-browser on 0.0.0.0:%d", LISTEN_PORT);

    int srv = socket(AF_INET, SOCK_STREAM, 0);
    if (srv < 0) {
        log_line("socket failed: %s", strerror(errno));
        return 1;
    }

    int yes = 1;
    setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(LISTEN_PORT);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);

    if (bind(srv, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        log_line("bind failed: %s", strerror(errno));
        close(srv);
        return 1;
    }
    if (listen(srv, 16) != 0) {
        log_line("listen failed: %s", strerror(errno));
        close(srv);
        return 1;
    }

    for (;;) {
        struct sockaddr_in peer;
        socklen_t peer_len = sizeof(peer);
        int client = accept(srv, (struct sockaddr *)&peer, &peer_len);
        if (client < 0) {
            if (errno == EINTR) {
                continue;
            }
            log_line("accept failed: %s", strerror(errno));
            sleep(1);
            continue;
        }

        pid_t pid = fork();
        if (pid == 0) {
            close(srv);
            handle_client(client, &peer);
            close(client);
            _exit(0);
        }
        close(client);
        if (pid < 0) {
            log_line("fork failed: %s", strerror(errno));
        }
    }
}
