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
	"strings"
	"time"
)

//go:embed certs/mapbox-proxy.crt
var serverCertPEM []byte

//go:embed certs/mapbox-proxy.key
var serverKeyPEM []byte

//go:embed certs/upstream-roots.pem
var upstreamRootsPEM []byte

var version = "v4-data-hosts"
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
const legacyDNSFile = "/data/karma-mapbox-proxy-dns"

func main() {
	configureLogging()
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)
	if handled, err := runCLI(os.Args[1:]); handled {
		if err != nil {
			log.Printf("command failed: %v", err)
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	if err := runPlatform(); err != nil {
		log.Fatalf("karma-mapbox-proxy stopped: %v", err)
	}
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
				return dialer.DialContext(ctx, network, upstream)
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
