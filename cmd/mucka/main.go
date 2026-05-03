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
	stdioFlag := flag.Bool("stdio", false, "plain-text LLM-harness mode (stdin/stdout, human-readable)")
	profileFlag := flag.String("profile", "", "auto-connect to this server profile")
	scriptFlag := flag.String("script", "", "script file to execute (headless mode)")
	flag.Parse()

	// Resolve initial profile: flag wins over positional arg.
	positionalProfile := ""
	if args := flag.Args(); len(args) > 0 {
		positionalProfile = args[0]
	}
	initialProfile := *profileFlag
	if initialProfile == "" {
		initialProfile = positionalProfile
	}

	if *headlessFlag {
		cfg, _ := config.Load()
		os.Exit(func() int {
			if err := headless.Run(cfg, initialProfile, *scriptFlag); err != nil {
				fmt.Fprintln(os.Stderr, err)
				return 1
			}
			return 0
		}())
	}

	if *stdioFlag {
		cfg, _ := config.Load()
		os.Exit(func() int {
			if err := headless.RunStdio(cfg, initialProfile, *scriptFlag); err != nil {
				fmt.Fprintln(os.Stderr, err)
				return 1
			}
			return 0
		}())
	}

	go func() {
		if err := run(initialProfile); err != nil {
			log.Println(err)
			os.Exit(1)
		}
		os.Exit(0)
	}()
	app.Main()
}

func run(initialProfile string) error {
	cfg, err := config.Load()
	if err != nil {
		log.Printf("config: %v (using defaults)", err)
		cfg = config.Default()
	}

	w := new(app.Window)
	w.Option(
		app.Title(version.AppName+" "+version.String()),
		app.Size(unit.Dp(900), unit.Dp(600)),
	)

	th := material.NewTheme()
	fonts := ui.LoadFontCollection(cfg.General.FontName)
	th.Shaper = text.NewShaper(text.WithCollection(fonts))

	u := ui.New()
	u.SetFont(cfg.General.FontName)
	u.SetFontSize(unit.Sp(cfg.General.FontSize))
	u.InputLine.SetHistoryLimit(cfg.General.History)
	u.TextPanel.SetMaxLines(cfg.General.Scrollback)
	u.TextPanel.AppendText("\x1b[1;32m" + version.AppName + " " + version.String() + "\x1b[0m — type .help for commands")

	_ = commands.NewDispatcher(w, u, cfg, fonts, initialProfile)

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
