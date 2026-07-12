using MudSharp.Models;

namespace MudSharp.Session;

/// <summary>
/// Maintains the present/absent state of the local player's temporary magical effects
/// from the discrete <see cref="StatusEffectChange"/> events the parser emits.
///
/// <para>Binary model: <see cref="EffectTransition.Started"/> turns a (stat, sign) slot on,
/// <see cref="EffectTransition.FullyWoreOff"/> turns it off, and
/// <see cref="EffectTransition.PartiallyWoreOff"/> is ignored (the effect is still active).
/// Stack depth is deliberately not tracked — cast and wear-off message counts don't line up
/// (one cast can be multi-level and bleeds off in stages), so depth from messages is a lie.</para>
///
/// <para>Not internally locked: <see cref="Apply"/> and <see cref="Reset"/> are called from
/// the parser Feed thread only (same thread that decodes the C11 codes). Consumers marshal
/// <see cref="Changed"/> to their UI thread.</para>
/// </summary>
public sealed class EffectTracker
{
    private bool _strBuff, _strDebuff, _dexBuff, _dexDebuff, _staBuff, _staDebuff, _glow;
    // The game line that last turned each slot on — surfaced as the icon tooltip.
    private string? _strBuffMsg, _strDebuffMsg, _dexBuffMsg, _dexDebuffMsg, _staBuffMsg, _staDebuffMsg, _glowMsg;
    // Affliction tooltip lines (no on/off tracked here — FES drives their display).
    private string? _deafMsg, _blindMsg, _dumbMsg, _crippledMsg;

    /// <summary>Fires with the new snapshot only when the active set actually changes.</summary>
    public event Action<StatusEffectState>? Changed;

    public StatusEffectState Current { get; private set; } = StatusEffectState.Empty;

    public void Apply(StatusEffectChange change)
    {
        // PartiallyWoreOff leaves the slot on — a level bled off but the effect remains.
        if (change.Transition == EffectTransition.PartiallyWoreOff)
            return;

        bool on = change.Transition == EffectTransition.Started;
        string? msg = on ? change.Message : null;   // keep the start line; clear it when off

        switch (change.Kind, change.Sign)
        {
            case (StatusEffectKind.Strength,  EffectSign.Buff):   _strBuff   = on; _strBuffMsg   = msg; break;
            case (StatusEffectKind.Strength,  EffectSign.Debuff): _strDebuff = on; _strDebuffMsg = msg; break;
            case (StatusEffectKind.Dexterity, EffectSign.Buff):   _dexBuff   = on; _dexBuffMsg   = msg; break;
            case (StatusEffectKind.Dexterity, EffectSign.Debuff): _dexDebuff = on; _dexDebuffMsg = msg; break;
            case (StatusEffectKind.Stamina,   EffectSign.Buff):   _staBuff   = on; _staBuffMsg   = msg; break;
            case (StatusEffectKind.Stamina,   EffectSign.Debuff): _staDebuff = on; _staDebuffMsg = msg; break;
            case (StatusEffectKind.Glow,      _):                 _glow      = on; _glowMsg      = msg; break;
            // Afflictions: cache the tooltip line only (FES flags drive their visibility).
            case (StatusEffectKind.Deaf,      _): _deafMsg     = msg; break;
            case (StatusEffectKind.Blind,     _): _blindMsg    = msg; break;
            case (StatusEffectKind.Dumb,      _): _dumbMsg     = msg; break;
            case (StatusEffectKind.Crippled,  _): _crippledMsg = msg; break;
        }

        Publish();
    }

    /// <summary>Clear all effects — call on game-mode entry/exit (relog wipes effects).</summary>
    public void Reset()
    {
        _strBuff = _strDebuff = _dexBuff = _dexDebuff = _staBuff = _staDebuff = _glow = false;
        _strBuffMsg = _strDebuffMsg = _dexBuffMsg = _dexDebuffMsg = _staBuffMsg = _staDebuffMsg = _glowMsg = null;
        _deafMsg = _blindMsg = _dumbMsg = _crippledMsg = null;
        Publish();
    }

    private void Publish()
    {
        var next = new StatusEffectState(
            _strBuff, _strDebuff, _dexBuff, _dexDebuff, _staBuff, _staDebuff, _glow,
            _strBuffMsg, _strDebuffMsg, _dexBuffMsg, _dexDebuffMsg, _staBuffMsg, _staDebuffMsg, _glowMsg,
            _deafMsg, _blindMsg, _dumbMsg, _crippledMsg);
        if (next == Current) return;   // record value-equality — no-op changes stay silent
        Current = next;
        Changed?.Invoke(next);
    }
}
