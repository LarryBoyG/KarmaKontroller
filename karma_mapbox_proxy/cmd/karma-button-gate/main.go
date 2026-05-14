//go:build linux

package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

const (
	iocNrBits   = 8
	iocTypeBits = 8
	iocSizeBits = 14

	iocNrShift   = 0
	iocTypeShift = iocNrShift + iocNrBits
	iocSizeShift = iocTypeShift + iocTypeBits
	iocDirShift  = iocSizeShift + iocSizeBits

	iocRead = 2
)

func main() {
	seconds := flag.Int("seconds", 45, "seconds to watch for the boot combo")
	keyList := flag.String("keys", "306,307", "comma-separated Linux input key codes")
	flag.Parse()

	keys, err := parseKeys(*keyList)
	if err != nil {
		fmt.Fprintf(os.Stderr, "invalid keys: %v\n", err)
		os.Exit(1)
	}
	if len(keys) == 0 {
		fmt.Fprintln(os.Stderr, "no keys supplied")
		os.Exit(1)
	}

	fmt.Printf("karma-button-gate watching keys %s for %d seconds\n", joinKeys(keys), *seconds)
	deadline := time.Now().Add(time.Duration(*seconds) * time.Second)
	for {
		found, devices, err := scanInputKeys(keys)
		if err != nil {
			fmt.Printf("scan warning: %v\n", err)
		}
		if len(devices) > 0 {
			fmt.Printf("input devices: %s\n", strings.Join(devices, ", "))
		}
		if len(found) > 0 {
			fmt.Printf("keys down: %s\n", describeFound(found))
		}
		if allKeysFound(keys, found) {
			fmt.Printf("combo detected: %s\n", describeFound(found))
			os.Exit(0)
		}
		if *seconds <= 0 || time.Now().After(deadline) {
			break
		}
		time.Sleep(250 * time.Millisecond)
	}

	fmt.Println("combo not detected")
	os.Exit(2)
}

func parseKeys(value string) ([]int, error) {
	var keys []int
	for _, part := range strings.Split(value, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		key, err := strconv.Atoi(part)
		if err != nil {
			return nil, err
		}
		if key < 0 || key > 767 {
			return nil, fmt.Errorf("key code out of range: %d", key)
		}
		keys = append(keys, key)
	}
	return keys, nil
}

func scanInputKeys(keys []int) (map[int]string, []string, error) {
	paths, err := filepath.Glob("/dev/input/event*")
	if err != nil {
		return nil, nil, err
	}
	if len(paths) == 0 {
		return nil, nil, fmt.Errorf("no /dev/input/event* devices found")
	}

	found := make(map[int]string)
	devices := make([]string, 0, len(paths))
	var lastErr error
	for _, path := range paths {
		file, err := os.Open(path)
		if err != nil {
			lastErr = err
			continue
		}
		name := eventDeviceName(file)
		label := path
		if name != "" {
			label += "(" + name + ")"
		}
		devices = append(devices, label)

		bits := make([]byte, 128)
		if err := ioctl(file.Fd(), eviocgkey(len(bits)), uintptr(unsafe.Pointer(&bits[0]))); err != nil {
			lastErr = err
			_ = file.Close()
			continue
		}
		for _, key := range keys {
			if bitSet(bits, key) {
				found[key] = label
			}
		}
		_ = file.Close()
	}
	return found, devices, lastErr
}

func eventDeviceName(file *os.File) string {
	buf := make([]byte, 128)
	if err := ioctl(file.Fd(), eviocgname(len(buf)), uintptr(unsafe.Pointer(&buf[0]))); err != nil {
		return ""
	}
	n := 0
	for n < len(buf) && buf[n] != 0 {
		n++
	}
	return string(buf[:n])
}

func ioctl(fd uintptr, request uintptr, arg uintptr) error {
	_, _, errno := syscall.Syscall(syscall.SYS_IOCTL, fd, request, arg)
	if errno != 0 {
		return errno
	}
	return nil
}

func eviocgname(size int) uintptr {
	return ior('E', 0x06, size)
}

func eviocgkey(size int) uintptr {
	return ior('E', 0x18, size)
}

func ior(t byte, nr byte, size int) uintptr {
	return ioc(iocRead, t, nr, size)
}

func ioc(dir int, t byte, nr byte, size int) uintptr {
	return uintptr((dir << iocDirShift) | (size << iocSizeShift) | (int(t) << iocTypeShift) | (int(nr) << iocNrShift))
}

func bitSet(bits []byte, key int) bool {
	index := key / 8
	if index < 0 || index >= len(bits) {
		return false
	}
	return bits[index]&(1<<uint(key%8)) != 0
}

func allKeysFound(keys []int, found map[int]string) bool {
	for _, key := range keys {
		if _, ok := found[key]; !ok {
			return false
		}
	}
	return true
}

func joinKeys(keys []int) string {
	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		parts = append(parts, strconv.Itoa(key))
	}
	return strings.Join(parts, ",")
}

func describeFound(found map[int]string) string {
	parts := make([]string, 0, len(found))
	for key, device := range found {
		parts = append(parts, fmt.Sprintf("%d on %s", key, device))
	}
	return strings.Join(parts, "; ")
}
