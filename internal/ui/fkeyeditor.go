package ui

import (
	"fmt"
	"image/color"
	"log"

	"gioui.org/app"
	"gioui.org/font"
	"gioui.org/io/system"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/text"
	"gioui.org/unit"
	"gioui.org/widget"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/config"
)

// FKeyEditor manages the state for the F-key binding editor window.
type FKeyEditor struct {
	editors   [3][12]widget.Editor
	tabs      [3]widget.Clickable
	btnSave   widget.Clickable
	btnApply  widget.Clickable
	btnCancel widget.Clickable
	activeTab int
	list      layout.List
	win       *app.Window
}

var tabNames = [3]string{"None", "Shift", "Ctrl"}

// OpenFKeyEditor opens a new GUI window for editing F1-F12 key bindings.
// It runs the event loop in a new goroutine.
// onApply is called when the user clicks Apply or Save (before saving).
// onSave is called when the user clicks Save (returns error on failure).
// onClose is called when the window is destroyed.
func OpenFKeyEditor(
	fonts []font.FontFace,
	initial config.FKeyConfig,
	onApply func(config.FKeyConfig),
	onSave func(config.FKeyConfig) error,
	onClose func(),
) {
	go func() {
		ed := &FKeyEditor{}
		ed.list.Axis = layout.Vertical

		// Initialise all 36 editors from initial config.
		for tab := 0; tab < 3; tab++ {
			set := initial.SetByIndex(tab)
			for i := 0; i < 12; i++ {
				ed.editors[tab][i].SingleLine = true
				ed.editors[tab][i].SetText(set.Get(i + 1))
			}
		}

		win := new(app.Window)
		win.Option(
			app.Title("F-Key Bindings"),
			app.Size(unit.Dp(500), unit.Dp(480)),
		)
		ed.win = win

		th := material.NewTheme()
		th.Shaper = text.NewShaper(text.WithCollection(fonts))

		var ops op.Ops
		for {
			switch e := win.Event().(type) {
			case app.DestroyEvent:
				onClose()
				return
			case app.FrameEvent:
				ops.Reset()
				gtx := app.NewContext(&ops, e)
				ed.layoutWindow(gtx, th, onApply, onSave)
				e.Frame(gtx.Ops)
			}
		}
	}()
}

// currentConfig reads all editor fields and returns an FKeyConfig.
func (ed *FKeyEditor) currentConfig() config.FKeyConfig {
	var cfg config.FKeyConfig
	for tab := 0; tab < 3; tab++ {
		set := cfg.SetByIndex(tab)
		for i := 0; i < 12; i++ {
			set.Set(i+1, ed.editors[tab][i].Text())
		}
	}
	return cfg
}

// layoutWindow renders the editor window contents.
func (ed *FKeyEditor) layoutWindow(
	gtx layout.Context,
	th *material.Theme,
	onApply func(config.FKeyConfig),
	onSave func(config.FKeyConfig) error,
) layout.Dimensions {
	// Handle tab clicks.
	for i := 0; i < 3; i++ {
		if ed.tabs[i].Clicked(gtx) {
			ed.activeTab = i
		}
	}

	// Handle action buttons.
	if ed.btnApply.Clicked(gtx) {
		onApply(ed.currentConfig())
	}
	if ed.btnSave.Clicked(gtx) {
		cfg := ed.currentConfig()
		onApply(cfg)
		if err := onSave(cfg); err != nil {
			log.Printf("fkeys: save error: %v", err)
		}
	}
	if ed.btnCancel.Clicked(gtx) {
		ed.win.Perform(system.ActionClose)
	}

	// Draw background.
	bg := color.NRGBA{R: 0x1E, G: 0x1E, B: 0x1E, A: 255}
	paint.FillShape(gtx.Ops, bg, clip.Rect{Max: gtx.Constraints.Max}.Op())

	return layout.UniformInset(unit.Dp(8)).Layout(gtx, func(gtx layout.Context) layout.Dimensions {
		return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
			// Tab row.
			layout.Rigid(func(gtx layout.Context) layout.Dimensions {
				return ed.layoutTabs(gtx, th)
			}),
			layout.Rigid(layout.Spacer{Height: unit.Dp(6)}.Layout),
			// F-key editor list (scrollable, takes remaining space).
			layout.Flexed(1, func(gtx layout.Context) layout.Dimensions {
				return ed.list.Layout(gtx, 12, func(gtx layout.Context, i int) layout.Dimensions {
					return ed.layoutRow(gtx, th, i)
				})
			}),
			layout.Rigid(layout.Spacer{Height: unit.Dp(6)}.Layout),
			// Action buttons row.
			layout.Rigid(func(gtx layout.Context) layout.Dimensions {
				return ed.layoutButtons(gtx, th)
			}),
		)
	})
}

// layoutTabs renders the three modifier tab buttons.
func (ed *FKeyEditor) layoutTabs(gtx layout.Context, th *material.Theme) layout.Dimensions {
	children := make([]layout.FlexChild, 3)
	for i := 0; i < 3; i++ {
		i := i
		children[i] = layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			btn := material.Button(th, &ed.tabs[i], tabNames[i])
			if ed.activeTab == i {
				btn.Background = color.NRGBA{R: 0x00, G: 0x78, B: 0xD7, A: 255}
			} else {
				btn.Background = color.NRGBA{R: 0x3C, G: 0x3C, B: 0x3C, A: 255}
			}
			return layout.UniformInset(unit.Dp(2)).Layout(gtx, btn.Layout)
		})
	}
	return layout.Flex{Axis: layout.Horizontal}.Layout(gtx, children...)
}

// layoutRow renders a single F-key label + editor row.
func (ed *FKeyEditor) layoutRow(gtx layout.Context, th *material.Theme, i int) layout.Dimensions {
	label := fkeyLabel(i + 1)
	return layout.UniformInset(unit.Dp(2)).Layout(gtx, func(gtx layout.Context) layout.Dimensions {
		return layout.Flex{Axis: layout.Horizontal, Alignment: layout.Middle}.Layout(gtx,
			layout.Rigid(func(gtx layout.Context) layout.Dimensions {
				gtx.Constraints.Min.X = gtx.Dp(unit.Dp(40))
				gtx.Constraints.Max.X = gtx.Dp(unit.Dp(40))
				lbl := material.Label(th, defaultFontSize, label)
				lbl.Color = color.NRGBA{R: 0xCC, G: 0xFF, B: 0xCC, A: 255}
				return lbl.Layout(gtx)
			}),
			layout.Flexed(1, func(gtx layout.Context) layout.Dimensions {
				edStyle := material.Editor(th, &ed.editors[ed.activeTab][i], "")
				edStyle.Color = color.NRGBA{R: 220, G: 220, B: 220, A: 255}
				return layout.UniformInset(unit.Dp(2)).Layout(gtx, edStyle.Layout)
			}),
		)
	})
}

// layoutButtons renders the Save/Apply/Cancel buttons.
func (ed *FKeyEditor) layoutButtons(gtx layout.Context, th *material.Theme) layout.Dimensions {
	return layout.Flex{Axis: layout.Horizontal}.Layout(gtx,
		layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			return layout.UniformInset(unit.Dp(2)).Layout(gtx,
				material.Button(th, &ed.btnSave, "Save").Layout)
		}),
		layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			return layout.UniformInset(unit.Dp(2)).Layout(gtx,
				material.Button(th, &ed.btnApply, "Apply").Layout)
		}),
		layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			return layout.UniformInset(unit.Dp(2)).Layout(gtx,
				material.Button(th, &ed.btnCancel, "Cancel").Layout)
		}),
	)
}

// fkeyLabel returns a label string like "F1:" for index 1-12.
func fkeyLabel(n int) string {
	if n < 1 || n > 12 {
		return ""
	}
	return fmt.Sprintf("F%d:", n)
}
