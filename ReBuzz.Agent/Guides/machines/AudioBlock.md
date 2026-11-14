## How it sounds

A control machine that plays audio clips laid out on a timeline in its own window, following the song's position. It makes no sound of its own when the timeline is empty, and its one parameter is Mute.

## Use it for

- Songs that already use it for audio on a timeline.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mute | No | yes silences every clip |

## Starting points

- Use it as the song already does: `machine_describe(machine="AudioBlock")` lists what is on it.
- For new work use the Session View's audio tracks or the arrangement.

## Pitfalls

- The clips live in its own window and are saved with the song; the Studio does not show that window, so they cannot be edited here: unverified how an AudioBlock song plays in the Studio.
- Silent at its defaults in the profile (no clips placed): that is expected.

## Pairs well with

- Any machine you want to line up with its clips.

## Successor

None: the Session View's Session Clip Player does the same job but is not a drop-in for it, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="AudioBlock")`: its one parameter is `Mute`.
- [ ] `param_get(machine="AudioBlock")`: `Mute` reads No.
- [ ] `meters_read(machines=["AudioBlock"])`: with clips placed it shows a level.
