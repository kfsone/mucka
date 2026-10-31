# Mucka privacy policy

Mucka is a client for the text game MUD2. This page is the privacy policy the app stores link to,
and it is also a rule the code answers to: a change that makes any sentence below false changes this
page in the same commit.

## What Mucka collects

Nothing. Mucka has no analytics, no advertising, no crash reporting and no account with us. The only
network connections it makes are to the game server you choose, over telnet or SSH.

## What stays on your device

Everything Mucka keeps, it keeps on the device it runs on:

- **Settings** - your profiles (each one's name, server, port and MUD account ID), your function
  keys, and display and sound choices. On Windows and macOS this is `~/.mucka/mucka.ini`; on a
  phone or tablet it is inside the app's own storage.
- **Passwords** - only when you tick Remember Password, and then in the system's secure store: the
  keychain on iOS and macOS, Android's keystore-encrypted storage, or Windows data protection. On
  macOS you can choose plain text instead, which keeps them in `mucka.ini`.
- **A session database** (`mucka.db`, beside the settings) - a record of every session: what the
  server sent, what Mucka sent, and when. What Mucka sent includes what you type, and that includes
  a password typed or sent at a login prompt. The database also keeps your score history and your
  fight records, built from those sessions.
- **Replays** - on Windows and macOS, a session you save with `$MAKEREPLAY`, in `~/.mucka/replays`:
  what the server sent, with your account ID and password replaced by stand-ins. Nothing you typed is in it,
  but what the server sent back is - your character's name, and other players' names and words.
  Watching a replay records nothing in the session database.

None of it leaves the device unless you copy or share it yourself.

## The game server

What you type is sent to the server you connect to, which keeps its own records under its own
terms. A telnet connection is not encrypted; an SSH connection is.

## Removing it

Uninstalling the app on a phone or tablet removes its storage. On a desktop, delete the `~/.mucka`
folder. A password saved in the system's secure store can outlive the app; Mucka removes one when
you delete its profile or untick Remember Password.

## Contact

Questions go to the project's issue tracker: https://github.com/kfsone/mucka/issues
