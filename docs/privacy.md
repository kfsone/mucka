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
- **Replays** - a session you save with `$MAKEREPLAY` (Windows and macOS) or with Save as Replay in
  History (every platform), in your replays folder (`~/.mucka/replays` on a desktop): what the
  server sent, with your account ID replaced by a stand-in where the game prints it (an ID you say
  in the game yourself stays). Nothing you typed is in it, your password included, but what the
  server sent back is - your character's name, and other players' names and words.
  Watching a replay records nothing in the session database.
- **Text logs** - a session you save as a Text log, Simple Color Log or Fancy Color Log in History
  (Windows and macOS), wherever you choose: the screen from entering the game to leaving it, as
  plain text (a Color Log adds the screen's colours as terminal colour codes: Simple uses only the
  terminal's 16 colours, Fancy uses Mucka's own exact colours; both leave your terminal's default
  text colour and background alone), with your account ID
  replaced where the game prints it, as in a replay. It holds what the screen showed, so it holds
  your commands as the game echoed them, and other players' names and words.

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
