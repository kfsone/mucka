package mud2

// semanticTags maps MUD2 semantic type numbers (0–60) to their output tag
// strings. Type 0 means "default text" and has no tag. Unknown types return "".
var semanticTags = [61]string{
	0:  "",         // default text
	1:  "PROMPT",   // prompt
	2:  "",         // (reserved)
	3:  "ROOM-NAME", // room short description
	4:  "ROOM-DESC", // room long description
	5:  "FEATURES", // exits and features
	6:  "OBJECT",   // non-treasure objects
	7:  "TRINKET",  // trinkets
	8:  "TREASURE", // treasure
	9:  "CREATURE", // normal creatures
	10: "CREATURE", // wiz-level creatures
	11: "PLAYER",   // mortal players
	12: "WIZ",      // wizard players
	13: "SAY",      // speech
	14: "EMOTE",    // emote
	15: "TOLD",     // told
	16: "ACT",      // acted
	17: "SHOUT",    // shouted
	18: "SAY",      // additional speech type
	19: "FIGHT",    // fight notification
	20: "FIGHT",    // fight hit
	21: "FIGHT",    // fight end
	22: "FIGHT",    // fight kill
	23: "",         // (reserved)
	24: "SPELL",    // spells
	// 25–28: reserved/unknown
	29: "NOISE",   // distant noises
	30: "INFO",    // unsolicited information
	31: "WEATHER", // rain
	32: "WEATHER", // snow
	33: "WEATHER", // clouds
	// 34–60: reserved/unknown
}

// SemanticTag returns the output tag string for the given MUD2 semantic type
// number. Returns "" for type 0 (default text), out-of-range values, and
// types that have no assigned label.
func SemanticTag(typeNum int) string {
	if typeNum < 0 || typeNum >= len(semanticTags) {
		return ""
	}
	return semanticTags[typeNum]
}
