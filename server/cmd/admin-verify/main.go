// admin-verify — verifies bcrypt hash vs password. Debug helper.
// Usage: admin-verify <hash> <password>
package main

import (
	"fmt"
	"os"

	"zconect/server/internal/admin"
)

func main() {
	if len(os.Args) != 3 {
		fmt.Fprintln(os.Stderr, "Usage: admin-verify <hash> <password>")
		os.Exit(2)
	}
	hash := os.Args[1]
	pass := os.Args[2]
	if admin.CheckPassword(pass, hash) {
		fmt.Println("✓ MATCH")
		os.Exit(0)
	}
	fmt.Println("✗ NO MATCH")
	os.Exit(1)
}
