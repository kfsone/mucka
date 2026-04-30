package core

// NopInvalidator is an Invalidator whose Invalidate method does nothing.
// Use it in tests or headless contexts where no UI redraw is needed.
type NopInvalidator struct{}

func (n *NopInvalidator) Invalidate() {}
