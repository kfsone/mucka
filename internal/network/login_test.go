package network

import (
	"testing"

	"github.com/kfsone/mucka/internal/config"
)

func TestLoginSequence(t *testing.T) {
	c := newTestConn()
	profile := config.ServerProfile{
		Login:    "mylogin",
		Account:  "myaccount",
		Password: "mypassword",
	}

	// Step 1: WaitLogin → sees "login: " → sends Login, moves to WaitAccount.
	state := c.runLoginAutomaton(stateWaitLogin, "Please enter your login: ", profile)
	if state != stateWaitAccount {
		t.Errorf("after login prompt: want stateWaitAccount, got %v", state)
	}
	if sent := drain(c); sent != "mylogin\r\n" {
		t.Errorf("login send: got %q, want %q", sent, "mylogin\r\n")
	}

	// Step 2: WaitAccount → sees "Account ID: " → sends Account, moves to WaitPassword.
	state = c.runLoginAutomaton(state, "Account ID: ", profile)
	if state != stateWaitPassword {
		t.Errorf("after account prompt: want stateWaitPassword, got %v", state)
	}
	if sent := drain(c); sent != "myaccount\r\n" {
		t.Errorf("account send: got %q, want %q", sent, "myaccount\r\n")
	}

	// Step 3: WaitPassword → sees "Password:" → sends Password, moves to Done.
	state = c.runLoginAutomaton(state, "Password:", profile)
	if state != stateDone {
		t.Errorf("after password prompt: want stateDone, got %v", state)
	}
	if sent := drain(c); sent != "mypassword\r\n" {
		t.Errorf("password send: got %q, want %q", sent, "mypassword\r\n")
	}
}

// TestLoginPasswordVariant checks that "assword:" matches both "Password:" and "password:".
func TestLoginPasswordVariant(t *testing.T) {
	c := newTestConn()
	profile := config.ServerProfile{Password: "secret"}

	state := c.runLoginAutomaton(stateWaitPassword, "Enter password:", profile)
	if state != stateDone {
		t.Errorf("lower-case password prompt: want stateDone, got %v", state)
	}
	if sent := drain(c); sent != "secret\r\n" {
		t.Errorf("got %q", sent)
	}
}

// TestLoginNoRetrigger: once in stateDone, further "login: " prompts are ignored.
func TestLoginNoRetrigger(t *testing.T) {
	c := newTestConn()
	profile := config.ServerProfile{Login: "mylogin"}

	state := c.runLoginAutomaton(stateDone, "login: ", profile)
	if state != stateDone {
		t.Errorf("after Done: state should remain Done, got %v", state)
	}
	select {
	case got := <-c.sendCh:
		t.Errorf("expected no send after Done, got %q", got)
	default:
	}
}

// TestLoginNoPartialMatch: "login:" without trailing space should not trigger WaitLogin.
func TestLoginNoPartialMatch(t *testing.T) {
	c := newTestConn()
	profile := config.ServerProfile{Login: "mylogin"}

	state := c.runLoginAutomaton(stateWaitLogin, "login:", profile) // missing space
	if state != stateWaitLogin {
		t.Errorf("partial match should not advance state, got %v", state)
	}
	select {
	case got := <-c.sendCh:
		t.Errorf("unexpected send: %q", got)
	default:
	}
}

// TestLoginPromptBuriedMidString: trigger fires when prompt appears after other text.
func TestLoginPromptBuriedMidString(t *testing.T) {
	c := newTestConn()
	profile := config.ServerProfile{
		Login:    "miduser",
		Account:  "midacct",
		Password: "midpass",
	}

	// "login: " buried after leading text.
	state := c.runLoginAutomaton(stateWaitLogin, "Welcome to MUD2. Please enter your login: ", profile)
	if state != stateWaitAccount {
		t.Errorf("buried login prompt: want stateWaitAccount, got %v", state)
	}
	if sent := drain(c); sent != "miduser\r\n" {
		t.Errorf("buried login prompt send: got %q, want %q", sent, "miduser\r\n")
	}

	// "Account ID: " buried after leading text.
	state = c.runLoginAutomaton(state, "Welcome. Account ID: please", profile)
	if state != stateWaitPassword {
		t.Errorf("buried account prompt: want stateWaitPassword, got %v", state)
	}
	if sent := drain(c); sent != "midacct\r\n" {
		t.Errorf("buried account prompt send: got %q, want %q", sent, "midacct\r\n")
	}

	// "assword:" buried after leading text.
	state = c.runLoginAutomaton(state, "Enter your Password: now", profile)
	if state != stateDone {
		t.Errorf("buried password prompt: want stateDone, got %v", state)
	}
	if sent := drain(c); sent != "midpass\r\n" {
		t.Errorf("buried password prompt send: got %q, want %q", sent, "midpass\r\n")
	}
}

// drain reads one item from sendCh (non-blocking). Returns "" if nothing was sent.
func drain(c *Conn) string {
	select {
	case s := <-c.sendCh:
		return s
	default:
		return ""
	}
}
