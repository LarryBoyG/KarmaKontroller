using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KarmaWinUSBApp
{
    internal sealed class FileBrowserEndpoint
    {
        public string Host;
        public string Url;
        public string ProbePath;

        public override string ToString()
        {
            return Url;
        }
    }

    internal static class FileBrowserDiscovery
    {
        private const int BrowserPort = 8080;

        public static FileBrowserEndpoint Find(Action<string> progress)
        {
            List<IPAddress> candidates = BuildCandidates();
            if (progress != null)
            {
                progress("Scanning " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " local addresses for port 8080.");
            }

            FileBrowserEndpoint found = null;
            object gate = new object();
            var options = new ParallelOptions { MaxDegreeOfParallelism = 64 };
            Parallel.ForEach(candidates, options, delegate(IPAddress address, ParallelLoopState state)
            {
                if (found != null)
                {
                    state.Stop();
                    return;
                }

                FileBrowserEndpoint endpoint = Probe(address);
                if (endpoint == null)
                {
                    return;
                }

                lock (gate)
                {
                    if (found == null)
                    {
                        found = endpoint;
                        if (progress != null)
                        {
                            progress("Found controller file browser at " + endpoint.Url);
                        }
                        state.Stop();
                    }
                }
            });

            return found;
        }

        private static FileBrowserEndpoint Probe(IPAddress address)
        {
            string host = address.ToString();
            if (!CanConnect(host, BrowserPort, 260))
            {
                return null;
            }

            if (LooksLikeKarmaFileBrowser("http://" + host + ":" + BrowserPort.ToString(CultureInfo.InvariantCulture) + "/data/karma-mapbox-proxy/", 1200))
            {
                return new FileBrowserEndpoint
                {
                    Host = host,
                    Url = "http://" + host + ":" + BrowserPort.ToString(CultureInfo.InvariantCulture) + "/",
                    ProbePath = "/data/karma-mapbox-proxy/"
                };
            }

            if (LooksLikeKarmaFileBrowser("http://" + host + ":" + BrowserPort.ToString(CultureInfo.InvariantCulture) + "/", 1200))
            {
                return new FileBrowserEndpoint
                {
                    Host = host,
                    Url = "http://" + host + ":" + BrowserPort.ToString(CultureInfo.InvariantCulture) + "/",
                    ProbePath = "/"
                };
            }

            return null;
        }

        private static bool CanConnect(string host, int port, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                IAsyncResult result = null;
                try
                {
                    result = client.BeginConnect(host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        return false;
                    }
                    client.EndConnect(result);
                    return client.Connected;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    if (result != null)
                    {
                        result.AsyncWaitHandle.Close();
                    }
                }
            }
        }

        private static bool LooksLikeKarmaFileBrowser(string url, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Proxy = null;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.UserAgent = "KarmaKontroller/2.1";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string html = reader.ReadToEnd();
                    return response.StatusCode == HttpStatusCode.OK
                        && html.IndexOf("<th>Name</th><th>Type</th><th>Size</th><th>Mode</th><th>Modified</th>", StringComparison.OrdinalIgnoreCase) >= 0
                        && html.IndexOf("font:14px sans-serif", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static List<IPAddress> BuildCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<IPAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props;
                try
                {
                    props = adapter.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    AddSubnetCandidates(list, seen, uni.Address, uni.IPv4Mask);
                }
            }
            return list;
        }

        private static void AddSubnetCandidates(List<IPAddress> list, HashSet<string> seen, IPAddress address, IPAddress mask)
        {
            uint ip = ToUInt(address);
            uint maskValue = mask == null ? 0xffffff00U : ToUInt(mask);
            uint network = ip & maskValue;
            uint broadcast = network | ~maskValue;
            ulong count = broadcast > network ? (ulong)(broadcast - network - 1U) : 0UL;

            if (count == 0UL || count > 1024UL)
            {
                network = ip & 0xffffff00U;
                broadcast = network | 0xffU;
            }

            for (uint current = network + 1U; current < broadcast; current++)
            {
                if (current == ip)
                {
                    continue;
                }
                IPAddress candidate = FromUInt(current);
                string text = candidate.ToString();
                if (seen.Add(text))
                {
                    list.Add(candidate);
                }
            }
        }

        private static uint ToUInt(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint value)
        {
            return new IPAddress(new byte[]
            {
                (byte)((value >> 24) & 0xff),
                (byte)((value >> 16) & 0xff),
                (byte)((value >> 8) & 0xff),
                (byte)(value & 0xff)
            });
        }
    }

    internal sealed class WebDavBridge : IDisposable
    {
        private const int MaxBodyBytes = 32 * 1024 * 1024;
        private readonly Uri controllerBaseUri;
        private readonly Action<string> log;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        public WebDavBridge(string controllerBaseUrl, Action<string> log)
        {
            this.controllerBaseUri = new Uri(controllerBaseUrl.TrimEnd('/') + "/");
            this.log = log;
        }

        public int Port { get; private set; }

        public bool IsRunning
        {
            get { return running; }
        }

        public string LocalUrl
        {
            get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/"; }
        }

        public string ExplorerPath
        {
            get { return @"\\127.0.0.1@" + Port.ToString(CultureInfo.InvariantCulture) + @"\DavWWWRoot\"; }
        }

        public void Start()
        {
            if (running)
            {
                return;
            }

            Exception last = null;
            for (int port = 18080; port <= 18099; port++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start(64);
                    Port = port;
                    running = true;
                    acceptThread = new Thread(AcceptLoop);
                    acceptThread.IsBackground = true;
                    acceptThread.Name = "Karma WebDAV bridge";
                    acceptThread.Start();
                    WriteLog("Explorer bridge listening on " + LocalUrl);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    listener = null;
                }
            }

            throw new InvalidOperationException("Could not start the local Explorer bridge on ports 18080-18099.", last);
        }

        public void Dispose()
        {
            running = false;
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                }
            }
            catch
            {
            }
            listener = null;
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch
                {
                    if (running)
                    {
                        Thread.Sleep(120);
                    }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (NetworkStream stream = client.GetStream())
                {
                    SimpleHttpRequest request;
                    try
                    {
                        request = SimpleHttpRequest.Read(stream, MaxBodyBytes);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("Bridge request parse failed: " + ex.Message);
                        return;
                    }

                    if (request == null)
                    {
                        return;
                    }

                    try
                    {
                        Dispatch(stream, request);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("Bridge request failed: " + request.Method + " " + request.RawUrl + " - " + ex.Message);
                        WriteSimpleResponse(stream, 502, "Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
                    }
                }
            }
        }

        private void Dispatch(Stream stream, SimpleHttpRequest request)
        {
            string method = request.Method.ToUpperInvariant();
            string path = NormalizeDavPath(request.RawUrl);
            if (method == "OPTIONS")
            {
                WriteOptions(stream);
            }
            else if (method == "PROPFIND")
            {
                WritePropFind(stream, path, request.GetHeader("Depth"));
            }
            else if (method == "GET" || method == "HEAD")
            {
                ProxyGet(stream, path, method == "HEAD");
            }
            else if (method == "PUT")
            {
                UploadFile(stream, path, request.Body);
            }
            else if (method == "MKCOL")
            {
                MakeCollection(stream, path);
            }
            else if (method == "DELETE")
            {
                DeletePath(stream, path);
            }
            else if (method == "LOCK")
            {
                WriteLock(stream, path);
            }
            else if (method == "UNLOCK")
            {
                WriteStatusOnly(stream, 204, "No Content");
            }
            else
            {
                WriteStatusOnly(stream, 405, "Method Not Allowed");
            }
        }

        private void WriteOptions(Stream stream)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["DAV"] = "1,2";
            headers["Allow"] = "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, LOCK, UNLOCK";
            headers["MS-Author-Via"] = "DAV";
            WriteHeaders(stream, 200, "OK", "text/plain; charset=utf-8", 0, headers);
        }

        private void WritePropFind(Stream stream, string path, string depth)
        {
            ControllerResource resource = FetchResource(path, true);
            if (resource == null)
            {
                WriteStatusOnly(stream, 404, "Not Found");
                return;
            }

            bool includeChildren = resource.IsDirectory && !string.Equals(depth, "0", StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<D:multistatus xmlns:D=\"DAV:\">");
            AppendPropResponse(sb, resource);
            if (includeChildren)
            {
                foreach (ControllerResource child in resource.Children)
                {
                    AppendPropResponse(sb, child);
                }
            }
            sb.Append("</D:multistatus>");

            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["DAV"] = "1,2";
            WriteHeaders(stream, 207, "Multi-Status", "application/xml; charset=utf-8", body.Length, headers);
            stream.Write(body, 0, body.Length);
        }

        private static void AppendPropResponse(StringBuilder sb, ControllerResource resource)
        {
            sb.Append("<D:response><D:href>");
            sb.Append(XmlEscape(EscapeDavHref(resource.Path, resource.IsDirectory)));
            sb.Append("</D:href><D:propstat><D:prop>");
            sb.Append("<D:displayname>");
            sb.Append(XmlEscape(resource.DisplayName));
            sb.Append("</D:displayname><D:resourcetype>");
            if (resource.IsDirectory)
            {
                sb.Append("<D:collection/>");
            }
            sb.Append("</D:resourcetype>");
            if (!resource.IsDirectory)
            {
                sb.Append("<D:getcontentlength>");
                sb.Append(resource.Size.ToString(CultureInfo.InvariantCulture));
                sb.Append("</D:getcontentlength><D:getcontenttype>application/octet-stream</D:getcontenttype>");
            }
            sb.Append("<D:getlastmodified>");
            sb.Append(resource.ModifiedUtc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            sb.Append("</D:getlastmodified>");
            sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }

        private void ProxyGet(Stream stream, string path, bool headOnly)
        {
            Uri uri = BuildControllerUri(path, null);
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = headOnly ? "HEAD" : "GET";
            request.Proxy = null;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "KarmaKontroller-WebDAV/2.1";

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string contentType = string.IsNullOrEmpty(response.ContentType) ? "application/octet-stream" : response.ContentType;
                long length = response.ContentLength;
                if (length < 0)
                {
                    using (var memory = new MemoryStream())
                    using (var body = response.GetResponseStream())
                    {
                        if (body != null && !headOnly)
                        {
                            CopyStream(body, memory);
                        }
                        byte[] bytes = memory.ToArray();
                        WriteHeaders(stream, 200, "OK", contentType, bytes.Length, null);
                        if (!headOnly)
                        {
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    return;
                }

                WriteHeaders(stream, 200, "OK", contentType, length, null);
                if (!headOnly)
                {
                    using (var body = response.GetResponseStream())
                    {
                        if (body != null)
                        {
                            CopyStream(body, stream);
                        }
                    }
                }
            }
        }

        private void UploadFile(Stream stream, string path, byte[] body)
        {
            if (path == "/" || path.EndsWith("/", StringComparison.Ordinal))
            {
                WriteStatusOnly(stream, 409, "Conflict");
                return;
            }

            string parent = ParentPath(path);
            string name = LastSegment(path);
            byte[] payload = BuildMultipartUpload(name, body);
            Uri uri = BuildControllerUri(parent, "action=upload");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "POST";
            request.Proxy = null;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = false;
            request.ContentType = "multipart/form-data; boundary=" + MultipartBoundary;
            request.ContentLength = payload.Length;
            using (Stream outStream = request.GetRequestStream())
            {
                outStream.Write(payload, 0, payload.Length);
            }
            using ((HttpWebResponse)request.GetResponse())
            {
                WriteStatusOnly(stream, 201, "Created");
            }
        }

        private void MakeCollection(Stream stream, string path)
        {
            if (path == "/")
            {
                WriteStatusOnly(stream, 405, "Method Not Allowed");
                return;
            }
            string parent = ParentPath(path);
            string name = LastSegment(path);
            byte[] payload = Encoding.UTF8.GetBytes("name=" + Uri.EscapeDataString(name));
            Uri uri = BuildControllerUri(parent, "action=mkdir");
            PostForm(uri, payload, "application/x-www-form-urlencoded");
            WriteStatusOnly(stream, 201, "Created");
        }

        private void DeletePath(Stream stream, string path)
        {
            if (path == "/")
            {
                WriteStatusOnly(stream, 403, "Forbidden");
                return;
            }
            Uri uri = BuildControllerUri(path, "action=delete");
            PostForm(uri, new byte[0], "application/x-www-form-urlencoded");
            WriteStatusOnly(stream, 204, "No Content");
        }

        private void PostForm(Uri uri, byte[] payload, string contentType)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "POST";
            request.Proxy = null;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = false;
            request.ContentType = contentType;
            request.ContentLength = payload.Length;
            using (Stream outStream = request.GetRequestStream())
            {
                outStream.Write(payload, 0, payload.Length);
            }
            using ((HttpWebResponse)request.GetResponse())
            {
            }
        }

        private void WriteLock(Stream stream, string path)
        {
            string token = "opaquelocktoken:" + Guid.NewGuid().ToString("D");
            string xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock>" +
                "<D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope>" +
                "<D:depth>0</D:depth><D:owner>Karma Kontroller</D:owner><D:timeout>Second-600</D:timeout>" +
                "<D:locktoken><D:href>" + XmlEscape(token) + "</D:href></D:locktoken>" +
                "<D:lockroot><D:href>" + XmlEscape(EscapeDavHref(path, false)) + "</D:href></D:lockroot>" +
                "</D:activelock></D:lockdiscovery></D:prop>";
            byte[] body = Encoding.UTF8.GetBytes(xml);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["Lock-Token"] = "<" + token + ">";
            WriteHeaders(stream, 200, "OK", "application/xml; charset=utf-8", body.Length, headers);
            stream.Write(body, 0, body.Length);
        }

        private ControllerResource FetchResource(string path, bool includeChildren)
        {
            Uri uri = BuildControllerUri(path, null);
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Proxy = null;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "KarmaKontroller-WebDAV/2.1";

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    string contentType = response.ContentType ?? "";
                    if (!IsText(contentType))
                    {
                        var fileResource = new ControllerResource();
                        fileResource.Path = CleanPath(path);
                        fileResource.DisplayName = DisplayNameForPath(fileResource.Path);
                        fileResource.IsDirectory = false;
                        fileResource.Size = response.ContentLength > 0 ? response.ContentLength : 0L;
                        fileResource.ModifiedUtc = DateTime.UtcNow;
                        fileResource.Children = new List<ControllerResource>();
                        return fileResource;
                    }

                    byte[] body;
                    using (var stream = response.GetResponseStream())
                    using (var memory = new MemoryStream())
                    {
                        if (stream != null)
                        {
                            CopyStream(stream, memory);
                        }
                        body = memory.ToArray();
                    }
                    string text = IsText(contentType) ? Encoding.UTF8.GetString(body) : "";
                    bool isDirectory = text.IndexOf("<th>Name</th><th>Type</th><th>Size</th><th>Mode</th><th>Modified</th>", StringComparison.OrdinalIgnoreCase) >= 0;

                    var resource = new ControllerResource();
                    resource.Path = CleanPath(path);
                    resource.DisplayName = DisplayNameForPath(resource.Path);
                    resource.IsDirectory = isDirectory;
                    resource.Size = isDirectory ? 0L : body.Length;
                    resource.ModifiedUtc = DateTime.UtcNow;
                    resource.Children = new List<ControllerResource>();
                    if (isDirectory && includeChildren)
                    {
                        resource.Children.AddRange(ParseDirectory(text));
                    }
                    return resource;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                throw;
            }
        }

        private IEnumerable<ControllerResource> ParseDirectory(string html)
        {
            var list = new List<ControllerResource>();
            var regex = new Regex("<tr><td><a href=\"(?<href>[^\"]*)\">(?<name>.*?)</a>.*?</td><td>(?<type>[^<]*)</td><td>(?<size>[^<]*)</td><td>(?<mode>[^<]*)</td><td>(?<modified>[^<]*)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in regex.Matches(html))
            {
                string href = HtmlDecodeMinimal(match.Groups["href"].Value);
                string type = HtmlDecodeMinimal(match.Groups["type"].Value).Trim();
                string path = CleanPath(Uri.UnescapeDataString(StripQuery(href)));
                if (path == "/" || LastSegment(path) == "..")
                {
                    continue;
                }

                var resource = new ControllerResource();
                resource.Path = path;
                resource.DisplayName = DisplayNameForPath(path);
                resource.IsDirectory = string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase);
                resource.Size = ParseLong(HtmlDecodeMinimal(match.Groups["size"].Value));
                resource.ModifiedUtc = ParseControllerTime(HtmlDecodeMinimal(match.Groups["modified"].Value));
                resource.Children = new List<ControllerResource>();
                list.Add(resource);
            }
            return list;
        }

        private static long ParseLong(string text)
        {
            long value;
            if (long.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0L;
        }

        private static DateTime ParseControllerTime(string text)
        {
            DateTime value;
            if (DateTime.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value))
            {
                return value.ToUniversalTime();
            }
            return DateTime.UtcNow;
        }

        private static bool IsText(string contentType)
        {
            return contentType != null && contentType.IndexOf("text/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Uri BuildControllerUri(string path, string query)
        {
            string url = controllerBaseUri.GetLeftPart(UriPartial.Authority) + EscapePath(CleanPath(path));
            if (!string.IsNullOrEmpty(query))
            {
                url += "?" + query;
            }
            return new Uri(url);
        }

        private static string NormalizeDavPath(string rawUrl)
        {
            string path = StripQuery(rawUrl ?? "/").Replace('\\', '/');
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                path = new Uri(path).AbsolutePath;
            }
            path = Uri.UnescapeDataString(path);
            if (path.StartsWith("/DavWWWRoot", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring("/DavWWWRoot".Length);
                if (path.Length == 0)
                {
                    path = "/";
                }
            }
            return CleanPath(path);
        }

        private static string CleanPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }
            path = path.Replace('\\', '/');
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }
            bool trailingSlash = path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal);
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var clean = new List<string>();
            foreach (string part in parts)
            {
                if (part == ".")
                {
                    continue;
                }
                if (part == "..")
                {
                    if (clean.Count > 0)
                    {
                        clean.RemoveAt(clean.Count - 1);
                    }
                    continue;
                }
                clean.Add(part);
            }
            string result = "/" + string.Join("/", clean.ToArray());
            if (trailingSlash && result != "/")
            {
                result += "/";
            }
            return result;
        }

        private static string StripQuery(string path)
        {
            int q = path.IndexOf('?');
            return q >= 0 ? path.Substring(0, q) : path;
        }

        private static string EscapePath(string path)
        {
            path = CleanPath(path);
            bool trailingSlash = path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal);
            string[] parts = path.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            sb.Append('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('/');
                }
                sb.Append(Uri.EscapeDataString(parts[i]));
            }
            if (trailingSlash && sb[sb.Length - 1] != '/')
            {
                sb.Append('/');
            }
            return sb.ToString();
        }

        private static string EscapeDavHref(string path, bool isDirectory)
        {
            string href = EscapePath(path);
            if (isDirectory && href.Length > 1 && !href.EndsWith("/", StringComparison.Ordinal))
            {
                href += "/";
            }
            return href;
        }

        private static string ParentPath(string path)
        {
            path = CleanPath(path).TrimEnd('/');
            int slash = path.LastIndexOf('/');
            if (slash <= 0)
            {
                return "/";
            }
            return path.Substring(0, slash);
        }

        private static string LastSegment(string path)
        {
            path = CleanPath(path).TrimEnd('/');
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static string DisplayNameForPath(string path)
        {
            string name = LastSegment(path);
            return string.IsNullOrEmpty(name) ? "/" : name;
        }

        private const string MultipartBoundary = "----KarmaKontrollerWebDavBoundary";

        private static byte[] BuildMultipartUpload(string fileName, byte[] fileBytes)
        {
            string header =
                "--" + MultipartBoundary + "\r\n" +
                "Content-Disposition: form-data; name=\"file\"; filename=\"" + fileName.Replace("\"", "_") + "\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n";
            string footer = "\r\n--" + MultipartBoundary + "--\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            byte[] footerBytes = Encoding.UTF8.GetBytes(footer);
            byte[] result = new byte[headerBytes.Length + fileBytes.Length + footerBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
            Buffer.BlockCopy(fileBytes, 0, result, headerBytes.Length, fileBytes.Length);
            Buffer.BlockCopy(footerBytes, 0, result, headerBytes.Length + fileBytes.Length, footerBytes.Length);
            return result;
        }

        private static string HtmlDecodeMinimal(string value)
        {
            return (value ?? "")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        private static string XmlEscape(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        private static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }

        private static void WriteStatusOnly(Stream stream, int code, string reason)
        {
            WriteHeaders(stream, code, reason, "text/plain; charset=utf-8", 0, null);
        }

        private static void WriteSimpleResponse(Stream stream, int code, string reason, string contentType, byte[] body)
        {
            WriteHeaders(stream, code, reason, contentType, body.Length, null);
            stream.Write(body, 0, body.Length);
        }

        private static void WriteHeaders(Stream stream, int code, string reason, string contentType, long contentLength, Dictionary<string, string> extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrEmpty(contentType))
            {
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }
            if (extra != null)
            {
                foreach (KeyValuePair<string, string> pair in extra)
                {
                    sb.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                }
            }
            sb.Append("\r\n");
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
        }

        private void WriteLog(string text)
        {
            if (log != null)
            {
                log(text);
            }
        }

        private sealed class ControllerResource
        {
            public string Path;
            public string DisplayName;
            public bool IsDirectory;
            public long Size;
            public DateTime ModifiedUtc;
            public List<ControllerResource> Children;
        }

        private sealed class SimpleHttpRequest
        {
            public string Method;
            public string RawUrl;
            public Dictionary<string, string> Headers;
            public byte[] Body;

            public string GetHeader(string name)
            {
                string value;
                return Headers.TryGetValue(name, out value) ? value : null;
            }

            public static SimpleHttpRequest Read(Stream stream, int maxBodyBytes)
            {
                byte[] headerBytes = ReadHeaderBytes(stream);
                if (headerBytes == null || headerBytes.Length == 0)
                {
                    return null;
                }

                string headerText = Encoding.ASCII.GetString(headerBytes);
                string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0)
                {
                    throw new InvalidDataException("Missing request line.");
                }

                string[] first = lines[0].Split(' ');
                if (first.Length < 2)
                {
                    throw new InvalidDataException("Invalid request line.");
                }

                var request = new SimpleHttpRequest();
                request.Method = first[0];
                request.RawUrl = first[1];
                request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                }

                int contentLength = 0;
                string contentLengthText = request.GetHeader("Content-Length");
                if (!string.IsNullOrEmpty(contentLengthText))
                {
                    int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
                if (contentLength < 0 || contentLength > maxBodyBytes)
                {
                    throw new InvalidDataException("Request body is too large.");
                }
                request.Body = ReadExactly(stream, contentLength);
                return request;
            }

            private static byte[] ReadHeaderBytes(Stream stream)
            {
                var memory = new MemoryStream();
                int b;
                int state = 0;
                while ((b = stream.ReadByte()) >= 0)
                {
                    memory.WriteByte((byte)b);
                    if (state == 0 && b == '\r') state = 1;
                    else if (state == 1 && b == '\n') state = 2;
                    else if (state == 2 && b == '\r') state = 3;
                    else if (state == 3 && b == '\n') break;
                    else state = 0;

                    if (memory.Length > 65536)
                    {
                        throw new InvalidDataException("Request headers are too large.");
                    }
                }
                byte[] all = memory.ToArray();
                if (all.Length >= 4)
                {
                    Array.Resize(ref all, all.Length - 4);
                }
                return all;
            }

            private static byte[] ReadExactly(Stream stream, int count)
            {
                byte[] bytes = new byte[count];
                int offset = 0;
                while (offset < count)
                {
                    int read = stream.Read(bytes, offset, count - offset);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                }
                return bytes;
            }
        }
    }
}
