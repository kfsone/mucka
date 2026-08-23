# Mucka.Input — the command box's sandbox

This project exists because the command-input box has been broken three times, by three different
well-meant changes, each made by someone who did not realise they were standing in the typing path:

1. a TwoWay binding round-trip, so a type-then-Enter faster than ~10 ms stranded the last character;
2. a clear that depended on a PropertyChanged chain, which raises nothing when the value is
   unchanged — so an empty read left typed text behind to be prepended to the next command;
3. a `tb.UpdateLayout()` inside `SelectionChanged`, i.e. a forced synchronous layout pass on every
   keystroke, which reordered when a character landed in `TextBox.Text` relative to the next key
   event and put `nne` on the wire when the owner typed `n`⏎`ne`⏎.
4. history recall driven off `PropertyChanged`, which `Set` does not raise for an unchanged value — so
   recalling an entry equal to the view model's copy moved the history index but never reached the
   box, and Up appeared to skip a line.

Each was individually reasonable-looking. Comments did not prevent any of them. So the rules are now
**mechanical**.

Note that (2) and (4) are the *same* mistake a year apart in different clothes: treating "has this
value changed?" as a proxy for "does the box need updating?". They are not the same question. The
framework therefore never compares — `RequestSetText` and `RequestClear` deliver unconditionally, and
`CommandInputTests` pins that.

## The wall

`IInputSurface` is four members long and is the *entire* vocabulary available for touching the real
control. Everything else in this project is platform-free, which means:

- App code on the far side **cannot reach a `TextBox`** — not "should not", *cannot*. There is no
  `using` that gets you there from here.
- Every rule below is unit-testable off-device, and is tested (`CommandInputTests`). They used to be
  discoverable only by the owner, live, at 120 wpm.

Same mechanism, same reason, as `mudsharp` (pure protocol, no MAUI) and `Mucka.Terminal`
(presentation logic, no MAUI). Assembly boundaries are how this codebase already enforces
architecture.

## The rules

| Rule | Enforced by |
|---|---|
| The box captures and enqueues. It does not interpret. | `CommandInput` has no parsing surface; `InputGate.LineReady` is the only exit |
| Nothing a consumer writes runs on a keystroke | `HotkeyRouter.Bind` dispatches via the gate; `BindImmediate` is the greppable exception |
| Enter empties the box *before* handing the line off | `CommandInput.AcceptLine`, in that order, unconditionally |
| Everything the player initiates keeps its order | one FIFO in `InputGate` — typed lines *and* hotkeys |
| Writes into the box cannot interleave with typing | `RequestSetText`/`RequestClear`/`RequestFocus` queue behind pending input |
| Work on the keystroke is measured, not assumed | `InputPathBudget`, always compiled in, 1 ms budget |
| Two features cannot silently fight over one key | duplicate `Bind` throws at startup |
| What the box shows is **asserted**, never inferred from a value having changed | `RequestSetText`/`RequestClear` compare nothing and always deliver |
| One bad consumer cannot strand the queue | per-item try/catch in the drain, `Faulted` event |

## What does *not* belong here

Autocomplete, expansion, history, command parsing, anything that inspects a line as it is typed. The
owner's standing position: the box has one job — capture and enqueue user input correctly and
smoothly. All of those live on the far side of `InputGate`, where they can take as long as they like.

If a change appears to need a fifth method on `IInputSurface`, that is the signal to ask whether the
feature belongs in the input path at all. So far the answer has always been no.
