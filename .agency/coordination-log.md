# Coordination Log — Mucka Unit Test Spec Pass

## Agents
- **Agent 1 (Clio Protocol Analyst / Principal Engineer):** Analysed `G:\Source\clio-1.8a\src\telnet.l`, `txfes.c`, `tinit.c`, `FES.md`
- **Agent 2 (Mucka Protocol Analyst / Reliability Engineer):** Analysed `G:\Source\mucka\Core\{MudStream,MudConnection,GameStats,StyledText,AutoLoginConfig,Profile}.cs`
- **Agent 3 (MAUI Platform Analyst / Maintenance Engineer):** Analysed `G:\Source\mucka\{Helpers/HtmlScrollback,Pages/GamePage.xaml.cs,ViewModels/GameViewModel,ViewModels/AnsiPalette}.cs`

## Status
- [x] Source recon complete (all 3 agents)
- [x] test-spec-protocol.md written
- [x] test-spec-muckacore.md written
- [x] test-spec-maui.md written
- [x] test-plan.md written (consolidated)
- [ ] Awaiting user/principal review of test-plan.md

## Key findings
- Mucka SE constant is 0xF0 but Clio uses 0xF0 too (SE=\\360 octal = 0xF0) — correct
- Mucka IAC IAC handling: silently consumes both bytes; Clio never expects literal 0xFF in text stream — consistent
- FES subscription bytes match exactly (ESC-[FES ESC-])
- Mucka WILL ECHO: sends DO (not WONT like Clio). This is a deviation — Clio sends TWONT for echo=1
- NEW-ENVIRON USER var is Mucka's login deviation vs Clio text-scanning of "login:"
- C1GenSeq catchall coverage: Mucka's ApplyC1Color only handles C00, C01, C03, C05, C07 — all other C1xx sequences silently set no colour
- FES field count guard: Mucka requires >= 15 fields; Clio uses strtok and tolerates partial
- DreamClearPrefix5 does NOT request FES — intentional, documented
