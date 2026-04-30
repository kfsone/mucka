// Package commands parses user input into typed, named commands with arguments.
package commands

import (
	"sort"
	"strings"
)

// CommandType distinguishes command kinds by their name prefix.
type CommandType int

const (
	// Plain is an unadorned word or sentence sent to the MUD server.
	Plain CommandType = iota
	// Dot commands start with '.' (e.g. ".quit").
	Dot
	// Dollar commands start with '$' (e.g. "$stream foo.txt").
	Dollar
)

// Command is the result of tokenising a single line of user input.
type Command struct {
	Type CommandType
	Name string   // first whitespace-delimited token (including any prefix)
	Args []string // remaining tokens
}

// Tokenise splits input on whitespace, derives the CommandType from the
// first token's leading character, and returns a Command.
// Empty or all-whitespace input returns a Plain command with an empty Name.
func Tokenise(input string) Command {
	parts := strings.Fields(input)
	if len(parts) == 0 {
		return Command{Type: Plain}
	}

	name := parts[0]
	args := parts[1:]

	var cmdType CommandType
	switch {
	case strings.HasPrefix(name, "$"):
		cmdType = Dollar
	case strings.HasPrefix(name, "."):
		cmdType = Dot
	default:
		cmdType = Plain
	}

	return Command{
		Type: cmdType,
		Name: name,
		Args: args,
	}
}

// HandlerFunc processes the arguments of a command.
type HandlerFunc func(args []string)

type entry struct {
	fn   HandlerFunc
	desc string
}

// Registry holds named command handlers.
type Registry struct {
	handlers map[string]entry
}

// NewRegistry returns an empty Registry.
func NewRegistry() *Registry {
	return &Registry{handlers: make(map[string]entry)}
}

// Register adds a handler with a description for the given command name (including any prefix).
func (r *Registry) Register(name, desc string, fn HandlerFunc) {
	r.handlers[name] = entry{fn: fn, desc: desc}
}

// Dispatch looks up and calls the handler for cmd.Name.
// Returns true if a handler was found, false otherwise.
func (r *Registry) Dispatch(cmd Command) bool {
	e, ok := r.handlers[cmd.Name]
	if ok {
		e.fn(cmd.Args)
	}
	return ok
}

// Entries returns all registered commands sorted by name.
func (r *Registry) Entries() []struct{ Name, Desc string } {
	result := make([]struct{ Name, Desc string }, 0, len(r.handlers))
	for name, e := range r.handlers {
		result = append(result, struct{ Name, Desc string }{name, e.desc})
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Name < result[j].Name })
	return result
}
