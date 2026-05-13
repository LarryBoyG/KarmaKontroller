//go:build windows

package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/getlantern/systray"
)

var logFile *os.File

type windowsAgent struct {
	mu               sync.Mutex
	server           *http.Server
	status           *systray.MenuItem
	upstreamStatus   *systray.MenuItem
	controllerStatus *systray.MenuItem
	startItem        *systray.MenuItem
	stopItem         *systray.MenuItem
	patchItem        *systray.MenuItem
	flashItem        *systray.MenuItem
	backupItem       *systray.MenuItem
	toolStatus       *systray.MenuItem
	jobMu            sync.Mutex
	jobRunning       bool
	lastPatchedImage string
	listenAddr       string
	controllerIP     string
	tooltipBase      string
}

func configureLogging() {
	path := os.Getenv("KARMA_MAPBOX_LOG")
	if path == "" {
		path = filepath.Join(karmaKontrollerDir(), "agent.log")
	}
	if path == "" {
		if exe, err := os.Executable(); err == nil {
			path = filepath.Join(filepath.Dir(exe), "KarmaKontroller.log")
		}
	}
	if path == "" {
		return
	}

	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return
	}
	logFile = f
	log.SetOutput(f)
}

func runPlatform() error {
	agent := &windowsAgent{}

	onReady := func() {
		systray.SetIcon(trayIcon(false))
		systray.SetTitle("KarmaKontroller")
		agent.tooltipBase = "KarmaKontroller - stopped"
		agent.refreshTooltipLocked()

		agent.status = systray.AddMenuItem("Stopped", "Current proxy status")
		agent.status.Disable()
		agent.upstreamStatus = systray.AddMenuItem("upstream.txt: not ready", "Local upstream file to drag to the controller")
		agent.upstreamStatus.Disable()
		agent.controllerStatus = systray.AddMenuItem("Controller: scanning...", "Controller IP detected on the local Wi-Fi network")
		agent.controllerStatus.Disable()
		agent.startItem = systray.AddMenuItem("Start", "Start the Mapbox proxy")
		agent.stopItem = systray.AddMenuItem("Stop", "Stop the Mapbox proxy")
		agent.stopItem.Disable()
		openFolder := systray.AddMenuItem("Open Folder", "Open the folder containing upstream.txt and logs")
		systray.AddSeparator()
		agent.patchItem = systray.AddMenuItem("Patch / Flash / Backup...", "Open KarmaKontroller image tools")
		systray.AddSeparator()
		quit := systray.AddMenuItem("Quit", "Stop KarmaKontroller")

		go func() {
			for range agent.startItem.ClickedCh {
				go agent.start()
			}
		}()

		go func() {
			for range agent.stopItem.ClickedCh {
				go agent.stop()
			}
		}()

		go func() {
			for range openFolder.ClickedCh {
				openKarmaKontrollerDir()
			}
		}()

		go func() {
			for range agent.patchItem.ClickedCh {
				if err := launchPatchWindow(); err != nil {
					log.Printf("open image tools: %v", err)
					showMessage("KarmaKontroller", "Could not open the image tools window:\n\n"+err.Error(), messageBoxIconError)
				}
			}
		}()

		go func() {
			<-quit.ClickedCh
			agent.stop()
			systray.Quit()
		}()

		go agent.start()
		go agent.discoverControllerLoop()
	}

	systray.Run(onReady, func() {
		if logFile != nil {
			_ = logFile.Close()
		}
	})
	return nil
}

func (a *windowsAgent) start() {
	a.mu.Lock()
	if a.server != nil {
		a.mu.Unlock()
		return
	}
	a.setStartingLocked()
	a.mu.Unlock()

	configureListenAddr()

	server, addr, err := newProxyServer()
	if err != nil {
		a.setError(err)
		return
	}

	ln, err := net.Listen("tcp4", addr)
	if err != nil {
		a.setError(fmt.Errorf("listen %s: %w", addr, err))
		return
	}

	a.mu.Lock()
	if a.server != nil {
		a.mu.Unlock()
		_ = ln.Close()
		return
	}
	a.server = server
	a.setListeningLocked(addr)
	a.mu.Unlock()

	log.Printf("listening on %s", addr)
	if upstream, path, err := writeUpstreamFiles(addr); err != nil {
		log.Printf("upstream file: %v", err)
	} else {
		log.Printf("wrote %s with %s", path, upstream)
		if a.upstreamStatus != nil {
			a.upstreamStatus.SetTitle("upstream.txt: " + upstream)
		}
	}

	if err := server.ServeTLS(ln, "", ""); err != nil && !errors.Is(err, http.ErrServerClosed) {
		a.mu.Lock()
		if a.server == server {
			a.server = nil
		}
		a.mu.Unlock()
		a.setError(err)
		return
	}

	a.mu.Lock()
	if a.server == server {
		a.server = nil
		a.setStoppedLocked("Stopped")
	}
	a.mu.Unlock()
}

func (a *windowsAgent) stop() {
	a.mu.Lock()
	server := a.server
	if server == nil {
		a.setStoppedLocked("Stopped")
		a.mu.Unlock()
		return
	}
	a.setStoppingLocked()
	a.mu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	if err := server.Shutdown(ctx); err != nil {
		log.Printf("shutdown: %v", err)
	}
	cancel()
}

func (a *windowsAgent) setStartingLocked() {
	systray.SetIcon(trayIcon(false))
	a.tooltipBase = "KarmaKontroller - starting"
	a.refreshTooltipLocked()
	if a.status != nil {
		a.status.SetTitle("Starting...")
	}
	if a.startItem != nil {
		a.startItem.Disable()
	}
	if a.stopItem != nil {
		a.stopItem.Disable()
	}
}

func (a *windowsAgent) setListeningLocked(addr string) {
	systray.SetIcon(trayIcon(true))
	a.listenAddr = addr
	a.tooltipBase = "KarmaKontroller - listening on " + addr
	a.refreshTooltipLocked()
	if a.status != nil {
		a.status.SetTitle("Listening on " + addr)
	}
	if a.startItem != nil {
		a.startItem.Disable()
	}
	if a.stopItem != nil {
		a.stopItem.Enable()
	}
}

func (a *windowsAgent) setStoppingLocked() {
	systray.SetIcon(trayIcon(false))
	a.tooltipBase = "KarmaKontroller - stopping"
	a.refreshTooltipLocked()
	if a.status != nil {
		a.status.SetTitle("Stopping...")
	}
	if a.startItem != nil {
		a.startItem.Disable()
	}
	if a.stopItem != nil {
		a.stopItem.Disable()
	}
}

func (a *windowsAgent) setStoppedLocked(title string) {
	systray.SetIcon(trayIcon(false))
	a.listenAddr = ""
	a.tooltipBase = "KarmaKontroller - stopped"
	a.refreshTooltipLocked()
	if a.status != nil {
		a.status.SetTitle(title)
	}
	if a.startItem != nil {
		a.startItem.Enable()
	}
	if a.stopItem != nil {
		a.stopItem.Disable()
	}
}

func (a *windowsAgent) setError(err error) {
	log.Printf("error: %v", err)
	title := err.Error()
	if len(title) > 80 {
		title = title[:77] + "..."
	}
	a.mu.Lock()
	a.server = nil
	a.setStoppedLocked("Error: " + title)
	a.mu.Unlock()
	a.mu.Lock()
	a.tooltipBase = "KarmaKontroller - error"
	a.refreshTooltipLocked()
	a.mu.Unlock()
}

func (a *windowsAgent) refreshTooltipLocked() {
	base := a.tooltipBase
	if base == "" {
		base = "KarmaKontroller"
	}
	controller := "controller not found"
	if a.controllerIP != "" {
		controller = "controller " + a.controllerIP
	}
	systray.SetTooltip(base + " - " + controller)
}

func (a *windowsAgent) setControllerIP(ip string) {
	a.mu.Lock()
	a.controllerIP = ip
	if a.controllerStatus != nil {
		if ip == "" {
			a.controllerStatus.SetTitle("Controller: not found")
		} else {
			a.controllerStatus.SetTitle("Controller: " + ip)
		}
	}
	a.refreshTooltipLocked()
	a.mu.Unlock()
}

func (a *windowsAgent) discoverControllerLoop() {
	a.setControllerIP(discoverControllerIP())
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for range ticker.C {
		a.setControllerIP(discoverControllerIP())
	}
}

func karmaKontrollerDir() string {
	if dir, err := os.UserConfigDir(); err == nil && dir != "" {
		return filepath.Join(dir, "KarmaKontroller")
	}
	if exe, err := os.Executable(); err == nil {
		return filepath.Dir(exe)
	}
	return "."
}

func openKarmaKontrollerDir() {
	dir := karmaKontrollerDir()
	if err := os.MkdirAll(dir, 0755); err != nil {
		log.Printf("open config folder: %v", err)
		return
	}
	if err := exec.Command("explorer.exe", dir).Start(); err != nil {
		log.Printf("open config folder: %v", err)
	}
}

func configureListenAddr() {
	if strings.TrimSpace(os.Getenv("KARMA_MAPBOX_PROXY_ADDR")) != "" {
		return
	}

	host, port, err := net.SplitHostPort(defaultListenAddr)
	if err != nil {
		port = addrPort(defaultListenAddr)
	} else if host != "" && !isWildcardOrLoopback(host) {
		return
	}

	if ip, ok := preferredLocalIPv4(); ok {
		defaultListenAddr = net.JoinHostPort(ip.String(), port)
	}
}

func writeUpstreamFiles(listenAddr string) (string, string, error) {
	host, port, err := hostPortForUpstream(listenAddr)
	if err != nil {
		return "", "", err
	}
	upstream := net.JoinHostPort(host, port)

	dir := karmaKontrollerDir()
	if err := os.MkdirAll(dir, 0755); err != nil {
		return "", "", err
	}
	upstreamPath := filepath.Join(dir, "upstream.txt")
	if err := os.WriteFile(upstreamPath, []byte(upstream+"\n"), 0644); err != nil {
		return "", "", err
	}

	var candidates strings.Builder
	candidates.WriteString("# Possible upstream.txt values. Use the one on the same network as the controller.\n")
	for _, ip := range localIPv4s() {
		candidates.WriteString(net.JoinHostPort(ip.String(), port))
		candidates.WriteByte('\n')
	}
	if candidates.Len() > 0 {
		_ = os.WriteFile(filepath.Join(dir, "upstream-candidates.txt"), []byte(candidates.String()), 0644)
	}

	return upstream, upstreamPath, nil
}

func hostPortForUpstream(listenAddr string) (string, string, error) {
	host, port, err := net.SplitHostPort(listenAddr)
	if err != nil {
		port = addrPort(listenAddr)
	}
	if host == "" || isWildcardOrLoopback(host) {
		ip, ok := preferredLocalIPv4()
		if !ok {
			return "", "", fmt.Errorf("no usable local IPv4 address found")
		}
		host = ip.String()
	}
	return host, port, nil
}

func addrPort(addr string) string {
	if _, port, err := net.SplitHostPort(addr); err == nil {
		return port
	}
	if i := strings.LastIndex(addr, ":"); i >= 0 && i+1 < len(addr) {
		return addr[i+1:]
	}
	return "443"
}

func preferredLocalIPv4() (net.IP, bool) {
	if routed, ok := routedIPv4(); ok {
		return routed, true
	}
	for _, ip := range localIPv4s() {
		if ip.IsPrivate() {
			return ip, true
		}
	}
	ips := localIPv4s()
	if len(ips) == 0 {
		return nil, false
	}
	return ips[0], true
}

func routedIPv4() (net.IP, bool) {
	conn, err := net.DialTimeout("udp4", "8.8.8.8:80", 300*time.Millisecond)
	if err != nil {
		return nil, false
	}
	defer conn.Close()
	addr, ok := conn.LocalAddr().(*net.UDPAddr)
	if !ok {
		return nil, false
	}
	ip := addr.IP.To4()
	if !isUsableLocalIPv4(ip) {
		return nil, false
	}
	return append(net.IP(nil), ip...), true
}

func localIPv4s() []net.IP {
	ifaces, err := net.Interfaces()
	if err != nil {
		log.Printf("list interfaces: %v", err)
		return nil
	}
	var ips []net.IP
	for _, iface := range ifaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, addr := range addrs {
			ipnet, ok := addr.(*net.IPNet)
			if !ok {
				continue
			}
			ip := ipnet.IP.To4()
			if isUsableLocalIPv4(ip) {
				ips = append(ips, append(net.IP(nil), ip...))
			}
		}
	}
	return ips
}

func discoverControllerIP() string {
	candidates := controllerCandidateIPs()
	if len(candidates) == 0 {
		return ""
	}

	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()

	results := make(chan string, 1)
	sem := make(chan struct{}, 48)
	done := make(chan struct{})
	var wg sync.WaitGroup
	client := &http.Client{Timeout: 900 * time.Millisecond}

	for _, candidate := range candidates {
		ip := candidate
		wg.Add(1)
		go func() {
			defer wg.Done()
			select {
			case sem <- struct{}{}:
				defer func() { <-sem }()
			case <-ctx.Done():
				return
			}
			if probeControllerIP(ctx, client, ip) {
				select {
				case results <- ip:
					cancel()
				default:
				}
			}
		}()
	}

	go func() {
		wg.Wait()
		close(done)
	}()

	select {
	case ip := <-results:
		return ip
	case <-done:
		return ""
	case <-ctx.Done():
		select {
		case ip := <-results:
			return ip
		default:
			return ""
		}
	}
}

func probeControllerIP(ctx context.Context, client *http.Client, ip string) bool {
	for _, path := range []string{"/data/", "/"} {
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+ip+":8080"+path, nil)
		if err != nil {
			continue
		}
		req.Header.Set("User-Agent", "KarmaKontroller")
		resp, err := client.Do(req)
		if err != nil {
			continue
		}
		_ = resp.Body.Close()
		if resp.StatusCode >= 200 && resp.StatusCode < 500 {
			return true
		}
	}
	return false
}

func controllerCandidateIPs() []string {
	local := localIPv4s()
	seen := make(map[string]bool)
	var out []string
	for _, ip := range local {
		v4 := ip.To4()
		if !isUsableLocalIPv4(v4) || !v4.IsPrivate() {
			continue
		}
		for host := 1; host <= 254; host++ {
			candidate := net.IPv4(v4[0], v4[1], v4[2], byte(host)).String()
			if candidate == v4.String() || seen[candidate] {
				continue
			}
			seen[candidate] = true
			out = append(out, candidate)
		}
	}
	return out
}

func isUsableLocalIPv4(ip net.IP) bool {
	if ip == nil || ip.IsLoopback() || ip.IsUnspecified() || ip.IsMulticast() {
		return false
	}
	return !(ip[0] == 169 && ip[1] == 254)
}

func isWildcardOrLoopback(host string) bool {
	host = strings.Trim(strings.ToLower(host), "[]")
	return host == "" || host == "0.0.0.0" || host == "::" || host == "localhost" ||
		host == "127.0.0.1" || strings.HasPrefix(host, "127.")
}

func trayIcon(listening bool) []byte {
	const width = 16
	const height = 16
	const headerSize = 6
	const dirSize = 16
	const dibHeaderSize = 40
	const xorBytes = width * height * 4
	const andBytes = height * 4
	const imageSize = dibHeaderSize + xorBytes + andBytes
	const imageOffset = headerSize + dirSize

	icon := make([]byte, imageOffset+imageSize)
	put16(icon[2:], 1)
	put16(icon[4:], 1)
	icon[6] = width
	icon[7] = height
	icon[8] = 0
	icon[9] = 0
	put16(icon[10:], 1)
	put16(icon[12:], 32)
	put32(icon[14:], imageSize)
	put32(icon[18:], imageOffset)

	dib := icon[imageOffset:]
	put32(dib[0:], dibHeaderSize)
	put32(dib[4:], width)
	put32(dib[8:], height*2)
	put16(dib[12:], 1)
	put16(dib[14:], 32)
	put32(dib[20:], xorBytes+andBytes)

	pixels := dib[dibHeaderSize : dibHeaderSize+xorBytes]
	kR, kG, kB := byte(0x35), byte(0xd0), byte(0x62)
	if !listening {
		kR, kG, kB = 0xe5, 0x38, 0x35
	}
	for y := 0; y < height; y++ {
		for x := 0; x < width; x++ {
			dstY := height - 1 - y
			off := (dstY*width + x) * 4
			edge := x == 0 || y == 0 || x == width-1 || y == height-1
			vertical := x >= 4 && x <= 6 && y >= 3 && y <= 12
			upper := y >= 3 && y <= 8 && x >= 7 && x <= 12 && x+y >= 14 && x+y <= 17
			lower := y >= 8 && y <= 12 && x >= 7 && x <= 12 && x-y >= -1 && x-y <= 2
			letter := vertical || upper || lower
			switch {
			case edge:
				pixels[off+0] = 0x2d
				pixels[off+1] = 0x2d
				pixels[off+2] = 0x2d
				pixels[off+3] = 0xff
			case letter:
				pixels[off+0] = kB
				pixels[off+1] = kG
				pixels[off+2] = kR
				pixels[off+3] = 0xff
			default:
				pixels[off+0] = 0x18
				pixels[off+1] = 0x18
				pixels[off+2] = 0x18
				pixels[off+3] = 0xff
			}
		}
	}
	return icon
}

func put16(dst []byte, v uint16) {
	dst[0] = byte(v)
	dst[1] = byte(v >> 8)
}

func put32(dst []byte, v uint32) {
	dst[0] = byte(v)
	dst[1] = byte(v >> 8)
	dst[2] = byte(v >> 16)
	dst[3] = byte(v >> 24)
}
