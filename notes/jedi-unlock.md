# Jedi unlock: what the client data shows

Sources: decrypted client data (`beautified/`), plus the public wiki
(swgr.org/wiki/jedi_theory, /inciting_incident, /the_village, /jedi, read 2026-10-26).

## Big caveat: the decision logic is server-side

SWG clients don't carry gameplay scripts. Restoration's server rolls the per-character variables at
character creation (which inciting incident, which bartender, which shrine), and keeps them in
server objvars that are never sent to a normal client. **The client can't tell you *your* rolls.**
It gives you the complete map of stages, quest tasks, signals and text. Combined with
what the server shows you in-game (conversation options, `/checkForce`), that narrows down
what to test.

## The badge chain: `col_jedi_unlock` (`datatables/collection/collection.tsv` ~line 417)

In order, with badge ids:

| Badge | Id | Name (`collection_n`) |
|---|---|---|
| `bdg_jedi_unlock_universal_requirements` | 22798 | Universal Requisites (Phase I-A) |
| `bdg_jedi_unlock_inciting_incident` | 22799 | Inciting Incident (Phase I-B) |
| `bdg_jedi_unlock_ambient_event` | 22818 | **Ambient Event**, not on the wiki |
| `bdg_jedi_unlock_meditate_minigame` | 22800 | Learn to Meditate (Phase I-C) |
| `bdg_jedi_unlock_village` | 22801 | Village |
| `bdg_jedi_unlock_nosslyn` | 22802 | **Journey with Noslyn**, not on the wiki |
| `bdg_jedi_unlock_padawan_trials` | 22803 | Padawan Trials |
| `bdg_jedi_unlock_knight_trials` | 22804 | Knight Trials (not live) |

The ambient-event badge id (22818) is far higher than the rest, so it was added later.
`collection_d` also has `ambient_events_1..3` ("Has discovered the Ambient Event on Naboo / Yavin / Tatooine").
Whether these relate to the unlock is unconfirmed.

## Phase II: "Mending Mindfulness" (`quest/ground/jedi_unlock_phase_2`)

The task chain, from `datatables/questtask/quest/jedi_unlock_phase_2.tsv`:
0. timer 30s "Wait for Contact"
1. comm from the Mysterious Figure (appearance `costume_exar_kun_cultist`)
2. timer 10s, then 3. comm "I've sent you some challenges"
4–7. `wait_for_signal` tasks `trialOne`..`trialFour`, the four appraisals
8. comm `finalContact`

Riddle text (verbatim from client):
- **First:** "Of billowing clouds where lives are among the sky, this lighted world sits nearby. Find the base of a burgeon of trapezium basins and meditate near the dangerous edge. …"
- **Second:** "Singleton association was of preference for this egalitarian world disregarded by its infamous celebutante of origin. Where the trees see the zenith, meditating upon this apogee …"
- **Third:** "… be positive you can find the fourteen pieces needed to put this puzzle together. Meditate close, it doesn't really bite, but be careful not to enter …"
- **Final:** "Each Force Sensitive has a Force Shrine they are connected to more than any other … dark forces may come to disrupt those at shrines they do not belong to."

So shrines that aren't yours spawn hostiles. That's the in-game way to rule shrines out.

Bartender lines (`conversation/bartender` s_10/s_13/s_15): "Do you believe in the supernatural?" →
"I have this connection to the world around me…" → "I will reach out to a few friends…". The
conversation tree is server-driven, so the option only shows on *your* bartender.

## Journey with Noslyn (undocumented on the wiki)

`quest/ground/whisper_in_the_force` ("A Whisper In The Force", category Force Sensitive):
0. comm from an old man (`dressed_jedi_trainer_old_human_male_01`): "*You hear someone whispering to you in your head* ... Come find me..."
1. signal `foundNoslyn`: "Using the **Force Crystal Cluster** will help you focus and find Noslyn through the Force." (item `noslyn_quest_crystal`)
2. talk to NPC **Noslyn**
3. signal `outOfTime`: "You've got a bad feeling…"
4. defend Noslyn from **Patriots**; on fail (`noslynDeath`) → 5. "Run"

`noslyn_and_the_force_crystal_prologue`: wait `waitingForMeditate` → `finishedMeditating` →
vision message box → **go to Dathomir 5400, -3900 (radius 500)**, which is the Village area →
`focusWithCrystal` → `boardedWithNoslyn` → complete.

This looks like the bridge into the Village (Noslyn = "someone knowledgeable about the Jedi" in the Village prologue).

## Other relevant strings

- `jedi_spam:fs_progress_0..5`: the `/checkForce` style progress lines, from "You feel no connection with the Force." up to "You feel an inner glow. The Force is with you."
- `static_item:item_incite_book`: "An Ancient Jedi Text … experience an Inciting Incident immediately upon use" (PAAS vendor).
- `kb/kb_d:kb_d_content_fs`, `kb_d_content_village`, `kb_d_faq_gameplay_jedi`: the in-client knowledge base articles on the unlock.
- `padawan_trials_no_longer_eligible`: Padawan trials need six complete FS branches.

## Open items

- What triggers `bdg_jedi_unlock_ambient_event`, and where it sits in the order.
- Inciting-incident trigger list: not in client data. A capture of `/checkForce` output would show what the server reveals.
- Look at `buildout/` and `clientpoi/` datatables for the shrine and altar object placements.
- The Village phase quests: `fs_*` quest strings and tasks are extracted but not yet summarised.
