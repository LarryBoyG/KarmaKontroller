//go:build windows

package main

import (
	"crypto/md5"
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"strings"
	"time"
)

var patchInstallRecoveryWNC = []byte("#!/system/bin/sh\n\n/system/xbin/wncsud --daemon &\n/system/bin/karma-start-mapbox-proxy\n/system/bin/karma-start-file-browser &\n")

type patchProgress func(percent int, status string)

func reportPatchProgress(progress patchProgress, percent int, status string) {
	if progress == nil {
		return
	}
	if percent < 0 {
		percent = 0
	}
	if percent > 100 {
		percent = 100
	}
	progress(percent, status)
}

type patchAssets struct {
	mapboxProxy      []byte
	fileBrowser      []byte
	buttonGate       []byte
	startMapboxProxy []byte
	startFileBrowser []byte
	hosts            []byte
	wmm              []byte
	proxyCA          []byte
}

type imagePatchFile struct {
	path       string
	systemPath string
	content    []byte
	mode       uint16
}

func patchSystemImage(source, dest string, progress patchProgress) error {
	reportPatchProgress(progress, 0, "Loading patch assets")
	assets, err := loadPatchAssets()
	if err != nil {
		return err
	}

	sourceAbs, err := filepath.Abs(source)
	if err != nil {
		return err
	}
	destAbs, err := filepath.Abs(dest)
	if err != nil {
		return err
	}
	if strings.EqualFold(sourceAbs, destAbs) {
		return fmt.Errorf("source and destination must be different files")
	}

	reportPatchProgress(progress, 2, "Validating source image")
	if err := validateSystemImagePath(sourceAbs); err != nil {
		return err
	}

	reportPatchProgress(progress, 3, "Copying source image")
	if err := copyFileWithProgress(sourceAbs, destAbs, func(copied, total int64) {
		if total <= 0 {
			reportPatchProgress(progress, 5, "Copying source image")
			return
		}
		reportPatchProgress(progress, 3+int((copied*47)/total), "Copying source image")
	}); err != nil {
		return err
	}

	reportPatchProgress(progress, 52, "Opening patched image")
	image, err := openExt4Image(destAbs)
	if err != nil {
		return err
	}
	defer image.close()

	reportPatchProgress(progress, 55, "Validating system image")
	if err := validateKarmaSystemImage(image); err != nil {
		return err
	}

	files := []imagePatchFile{
		{
			path:       "/etc/security/cacerts/b60c023d.0",
			systemPath: "/system/etc/security/cacerts/b60c023d.0",
			content:    assets.proxyCA,
			mode:       0o100644,
		},
		{
			path:       "/etc/hosts",
			systemPath: "/system/etc/hosts",
			content:    assets.hosts,
			mode:       0o100644,
		},
		{
			path:       "/etc/WMM.COF",
			systemPath: "/system/etc/WMM.COF",
			content:    assets.wmm,
			mode:       0o100644,
		},
		{
			path:       "/bin/karma-mapbox-proxy",
			systemPath: "/system/bin/karma-mapbox-proxy",
			content:    assets.mapboxProxy,
			mode:       0o100755,
		},
		{
			path:       "/bin/karma-file-browser",
			systemPath: "/system/bin/karma-file-browser",
			content:    assets.fileBrowser,
			mode:       0o100755,
		},
		{
			path:       "/bin/karma-button-gate",
			systemPath: "/system/bin/karma-button-gate",
			content:    assets.buttonGate,
			mode:       0o100755,
		},
		{
			path:       "/bin/karma-start-mapbox-proxy",
			systemPath: "/system/bin/karma-start-mapbox-proxy",
			content:    assets.startMapboxProxy,
			mode:       0o100755,
		},
		{
			path:       "/bin/karma-start-file-browser",
			systemPath: "/system/bin/karma-start-file-browser",
			content:    assets.startFileBrowser,
			mode:       0o100755,
		},
		{
			path:       "/etc/install-recovery-wnc.sh",
			systemPath: "/system/etc/install-recovery-wnc.sh",
			content:    patchInstallRecoveryWNC,
			mode:       0o100755,
		},
	}

	for i, file := range files {
		stepPercent := 60 + int((i*35)/len(files))
		reportPatchProgress(progress, stepPercent, "Patching "+file.systemPath)
		if err := image.upsertFile(file.path, file.content, file.mode); err != nil {
			return err
		}
		if err := image.updateChksumList(file.systemPath, file.content); err != nil {
			return err
		}
		reportPatchProgress(progress, 60+int(((i+1)*35)/len(files)), "Patched "+file.systemPath)
	}

	reportPatchProgress(progress, 100, "Patch complete")
	return nil
}

func validateSystemImagePath(path string) error {
	image, err := openExt4ImageReadOnly(path)
	if err != nil {
		return err
	}
	defer image.close()
	return validateKarmaSystemImage(image)
}

func validateKarmaSystemImage(image *ext4Image) error {
	required := []string{
		"/etc/hosts",
		"/etc/WMM.COF",
		"/etc/install-recovery-wnc.sh",
		"/etc/security/cacerts",
		"/bin",
		"/chksum_list",
	}
	for _, path := range required {
		if _, err := image.resolve(path); err != nil {
			return fmt.Errorf("not a compatible Karma system image: %s: %w", path, err)
		}
	}
	if err := image.validatePatchSource(); err != nil {
		return err
	}
	return nil
}

func loadPatchAssets() (patchAssets, error) {
	assets := patchAssets{}
	var err error
	if assets.mapboxProxy, err = readPatchAsset("karma-mapbox-proxy"); err != nil {
		return assets, err
	}
	if assets.fileBrowser, err = readPatchAsset("karma-file-browser"); err != nil {
		return assets, err
	}
	if assets.buttonGate, err = readPatchAsset("karma-button-gate"); err != nil {
		return assets, err
	}
	if assets.startMapboxProxy, err = readPatchAsset("karma-start-mapbox-proxy"); err != nil {
		return assets, err
	}
	if assets.startFileBrowser, err = readPatchAsset("karma-start-file-browser"); err != nil {
		return assets, err
	}
	if assets.hosts, err = readPatchAsset("hosts"); err != nil {
		return assets, err
	}
	if assets.wmm, err = readPatchAsset("WMM.COF"); err != nil {
		return assets, err
	}
	if assets.proxyCA, err = readPatchAsset("b60c023d.0"); err != nil {
		return assets, err
	}
	return assets, nil
}

func readPatchAsset(name string) ([]byte, error) {
	var roots []string
	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		roots = append(roots,
			filepath.Join(exeDir, "assets"),
			filepath.Join(exeDir, "KarmaKontroller-assets"),
			filepath.Join(exeDir, "karma_mapbox_proxy", "assets"),
		)
	}
	if cwd, err := os.Getwd(); err == nil {
		roots = append(roots,
			filepath.Join(cwd, "assets"),
			filepath.Join(cwd, "karma_mapbox_proxy", "assets"),
		)
	}

	for _, root := range roots {
		path := filepath.Join(root, name)
		data, err := os.ReadFile(path)
		if err == nil {
			return data, nil
		}
	}
	return nil, fmt.Errorf("missing patch asset %q; keep the assets folder next to KarmaKontroller.exe", name)
}

func (e *ext4Image) validatePatchSource() error {
	if _, err := e.resolve("/etc/install-recovery.sh"); err == nil {
		return fmt.Errorf("system image contains /system/etc/install-recovery.sh from an older experimental patch; start from a clean stock system.img")
	}
	if _, err := e.resolve("/bin/karma-start-mapbox-proxy-data"); err == nil {
		return fmt.Errorf("system image contains /system/bin/karma-start-mapbox-proxy-data from an older experimental patch; start from a clean stock system.img")
	}
	if _, err := e.resolve("/bin/karma-file-browser"); err == nil {
		return fmt.Errorf("system image already contains a KarmaKontroller file browser patch; start from a clean stock system.img")
	}
	if e.fileContains("/etc/install-recovery-wnc.sh", "karma-start-mapbox-proxy-data") {
		return fmt.Errorf("system image contains an older experimental install-recovery hook; start from a clean stock system.img")
	}
	if e.fileContains("/etc/install-recovery-wnc.sh", "karma-start-file-browser") {
		return fmt.Errorf("system image contains an older file browser startup hook; start from a clean stock system.img")
	}
	if e.fileContains("/etc/dhcpcd/dhcpcd-hooks/95-configured", "karma-start-mapbox-proxy") ||
		e.fileContains("/etc/dhcpcd/dhcpcd-hooks/95-configured", "karma-mapbox-proxy-dns") {
		return fmt.Errorf("system image contains an older experimental DHCP hook patch; start from a clean stock system.img")
	}
	return nil
}

func (e *ext4Image) fileContains(path, needle string) bool {
	ino, err := e.resolve(path)
	if err != nil {
		return false
	}
	data, err := e.readFile(ino)
	if err != nil {
		return false
	}
	return strings.Contains(string(data), needle)
}

func copyFileWithProgress(source, dest string, progress func(copied, total int64)) error {
	in, err := os.Open(source)
	if err != nil {
		return err
	}
	defer in.Close()
	info, err := in.Stat()
	if err != nil {
		return err
	}
	total := info.Size()

	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		return err
	}

	tmp := dest + ".tmp"
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0644)
	if err != nil {
		return err
	}

	buffer := make([]byte, 4*1024*1024)
	var copied int64
	for {
		n, readErr := in.Read(buffer)
		if n > 0 {
			written, writeErr := out.Write(buffer[:n])
			copied += int64(written)
			if progress != nil {
				progress(copied, total)
			}
			if writeErr != nil {
				_ = out.Close()
				_ = os.Remove(tmp)
				return writeErr
			}
			if written != n {
				_ = out.Close()
				_ = os.Remove(tmp)
				return io.ErrShortWrite
			}
		}
		if readErr == io.EOF {
			break
		}
		if readErr != nil {
			_ = out.Close()
			_ = os.Remove(tmp)
			return readErr
		}
	}
	closeErr := out.Close()
	if closeErr != nil {
		_ = os.Remove(tmp)
		return closeErr
	}
	if err := os.Rename(tmp, dest); err != nil {
		_ = os.Remove(dest)
		if err := os.Rename(tmp, dest); err != nil {
			_ = os.Remove(tmp)
			return err
		}
	}
	return nil
}

type ext4Image struct {
	f               *os.File
	blockSize       uint64
	inodeSize       uint16
	inodesPerGroup  uint32
	blocksPerGroup  uint32
	firstDataBlock  uint64
	descSize        uint16
	blocksCount     uint64
	groups          uint64
	groupDescOffset uint64
	groupDescTable  []byte
}

func openExt4Image(path string) (*ext4Image, error) {
	return openExt4ImageWithFlag(path, os.O_RDWR)
}

func openExt4ImageReadOnly(path string) (*ext4Image, error) {
	return openExt4ImageWithFlag(path, os.O_RDONLY)
}

func openExt4ImageWithFlag(path string, flag int) (*ext4Image, error) {
	f, err := os.OpenFile(path, flag, 0)
	if err != nil {
		return nil, err
	}

	sb := make([]byte, 1024)
	if _, err := f.ReadAt(sb, 1024); err != nil {
		_ = f.Close()
		return nil, err
	}
	if le16(sb, 56) != 0xEF53 {
		_ = f.Close()
		return nil, fmt.Errorf("not an ext filesystem image")
	}

	blockSize := uint64(1024) << le32(sb, 24)
	inodeSize := le16(sb, 88)
	inodesPerGroup := le32(sb, 40)
	blocksPerGroup := le32(sb, 32)
	firstDataBlock := uint64(le32(sb, 20))
	descSize := le16(sb, 254)
	if descSize == 0 {
		descSize = 32
	}
	blocksCount := uint64(le32(sb, 4)) | uint64(le32(sb, 336))<<32
	groups := (blocksCount - firstDataBlock + uint64(blocksPerGroup) - 1) / uint64(blocksPerGroup)
	groupDescBlock := uint64(1)
	if blockSize == 1024 {
		groupDescBlock = 2
	}
	groupDescOffset := groupDescBlock * blockSize
	groupDescBytes := groups * uint64(descSize)
	if groupDescBytes > uint64(int(^uint(0)>>1)) {
		_ = f.Close()
		return nil, fmt.Errorf("group descriptor table is too large")
	}
	groupDescTable := make([]byte, int(groupDescBytes))
	if _, err := f.ReadAt(groupDescTable, int64(groupDescOffset)); err != nil {
		_ = f.Close()
		return nil, err
	}

	return &ext4Image{
		f:               f,
		blockSize:       blockSize,
		inodeSize:       inodeSize,
		inodesPerGroup:  inodesPerGroup,
		blocksPerGroup:  blocksPerGroup,
		firstDataBlock:  firstDataBlock,
		descSize:        descSize,
		blocksCount:     blocksCount,
		groups:          groups,
		groupDescOffset: groupDescOffset,
		groupDescTable:  groupDescTable,
	}, nil
}

func (e *ext4Image) close() error {
	return e.f.Close()
}

func (e *ext4Image) groupDesc(group uint64) (blockBitmap, inodeBitmap, inodeTable uint64, err error) {
	off := group * uint64(e.descSize)
	if off+12 > uint64(len(e.groupDescTable)) {
		return 0, 0, 0, fmt.Errorf("group descriptor out of range: %d", group)
	}
	desc := e.groupDescTable[off : off+uint64(e.descSize)]
	return uint64(le32(desc, 0)), uint64(le32(desc, 4)), uint64(le32(desc, 8)), nil
}

func (e *ext4Image) readBlock(block uint64) ([]byte, error) {
	data := make([]byte, int(e.blockSize))
	_, err := e.f.ReadAt(data, int64(block*e.blockSize))
	return data, err
}

func (e *ext4Image) writeBlock(block uint64, data []byte) error {
	if uint64(len(data)) != e.blockSize {
		return fmt.Errorf("block write must be exactly %d bytes", e.blockSize)
	}
	_, err := e.f.WriteAt(data, int64(block*e.blockSize))
	return err
}

func (e *ext4Image) inodeOffset(ino uint32) (uint64, error) {
	if ino == 0 {
		return 0, fmt.Errorf("invalid inode 0")
	}
	group := uint64((ino - 1) / e.inodesPerGroup)
	index := uint64((ino - 1) % e.inodesPerGroup)
	_, _, inodeTable, err := e.groupDesc(group)
	if err != nil {
		return 0, err
	}
	return inodeTable*e.blockSize + index*uint64(e.inodeSize), nil
}

func (e *ext4Image) readInode(ino uint32) ([]byte, error) {
	off, err := e.inodeOffset(ino)
	if err != nil {
		return nil, err
	}
	data := make([]byte, int(e.inodeSize))
	_, err = e.f.ReadAt(data, int64(off))
	return data, err
}

func (e *ext4Image) writeInode(ino uint32, inode []byte) error {
	if len(inode) != int(e.inodeSize) {
		return fmt.Errorf("inode write size mismatch")
	}
	off, err := e.inodeOffset(ino)
	if err != nil {
		return err
	}
	_, err = e.f.WriteAt(inode, int64(off))
	return err
}

func (e *ext4Image) inodeSizeBytes(inode []byte) uint64 {
	size := uint64(le32(inode, 4))
	if le16(inode, 0)&0xF000 == 0x8000 && len(inode) >= 112 {
		size |= uint64(le32(inode, 108)) << 32
	}
	return size
}

func (e *ext4Image) inodeBlocks(inode []byte) ([]uint64, error) {
	if le16(inode, 40) != 0xF30A {
		blocks := make([]uint64, 0, 12)
		for i := 0; i < 12; i++ {
			block := le32(inode, 40+i*4)
			if block != 0 {
				blocks = append(blocks, uint64(block))
			}
		}
		return blocks, nil
	}
	return e.extentNodeBlocks(inode[40:100], le16(inode, 46))
}

func (e *ext4Image) extentNodeBlocks(node []byte, depth uint16) ([]uint64, error) {
	if le16(node, 0) != 0xF30A {
		return nil, fmt.Errorf("invalid extent header")
	}
	entries := le16(node, 2)
	out := make([]uint64, 0)
	if depth == 0 {
		for i := uint16(0); i < entries; i++ {
			off := 12 + int(i)*12
			length := le16(node, off+4) & 0x7FFF
			start := uint64(le32(node, off+8)) | uint64(le16(node, off+6))<<32
			for j := uint16(0); j < length; j++ {
				out = append(out, start+uint64(j))
			}
		}
		return out, nil
	}

	for i := uint16(0); i < entries; i++ {
		off := 12 + int(i)*12
		child := uint64(le32(node, off+4)) | uint64(le16(node, off+8))<<32
		block, err := e.readBlock(child)
		if err != nil {
			return nil, err
		}
		childBlocks, err := e.extentNodeBlocks(block, depth-1)
		if err != nil {
			return nil, err
		}
		out = append(out, childBlocks...)
	}
	return out, nil
}

func (e *ext4Image) readFile(ino uint32) ([]byte, error) {
	inode, err := e.readInode(ino)
	if err != nil {
		return nil, err
	}
	size := e.inodeSizeBytes(inode)
	blocks, err := e.inodeBlocks(inode)
	if err != nil {
		return nil, err
	}
	if size > uint64(int(^uint(0)>>1)) {
		return nil, fmt.Errorf("file too large to read")
	}
	out := make([]byte, 0, int(size))
	for _, block := range blocks {
		data, err := e.readBlock(block)
		if err != nil {
			return nil, err
		}
		out = append(out, data...)
		if uint64(len(out)) >= size {
			break
		}
	}
	return out[:int(size)], nil
}

type ext4DirEntry struct {
	offset   int
	child    uint32
	recLen   uint16
	nameLen  uint8
	fileType uint8
	name     string
}

func (e *ext4Image) listDir(ino uint32) ([]ext4DirEntry, error) {
	data, err := e.readFile(ino)
	if err != nil {
		return nil, err
	}
	var entries []ext4DirEntry
	for off := 0; off+8 <= len(data); {
		child := le32(data, off)
		recLen := le16(data, off+4)
		nameLen := data[off+6]
		fileType := data[off+7]
		if recLen < 8 || off+int(recLen) > len(data) {
			break
		}
		nameEnd := off + 8 + int(nameLen)
		if child != 0 && nameEnd <= len(data) {
			entries = append(entries, ext4DirEntry{
				offset:   off,
				child:    child,
				recLen:   recLen,
				nameLen:  nameLen,
				fileType: fileType,
				name:     string(data[off+8 : nameEnd]),
			})
		}
		off += int(recLen)
	}
	return entries, nil
}

func (e *ext4Image) resolve(path string) (uint32, error) {
	ino := uint32(2)
	for _, part := range splitExt4Path(path) {
		entries, err := e.listDir(ino)
		if err != nil {
			return 0, err
		}
		found := false
		for _, entry := range entries {
			if entry.name == part {
				ino = entry.child
				found = true
				break
			}
		}
		if !found {
			return 0, fmt.Errorf("path not found: %s", path)
		}
	}
	return ino, nil
}

func (e *ext4Image) upsertFile(path string, content []byte, mode uint16) error {
	if ino, err := e.resolve(path); err == nil {
		return e.writeExistingFile(ino, path, content, mode)
	}
	return e.injectFile(path, content, mode)
}

func (e *ext4Image) writeExistingFile(ino uint32, path string, content []byte, mode uint16) error {
	inode, err := e.readInode(ino)
	if err != nil {
		return err
	}
	blocks, err := e.inodeBlocks(inode)
	if err != nil {
		return err
	}
	capacity := len(blocks) * int(e.blockSize)
	if len(content) > capacity {
		return fmt.Errorf("%s needs %d bytes, but only %d bytes are allocated", path, len(content), capacity)
	}

	padded := make([]byte, capacity)
	copy(padded, content)
	for index, block := range blocks {
		start := index * int(e.blockSize)
		end := start + int(e.blockSize)
		if err := e.writeBlock(block, padded[start:end]); err != nil {
			return err
		}
	}

	now := uint32(time.Now().Unix())
	putLE16(inode, 0, mode)
	putLE32(inode, 4, uint32(len(content)))
	putLE32(inode, 12, now)
	putLE32(inode, 16, now)
	if len(inode) >= 112 {
		putLE32(inode, 108, 0)
	}
	return e.writeInode(ino, inode)
}

func (e *ext4Image) injectFile(path string, content []byte, mode uint16) error {
	parts := splitExt4Path(path)
	if len(parts) == 0 {
		return fmt.Errorf("cannot replace filesystem root")
	}
	parentPath := "/"
	if len(parts) > 1 {
		parentPath = "/" + strings.Join(parts[:len(parts)-1], "/")
	}
	name := parts[len(parts)-1]

	parentIno, err := e.resolve(parentPath)
	if err != nil {
		return err
	}
	entries, err := e.listDir(parentIno)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if entry.name == name {
			return fmt.Errorf("%s already exists", path)
		}
	}

	preferredGroup := uint64((parentIno - 1) / e.inodesPerGroup)
	ino, err := e.allocateInode(preferredGroup)
	if err != nil {
		return err
	}
	blockCount := uint64(math.Ceil(float64(len(content)) / float64(e.blockSize)))
	if blockCount == 0 {
		blockCount = 1
	}
	startBlock, err := e.allocateContiguousBlocks(preferredGroup, blockCount)
	if err != nil {
		return err
	}
	if err := e.createRegularFileExtents(ino, startBlock, blockCount, content, mode); err != nil {
		return err
	}
	return e.addDirEntryAnyBlock(parentIno, ino, name, 1)
}

func (e *ext4Image) allocateInode(preferredGroup uint64) (uint32, error) {
	for _, group := range e.groupSearchOrder(preferredGroup) {
		_, inodeBitmap, _, err := e.groupDesc(group)
		if err != nil {
			return 0, err
		}
		bitmap, err := e.readBlock(inodeBitmap)
		if err != nil {
			return 0, err
		}
		for idx := uint32(0); idx < e.inodesPerGroup; idx++ {
			ino := uint32(group)*e.inodesPerGroup + idx + 1
			if ino < 11 {
				continue
			}
			if !bitmapIsSet(bitmap, idx) {
				bitmapSet(bitmap, idx)
				if err := e.writeBlock(inodeBitmap, bitmap); err != nil {
					return 0, err
				}
				return ino, nil
			}
		}
	}
	return 0, fmt.Errorf("no free inode found")
}

func (e *ext4Image) allocateContiguousBlocks(preferredGroup, count uint64) (uint64, error) {
	for _, group := range e.groupSearchOrder(preferredGroup) {
		blockBitmap, _, _, err := e.groupDesc(group)
		if err != nil {
			return 0, err
		}
		bitmap, err := e.readBlock(blockBitmap)
		if err != nil {
			return 0, err
		}
		groupStart := e.firstDataBlock + group*uint64(e.blocksPerGroup)
		groupEnd := groupStart + uint64(e.blocksPerGroup)
		if groupEnd > e.blocksCount {
			groupEnd = e.blocksCount
		}
		maxIndex := groupEnd - groupStart
		runStart := uint64(0)
		runLen := uint64(0)
		for idx := uint64(0); idx < maxIndex; idx++ {
			if bitmapIsSet(bitmap, uint32(idx)) {
				runLen = 0
				continue
			}
			if runLen == 0 {
				runStart = idx
			}
			runLen++
			if runLen == count {
				for bit := runStart; bit < runStart+count; bit++ {
					bitmapSet(bitmap, uint32(bit))
				}
				if err := e.writeBlock(blockBitmap, bitmap); err != nil {
					return 0, err
				}
				return groupStart + runStart, nil
			}
		}
	}
	return 0, fmt.Errorf("no contiguous free run of %d blocks found", count)
}

func (e *ext4Image) createRegularFileExtents(ino uint32, startBlock, blockCount uint64, content []byte, mode uint16) error {
	if blockCount > 0x7FFF {
		return fmt.Errorf("file too large for a single direct extent")
	}

	now := uint32(time.Now().Unix())
	inode := make([]byte, int(e.inodeSize))
	putLE16(inode, 0, mode)
	putLE16(inode, 2, 0)
	putLE32(inode, 4, uint32(len(content)))
	putLE32(inode, 8, now)
	putLE32(inode, 12, now)
	putLE32(inode, 16, now)
	putLE32(inode, 20, 0)
	putLE16(inode, 24, 0)
	putLE16(inode, 26, 1)
	putLE32(inode, 28, uint32(blockCount*(e.blockSize/512)))
	putLE32(inode, 32, 0x80000)
	putLE16(inode, 40, 0xF30A)
	putLE16(inode, 42, 1)
	putLE16(inode, 44, 4)
	putLE16(inode, 46, 0)
	putLE32(inode, 48, 0)
	putLE32(inode, 52, 0)
	putLE16(inode, 56, uint16(blockCount))
	putLE16(inode, 58, uint16((startBlock>>32)&0xFFFF))
	putLE32(inode, 60, uint32(startBlock&0xFFFFFFFF))

	if err := e.writeInode(ino, inode); err != nil {
		return err
	}

	paddedLen := blockCount * e.blockSize
	if paddedLen > uint64(int(^uint(0)>>1)) {
		return fmt.Errorf("file too large to stage")
	}
	padded := make([]byte, int(paddedLen))
	copy(padded, content)
	for index := uint64(0); index < blockCount; index++ {
		start := index * e.blockSize
		end := start + e.blockSize
		if err := e.writeBlock(startBlock+index, padded[int(start):int(end)]); err != nil {
			return err
		}
	}
	return nil
}

func (e *ext4Image) addDirEntryAnyBlock(dirIno, childIno uint32, name string, fileType uint8) error {
	dirInode, err := e.readInode(dirIno)
	if err != nil {
		return err
	}
	blocks, err := e.inodeBlocks(dirInode)
	if err != nil {
		return err
	}
	nameBytes := []byte(name)
	needed := align4(8 + len(nameBytes))

	for _, block := range blocks {
		data, err := e.readBlock(block)
		if err != nil {
			return err
		}
		for off := 0; off+8 <= int(e.blockSize); {
			recLen := le16(data, off+4)
			nameLen := data[off+6]
			if recLen < 8 || off+int(recLen) > int(e.blockSize) {
				break
			}
			minLen := align4(8 + int(nameLen))
			slack := int(recLen) - minLen
			if le32(data, off) != 0 && slack >= needed {
				newOff := off + minLen
				putLE16(data, off+4, uint16(minLen))
				putLE32(data, newOff, childIno)
				putLE16(data, newOff+4, uint16(slack))
				data[newOff+6] = byte(len(nameBytes))
				data[newOff+7] = fileType
				copy(data[newOff+8:newOff+8+len(nameBytes)], nameBytes)
				if err := e.writeBlock(block, data); err != nil {
					return err
				}

				now := uint32(time.Now().Unix())
				putLE32(dirInode, 12, now)
				putLE32(dirInode, 16, now)
				return e.writeInode(dirIno, dirInode)
			}
			off += int(recLen)
		}
	}
	return fmt.Errorf("no directory slack large enough for %q", name)
}

func (e *ext4Image) updateChksumList(systemPath string, content []byte) error {
	ino, err := e.resolve("/chksum_list")
	if err != nil {
		return err
	}
	currentBytes, err := e.readFile(ino)
	if err != nil {
		return err
	}
	sum := md5.Sum(content)
	replacement := fmt.Sprintf("%x  %s", sum, systemPath)
	targetSuffix := "  " + systemPath
	lines := strings.Split(strings.ReplaceAll(string(currentBytes), "\r\n", "\n"), "\n")
	replaced := false
	for i, line := range lines {
		if strings.HasSuffix(line, targetSuffix) {
			lines[i] = replacement
			replaced = true
			break
		}
	}
	if !replaced {
		if len(lines) > 0 && strings.TrimSpace(lines[len(lines)-1]) == "" {
			lines[len(lines)-1] = replacement
		} else {
			lines = append(lines, replacement)
		}
	}
	next := strings.Join(lines, "\n")
	if !strings.HasSuffix(next, "\n") {
		next += "\n"
	}
	return e.writeExistingFile(ino, "/chksum_list", []byte(next), 0o100644)
}

func (e *ext4Image) groupSearchOrder(preferred uint64) []uint64 {
	order := make([]uint64, 0, e.groups)
	for group := preferred; group < e.groups; group++ {
		order = append(order, group)
	}
	for group := uint64(0); group < preferred && group < e.groups; group++ {
		order = append(order, group)
	}
	return order
}

func splitExt4Path(path string) []string {
	parts := strings.Split(strings.Trim(path, "/"), "/")
	out := parts[:0]
	for _, part := range parts {
		if part != "" {
			out = append(out, part)
		}
	}
	return out
}

func align4(value int) int {
	return (value + 3) &^ 3
}

func bitmapIsSet(bitmap []byte, idx uint32) bool {
	return bitmap[idx/8]&(1<<(idx%8)) != 0
}

func bitmapSet(bitmap []byte, idx uint32) {
	bitmap[idx/8] |= 1 << (idx % 8)
}

func le16(data []byte, off int) uint16 {
	return binary.LittleEndian.Uint16(data[off:])
}

func le32(data []byte, off int) uint32 {
	return binary.LittleEndian.Uint32(data[off:])
}

func putLE16(data []byte, off int, value uint16) {
	binary.LittleEndian.PutUint16(data[off:], value)
}

func putLE32(data []byte, off int, value uint32) {
	binary.LittleEndian.PutUint32(data[off:], value)
}
