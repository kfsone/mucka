// Command mucka is a lightweight MUD client with an ANSI-aware text window.
package main

import (
	"flag"
	"fmt"
	"log"
	"os"

	"gioui.org/app"
	"gioui.org/op"
	"gioui.org/text"
	"gioui.org/unit"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/commands"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/headless"
	"github.com/kfsone/mucka/internal/ui"
	"github.com/kfsone/mucka/internal/version"
)

func main() {
	headlessFlag := flag.Bool("headless", false, "run without GUI, stdin/stdout mode")
	profileFlag := flag.String("profile", "", "auto-connect to this server profile")
	scriptFlag := flag.String("script", "", "script file to execute (headless mode)")
	flag.Parse()

	if *headlessFlag {
		cfg, _ := config.Load()
		os.Exit(func() int {
			if err := headless.Run(cfg, *profileFlag, *scriptFlag); err != nil {
				fmt.Fprintln(os.Stderr, err)
				return 1
			}
			return 0
		}())
	}

	go func() {
		if err := run(); err != nil {
			log.Println(err)
			os.Exit(1)
		}
		os.Exit(0)
	}()
	app.Main()
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		log.Printf("config: %v (using defaults)", err)
		cfg = config.Default()
	}

	w := new(app.Window)
	w.Option(
		app.Title(version.AppName+" v"+version.Version),
		app.Size(unit.Dp(900), unit.Dp(600)),
	)

	th := material.NewTheme()
	fonts := ui.LoadFontCollection(cfg.General.FontName)
	th.Shaper = text.NewShaper(text.WithCollection(fonts))

	u := ui.New()
	u.SetFont(cfg.General.FontName)
	u.SetFontSize(unit.Sp(cfg.General.FontSize))
	u.TextPanel.AppendText("\x1b[1;32m" + version.AppName + " v" + version.Version + "\x1b[0m — type .help for commands")

	_ = commands.NewDispatcher(w, u, cfg, fonts)

	var ops op.Ops
	for {
		switch e := w.Event().(type) {
		case app.DestroyEvent:
			return e.Err
		case app.FrameEvent:
			ops.Reset()
			gtx := app.NewContext(&ops, e)
			u.Layout(gtx, th)
			e.Frame(gtx.Ops)
		}
	}
}
