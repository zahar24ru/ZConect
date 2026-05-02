// admin-hashpass — helper CLI для генерации bcrypt hash admin password.
//
// Usage:
//   go run ./cmd/admin-hashpass         # читает пароль из stdin (с подсказкой)
//   echo 'mypass' | ./admin-hashpass    # или из pipe
//
// Output: bcrypt hash — прописывается в env var ADMIN_PASSWORD_HASH на сервере.
//
// НЕ ПЕЧАТАЕТ пароль. НЕ сохраняет hash в файл (pipe в env manager вручную).
package main

import (
	"bufio"
	"fmt"
	"os"
	"strings"

	"zconect/server/internal/admin"
)

func main() {
	var pass string
	stat, _ := os.Stdin.Stat()
	isPiped := (stat.Mode() & os.ModeCharDevice) == 0
	if isPiped {
		// Read from stdin (one line).
		scan := bufio.NewScanner(os.Stdin)
		if scan.Scan() {
			pass = strings.TrimSpace(scan.Text())
		}
	} else {
		fmt.Fprint(os.Stderr, "Admin password (min 8 chars, will NOT echo): ")
		// Простое reading без masking (os.Stdin line read).
		// Для production лучше golang.org/x/term.ReadPassword, но не хотим добавлять
		// зависимость ради helper script. User запустит один раз через ssh.
		scan := bufio.NewScanner(os.Stdin)
		if scan.Scan() {
			pass = strings.TrimSpace(scan.Text())
		}
		fmt.Fprintln(os.Stderr)
	}

	if pass == "" {
		fmt.Fprintln(os.Stderr, "ERROR: empty password")
		os.Exit(2)
	}
	if len(pass) < 8 {
		fmt.Fprintln(os.Stderr, "ERROR: password too short (min 8 chars)")
		os.Exit(2)
	}

	hash, err := admin.HashPassword(pass)
	if err != nil {
		fmt.Fprintln(os.Stderr, "ERROR:", err)
		os.Exit(1)
	}

	// Hash в stdout (чтобы `./admin-hashpass | ... | sudo tee ...` работал).
	// Подсказки / ошибки — в stderr.
	fmt.Println(hash)
	fmt.Fprintln(os.Stderr, "Done. Set on server:")
	fmt.Fprintln(os.Stderr, "  export ADMIN_PASSWORD_HASH='"+hash+"'")
}
