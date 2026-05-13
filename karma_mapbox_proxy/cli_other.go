//go:build !windows

package main

func runCLI(args []string) (bool, error) {
	return false, nil
}
