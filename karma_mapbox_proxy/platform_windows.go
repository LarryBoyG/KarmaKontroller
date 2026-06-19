//go:build windows

package main

import (
	"log"
	"os"
	"path/filepath"
)

var logFile *os.File

func configureLogging() {
	path := os.Getenv("KARMA_MAPBOX_LOG")
	if path == "" {
		path = filepath.Join(karmaKontrollerDir(), "KarmaKontroller.log")
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
	defer func() {
		if logFile != nil {
			_ = logFile.Close()
		}
	}()
	return launchPatchWindow()
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
