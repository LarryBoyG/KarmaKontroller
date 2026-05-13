//go:build !windows

package main

import (
	"errors"
	"log"
	"net"
	"net/http"
)

func configureLogging() {}

func runPlatform() error {
	server, addr, err := newProxyServer()
	if err != nil {
		return err
	}

	ln, err := net.Listen("tcp4", addr)
	if err != nil {
		return err
	}
	log.Printf("listening on %s", addr)
	if err := server.ServeTLS(ln, "", ""); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}
