package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"crypto/x509"
	_ "embed"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

//go:embed certs/mapbox-proxy.crt
var serverCertPEM []byte

//go:embed certs/mapbox-proxy.key
var serverKeyPEM []byte

//go:embed certs/upstream-roots.pem
var upstreamRootsPEM []byte

var version = "v8-upstream-dns"
var defaultListenAddr = "127.0.0.1:443"

var defaultAllowedHosts = map[string]bool{
	"api.mapbox.com":       true,
	"api.tiles.mapbox.com": true,
	"a.tiles.mapbox.com":   true,
	"b.tiles.mapbox.com":   true,
	"c.tiles.mapbox.com":   true,
	"d.tiles.mapbox.com":   true,
	"events.mapbox.com":    true,
	"mapbox.com":           true,
	"www.mapbox.com":       true,
}

var fallbackDNSServers = []string{
	"1.1.1.1:53",
	"8.8.8.8:53",
	"9.9.9.9:53",
}

const hostsFile = "/data/karma-mapbox-proxy/hosts.txt"
const dnsFile = "/data/karma-mapbox-proxy/dns.txt"
const upstreamFile = "/data/karma-mapbox-proxy/upstream.txt"
const onlineConfigURLFile = "/data/karma-mapbox-proxy/online-hosts-url.txt"
const onlineConfigCacheFile = "/data/karma-mapbox-proxy/online-hosts.last"
const legacyDNSFile = "/data/karma-mapbox-proxy-dns"

func main() {
	configureLogging()
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)
	if handled, err := runCLI(os.Args[1:]); handled {
		if err != nil {
			fmt.Fprintf(os.Stdout, "KK_RESULT|1|%s\n", resultLineText(err.Error()))
			log.Printf("command failed: %v", err)
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Fprintln(os.Stdout, "KK_RESULT|0|OK")
		return
	}
	if err := runPlatform(); err != nil {
		log.Fatalf("karma-mapbox-proxy stopped: %v", err)
	}
}

func resultLineText(value string) string {
	value = strings.ReplaceAll(value, "\r", " ")
	value = strings.ReplaceAll(value, "\n", " ")
	return strings.ReplaceAll(value, "|", "/")
}

func newProxyServer() (*http.Server, string, error) {
	log.Printf("karma-mapbox-proxy %s starting", version)

	cert, err := tls.X509KeyPair(serverCertPEM, serverKeyPEM)
	if err != nil {
		return nil, "", fmt.Errorf("load local certificate: %w", err)
	}

	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(upstreamRootsPEM) {
		return nil, "", fmt.Errorf("load upstream root certificates")
	}

	startOnlineConfigRefresher(roots)

	proxy := &proxyHandler{client: buildHTTPClient(roots)}
	addr := envOrDefault("KARMA_MAPBOX_PROXY_ADDR", defaultListenAddr)
	return &http.Server{
		Addr:              addr,
		Handler:           proxy,
		ReadHeaderTimeout: 20 * time.Second,
		TLSConfig: &tls.Config{
			Certificates: []tls.Certificate{cert},
			MinVersion:   tls.VersionTLS10,
			CipherSuites: []uint16{
				tls.TLS_RSA_WITH_AES_128_CBC_SHA,
				tls.TLS_RSA_WITH_AES_256_CBC_SHA,
				tls.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
				tls.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
				tls.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
				tls.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
			},
		},
	}, addr, nil
}

type proxyHandler struct {
	client *http.Client
}

func (p *proxyHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	host := cleanHost(r.Host)
	if !currentAllowedHosts()[host] {
		http.Error(w, "host not allowed", http.StatusBadGateway)
		log.Printf("reject host=%q path=%q", r.Host, r.URL.RequestURI())
		return
	}
	log.Printf("request remote=%s host=%s path=%q", r.RemoteAddr, host, r.URL.RequestURI())

	upstreamURL := *r.URL
	upstreamURL.Scheme = "https"
	upstreamURL.Host = host
	if upstreamURL.Path == "" {
		upstreamURL.Path = "/"
	}

	req, err := http.NewRequestWithContext(r.Context(), r.Method, upstreamURL.String(), r.Body)
	if err != nil {
		http.Error(w, "request build failed", http.StatusBadGateway)
		log.Printf("request build failed host=%s path=%q err=%v", host, r.URL.RequestURI(), err)
		return
	}
	copyHeaders(req.Header, r.Header)
	req.Host = host

	resp, err := p.client.Do(req)
	if err != nil {
		http.Error(w, "upstream failed", http.StatusBadGateway)
		log.Printf("upstream failed host=%s path=%q err=%v", host, r.URL.RequestURI(), err)
		return
	}
	defer resp.Body.Close()

	copyHeaders(w.Header(), resp.Header)
	w.WriteHeader(resp.StatusCode)
	if r.Method != http.MethodHead {
		if _, err := io.Copy(w, resp.Body); err != nil {
			log.Printf("response copy failed host=%s path=%q err=%v", host, r.URL.RequestURI(), err)
		}
	}
	if resp.StatusCode >= 400 {
		log.Printf("%s %s %s -> %d", r.Method, host, r.URL.RequestURI(), resp.StatusCode)
	}
}

func buildHTTPClient(roots *x509.CertPool) *http.Client {
	dialer := &net.Dialer{
		Timeout:   30 * time.Second,
		KeepAlive: 30 * time.Second,
	}
	resolver := &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, address string) (net.Conn, error) {
			var lastErr error
			for _, dnsServer := range currentDNSServers() {
				conn, err := dialer.DialContext(ctx, "udp", dnsServer)
				if err == nil {
					return conn, nil
				}
				lastErr = err
			}
			if lastErr == nil {
				lastErr = fmt.Errorf("no DNS servers configured")
			}
			return nil, lastErr
		},
	}

	transport := &http.Transport{
		Proxy: nil,
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			host, port, err := net.SplitHostPort(address)
			if err != nil {
				return nil, err
			}
			if upstream := currentUpstreamAddr(); upstream != "" {
				return dialResolvedIPv4(ctx, dialer, resolver, network, upstream)
			}
			if upstream := currentHostOverride(host, port); upstream != "" {
				return dialResolvedIPv4(ctx, dialer, resolver, network, upstream)
			}
			return dialResolvedIPv4(ctx, dialer, resolver, network, address)
		},
		ForceAttemptHTTP2:     false,
		MaxIdleConns:          16,
		IdleConnTimeout:       60 * time.Second,
		TLSHandshakeTimeout:   30 * time.Second,
		ResponseHeaderTimeout: 60 * time.Second,
		TLSClientConfig: &tls.Config{
			MinVersion:         tls.VersionTLS12,
			RootCAs:            roots,
			InsecureSkipVerify: true,
		},
	}
	return &http.Client{Transport: transport, Timeout: 120 * time.Second}
}

func dialResolvedIPv4(ctx context.Context, dialer *net.Dialer, resolver *net.Resolver, network, address string) (net.Conn, error) {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	host = cleanHost(host)
	if ip := net.ParseIP(host); ip != nil {
		if ip.To4() == nil {
			return nil, fmt.Errorf("no IPv4 address for %s", host)
		}
		return dialer.DialContext(ctx, network, net.JoinHostPort(ip.String(), port))
	}

	ips, err := resolver.LookupIPAddr(ctx, host)
	if err != nil {
		return nil, err
	}
	var lastErr error
	for _, ip := range ips {
		if ip.IP.To4() == nil {
			continue
		}
		conn, err := dialer.DialContext(ctx, network, net.JoinHostPort(ip.IP.String(), port))
		if err == nil {
			return conn, nil
		}
		lastErr = err
	}
	if lastErr == nil {
		lastErr = fmt.Errorf("no IPv4 addresses for %s", host)
	}
	return nil, lastErr
}

func currentHostOverride(host, port string) string {
	host = cleanHost(host)
	overrides, err := readHostOverrides(envOrDefault("KARMA_MAPBOX_HOSTS_FILE", hostsFile))
	if err != nil {
		return ""
	}
	value := overrides[host]
	if value == "" {
		return ""
	}
	if !strings.Contains(value, ":") {
		value = net.JoinHostPort(value, port)
	}
	return value
}

func readHostOverrides(path string) (map[string]string, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	overrides := make(map[string]string)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := scanner.Text()
		if idx := strings.IndexByte(line, '#'); idx >= 0 {
			line = line[:idx]
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		target := cleanHost(fields[0])
		ip := net.ParseIP(target)
		if ip == nil || ip.IsLoopback() || ip.IsUnspecified() {
			continue
		}
		target = ip.String()
		for _, field := range fields[1:] {
			host := cleanHost(field)
			if host == "" || host == "localhost" || net.ParseIP(host) != nil {
				continue
			}
			overrides[host] = target
		}
	}
	if err := scanner.Err(); err != nil {
		return nil, err
	}
	return overrides, nil
}

func startOnlineConfigRefresher(roots *x509.CertPool) {
	if onlineConfigURL() == "" {
		return
	}
	go func() {
		delay := 8 * time.Second
		for {
			time.Sleep(delay)
			if err := refreshOnlineConfig(roots); err != nil {
				log.Printf("online config refresh failed: %v", err)
				delay = 30 * time.Second
				continue
			}
			delay = 15 * time.Minute
		}
	}()
}

func onlineConfigURL() string {
	value := strings.TrimSpace(os.Getenv("KARMA_MAPBOX_ONLINE_CONFIG_URL"))
	if value == "" {
		path := envOrDefault("KARMA_MAPBOX_ONLINE_CONFIG_URL_FILE", onlineConfigURLFile)
		data, err := os.ReadFile(path)
		if err == nil {
			for _, line := range strings.Split(string(data), "\n") {
				if idx := strings.IndexByte(line, '#'); idx >= 0 {
					line = line[:idx]
				}
				value = strings.TrimSpace(line)
				if value != "" {
					break
				}
			}
		}
	}
	switch strings.ToLower(value) {
	case "", "none", "off", "disabled":
		return ""
	}
	return value
}

func refreshOnlineConfig(roots *x509.CertPool) error {
	url := onlineConfigURL()
	if url == "" {
		return nil
	}
	client := buildConfigHTTPClient(roots)
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "KarmaKontroller/"+version)
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("GET %s returned %s", url, resp.Status)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
	if err != nil {
		return err
	}
	if len(body) == 64*1024 {
		return fmt.Errorf("online config is too large")
	}
	if err := writeConfigFile(envOrDefault("KARMA_MAPBOX_ONLINE_CONFIG_CACHE_FILE", onlineConfigCacheFile), body); err != nil {
		log.Printf("online config cache write failed: %v", err)
	}
	if err := applyOnlineConfig(body); err != nil {
		return err
	}
	log.Printf("online config refreshed from %s", url)
	return nil
}

func buildConfigHTTPClient(roots *x509.CertPool) *http.Client {
	dialer := &net.Dialer{Timeout: 30 * time.Second, KeepAlive: 30 * time.Second}
	resolver := &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, address string) (net.Conn, error) {
			var lastErr error
			for _, dnsServer := range currentDNSServers() {
				conn, err := dialer.DialContext(ctx, "udp", dnsServer)
				if err == nil {
					return conn, nil
				}
				lastErr = err
			}
			if lastErr == nil {
				lastErr = fmt.Errorf("no DNS servers configured")
			}
			return nil, lastErr
		},
	}
	return &http.Client{
		Timeout: 45 * time.Second,
		Transport: &http.Transport{
			Proxy: nil,
			DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
				return dialResolvedIPv4(ctx, dialer, resolver, network, address)
			},
			ForceAttemptHTTP2:     false,
			TLSHandshakeTimeout:   30 * time.Second,
			ResponseHeaderTimeout: 30 * time.Second,
			TLSClientConfig: &tls.Config{
				MinVersion:         tls.VersionTLS12,
				RootCAs:            roots,
				InsecureSkipVerify: true,
			},
		},
	}
}

func applyOnlineConfig(data []byte) error {
	var hostsLines []string
	var upstreamLines []string
	var dnsLines []string
	var bareValues []string

	for _, raw := range strings.Split(string(data), "\n") {
		line := strings.TrimSpace(strings.TrimRight(raw, "\r"))
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if idx := strings.IndexByte(line, '#'); idx >= 0 {
			line = strings.TrimSpace(line[:idx])
		}
		if line == "" {
			continue
		}
		key, value, ok := splitConfigKV(line)
		if ok {
			switch key {
			case "upstream", "proxy", "agent":
				if value != "" {
					upstreamLines = append(upstreamLines, value)
				}
				continue
			case "dns", "nameserver":
				if value != "" {
					dnsLines = append(dnsLines, value)
				}
				continue
			case "host", "hosts":
				if value != "" {
					hostsLines = append(hostsLines, value)
				}
				continue
			}
		}
		fields := strings.Fields(line)
		if len(fields) >= 2 && net.ParseIP(fields[0]) != nil {
			hostsLines = append(hostsLines, strings.Join(fields, " "))
			continue
		}
		if len(fields) == 1 {
			bareValues = append(bareValues, fields[0])
		}
	}

	if len(upstreamLines) == 0 && len(hostsLines) == 0 && len(dnsLines) == 0 && len(bareValues) == 1 {
		upstreamLines = append(upstreamLines, bareValues[0])
	}

	if len(hostsLines) > 0 {
		content := strings.Join(ensureLocalhostHostLine(hostsLines), "\n") + "\n"
		path := envOrDefault("KARMA_MAPBOX_HOSTS_FILE", hostsFile)
		if err := writeConfigFile(path, []byte(content)); err != nil {
			return err
		}
		log.Printf("online config wrote %s", path)
	}
	if len(upstreamLines) > 0 {
		content := strings.Join(upstreamLines, "\n") + "\n"
		path := envOrDefault("KARMA_MAPBOX_UPSTREAM_FILE", upstreamFile)
		if err := writeConfigFile(path, []byte(content)); err != nil {
			return err
		}
		log.Printf("online config wrote %s", path)
	}
	if len(dnsLines) > 0 {
		content := strings.Join(dnsLines, "\n") + "\n"
		path := envOrDefault("KARMA_MAPBOX_DNS_FILE", dnsFile)
		if err := writeConfigFile(path, []byte(content)); err != nil {
			return err
		}
		log.Printf("online config wrote %s", path)
	}
	if len(hostsLines) == 0 && len(upstreamLines) == 0 && len(dnsLines) == 0 {
		return fmt.Errorf("online config did not contain hosts, upstream, or DNS entries")
	}
	return nil
}

func splitConfigKV(line string) (string, string, bool) {
	if idx := strings.IndexByte(line, '='); idx >= 0 {
		key := strings.ToLower(strings.TrimSpace(line[:idx]))
		value := strings.TrimSpace(line[idx+1:])
		return key, value, key != ""
	}
	fields := strings.Fields(line)
	if len(fields) >= 2 {
		key := strings.ToLower(strings.TrimSuffix(fields[0], ":"))
		switch key {
		case "upstream", "proxy", "agent", "dns", "nameserver", "host", "hosts":
			return key, strings.Join(fields[1:], " "), true
		}
	}
	return "", "", false
}

func ensureLocalhostHostLine(lines []string) []string {
	for _, line := range lines {
		fields := strings.Fields(line)
		ip := net.ParseIP(firstField(fields))
		if len(fields) >= 2 && ip != nil && ip.IsLoopback() {
			for _, field := range fields[1:] {
				if cleanHost(field) == "localhost" {
					return lines
				}
			}
		}
	}
	return append([]string{"127.0.0.1 localhost"}, lines...)
}

func firstField(fields []string) string {
	if len(fields) == 0 {
		return ""
	}
	return fields[0]
}

func writeConfigFile(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func currentDNSServers() []string {
	for _, path := range []string{envOrDefault("KARMA_MAPBOX_DNS_FILE", dnsFile), legacyDNSFile} {
		servers, err := readDNSServers(path)
		if err == nil && len(servers) > 0 {
			return servers
		}
	}
	return fallbackDNSServers
}

func readDNSServers(path string) ([]string, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	var servers []string
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		server := strings.TrimSpace(scanner.Text())
		if server == "" || strings.HasPrefix(server, "#") {
			continue
		}
		if !strings.Contains(server, ":") {
			server += ":53"
		}
		servers = append(servers, server)
	}
	if len(servers) == 0 {
		return nil, fmt.Errorf("no DNS servers configured in %s", path)
	}
	return servers, scanner.Err()
}

func currentAllowedHosts() map[string]bool {
	path := envOrDefault("KARMA_MAPBOX_HOSTS_FILE", hostsFile)
	hosts, err := readAllowedHosts(path)
	if err != nil || len(hosts) == 0 {
		return defaultAllowedHosts
	}
	return hosts
}

func readAllowedHosts(path string) (map[string]bool, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	hosts := make(map[string]bool)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := scanner.Text()
		if idx := strings.IndexByte(line, '#'); idx >= 0 {
			line = line[:idx]
		}
		for _, field := range strings.Fields(line) {
			field = cleanHost(field)
			if field == "" || field == "localhost" || net.ParseIP(field) != nil {
				continue
			}
			hosts[field] = true
		}
	}
	if err := scanner.Err(); err != nil {
		return nil, err
	}
	return hosts, nil
}

func currentUpstreamAddr() string {
	value := strings.TrimSpace(os.Getenv("KARMA_MAPBOX_UPSTREAM"))
	if value == "" {
		path := envOrDefault("KARMA_MAPBOX_UPSTREAM_FILE", upstreamFile)
		data, err := os.ReadFile(path)
		if err == nil {
			for _, line := range strings.Split(string(data), "\n") {
				if idx := strings.IndexByte(line, '#'); idx >= 0 {
					line = line[:idx]
				}
				value = strings.TrimSpace(line)
				if value != "" {
					break
				}
			}
		}
	}
	value = strings.TrimSpace(value)
	switch strings.ToLower(value) {
	case "", "direct", "none", "mapbox":
		return ""
	}
	if !strings.Contains(value, ":") {
		value += ":443"
	}
	return value
}

func envOrDefault(name, fallback string) string {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback
	}
	return value
}

func cleanHost(hostport string) string {
	host := strings.ToLower(strings.TrimSpace(hostport))
	if strings.Contains(host, ":") {
		if splitHost, _, err := net.SplitHostPort(host); err == nil {
			host = splitHost
		}
	}
	return strings.Trim(host, "[]")
}

func copyHeaders(dst, src http.Header) {
	for key, values := range src {
		lower := strings.ToLower(key)
		if lower == "connection" ||
			lower == "keep-alive" ||
			lower == "proxy-authenticate" ||
			lower == "proxy-authorization" ||
			lower == "te" ||
			lower == "trailer" ||
			lower == "trailers" ||
			lower == "transfer-encoding" ||
			lower == "upgrade" {
			continue
		}
		for _, value := range values {
			dst.Add(key, value)
		}
	}
}
