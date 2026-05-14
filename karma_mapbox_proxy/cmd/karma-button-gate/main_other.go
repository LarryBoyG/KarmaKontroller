//go:build !linux

package main

import "fmt"

func main() {
	fmt.Println("karma-button-gate is only built for the controller Linux target")
}
