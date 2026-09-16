using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// The rows <see cref="ClogWriter"/> produces, one type per encounter table. Each is fully
/// materialised on the Feed thread and written on the store's writer task - nothing here may hold a
/// reference to state the Feed thread can still mutate, which is why the contents row copies its
/// item list rather than pointing at the writer's live arrays.
///
/// <para><c>Key</c> is <c>encounter_started_at_ms</c> throughout: the one value MuckaConnection stamps
/// per encounter and hands to every writer, and what joins these to <c>swings</c> and
/// <c>fights</c>.</para>
/// </summary>
internal sealed record EncounterRow(
    long Key, string? Room, string? Weather, int? TimeToReset, long? PersonaSessionId) : IStoreRow
{
    // No estimate of when the reset will land. Every one of those was arithmetic on TimeToReset, and
    // wizards move it - see 0003_persona_sessions.sql. The countdown itself stays, raw, as a reading.
    private const string Sql = """
        INSERT INTO encounters (
            encounter_started_at_ms, room, weather, time_to_reset, persona_session_id
        ) VALUES (
            $key, $room, $weather, $ttr, $psid
        );
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$room", StoreWrite.Value(Room));
        command.Parameters.AddWithValue("$weather", StoreWrite.Value(Weather));
        command.Parameters.AddWithValue("$ttr", StoreWrite.Value(TimeToReset));
        command.Parameters.AddWithValue("$psid", StoreWrite.Value(PersonaSessionId));
        command.ExecuteNonQuery();
    }
}

/// <summary>Stamps an encounter's end. An UPDATE rather than a column written at insert time,
/// because the end is only known once the tail has drained to the next prompt.</summary>
internal sealed record EncounterEndRow(long Key, long EndedAtMs) : IStoreRow
{
    private const string Sql =
        "UPDATE encounters SET ended_at_ms = $ended WHERE encounter_started_at_ms = $key;";

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$ended", EndedAtMs);
        command.Parameters.AddWithValue("$key", Key);
        command.ExecuteNonQuery();
    }
}

/// <summary>One plain line either side of an encounter - see the <c>encounter_lines</c> table.
/// <paramref name="TimestampMs"/> is null for the pre-roll, which was buffered before the encounter
/// existed and carries no stamp of its own.</summary>
internal sealed record EncounterLineRow(
    long Key, string Phase, int Ord, long? TimestampMs, string Text) : IStoreRow
{
    private const string Sql = """
        INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, ts, text)
        VALUES ($key, $phase, $ord, $ts, $text);
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$phase", Phase);
        command.Parameters.AddWithValue("$ord", Ord);
        command.Parameters.AddWithValue("$ts", StoreWrite.Value(TimestampMs));
        command.Parameters.AddWithValue("$text", Text);
        command.ExecuteNonQuery();
    }
}

/// <summary>One absolute stats snapshot - see the <c>encounter_stats</c> table for what triggers
/// one and why they are snapshots rather than deltas.</summary>
internal sealed record EncounterStatsRow : IStoreRow
{
    public required long Key { get; init; }
    public required long TimestampMs { get; init; }
    public required string Reason { get; init; }

    public int? Stamina { get; init; }
    public int? MaxStamina { get; init; }
    public int? Strength { get; init; }
    public int? RawStrength { get; init; }
    public int? MaxStrength { get; init; }
    public int? Dexterity { get; init; }
    public int? RawDexterity { get; init; }
    public int? MaxDexterity { get; init; }
    public int? Magic { get; init; }
    public int? MaxMagic { get; init; }
    public int? ObjectsCarried { get; init; }
    public int? MaxObjectsCarried { get; init; }
    public int? CarriedCount { get; init; }
    public int? GamesPlayed { get; init; }
    public string? Weather { get; init; }
    public bool IsBlind { get; init; }
    public bool IsDeaf { get; init; }
    public bool IsCrippled { get; init; }
    public bool IsDumb { get; init; }
    public bool StrengthBuff { get; init; }
    public bool StrengthDebuff { get; init; }
    public bool DexterityBuff { get; init; }
    public bool DexterityDebuff { get; init; }
    public bool StaminaBuff { get; init; }
    public bool StaminaDebuff { get; init; }
    public bool Glow { get; init; }

    private const string Sql = """
        INSERT INTO encounter_stats (
            encounter_started_at_ms, ts, reason,
            stamina, max_stamina, strength, raw_strength, max_strength,
            dexterity, raw_dexterity, max_dexterity, magic, max_magic,
            objects_carried, max_objects_carried, carried_count,
            games_played, weather,
            is_blind, is_deaf, is_crippled, is_dumb,
            str_buff, str_debuff, dex_buff, dex_debuff, sta_buff, sta_debuff, glow
        ) VALUES (
            $key, $ts, $reason,
            $sta, $sta_max, $str, $str_raw, $str_max,
            $dex, $dex_raw, $dex_max, $magic, $magic_max,
            $objects, $objects_max, $carried,
            $games, $weather,
            $blind, $deaf, $crippled, $dumb,
            $str_buff, $str_debuff, $dex_buff, $dex_debuff, $sta_buff, $sta_debuff, $glow
        );
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$ts", TimestampMs);
        command.Parameters.AddWithValue("$reason", Reason);
        command.Parameters.AddWithValue("$sta", StoreWrite.Value(Stamina));
        command.Parameters.AddWithValue("$sta_max", StoreWrite.Value(MaxStamina));
        command.Parameters.AddWithValue("$str", StoreWrite.Value(Strength));
        command.Parameters.AddWithValue("$str_raw", StoreWrite.Value(RawStrength));
        command.Parameters.AddWithValue("$str_max", StoreWrite.Value(MaxStrength));
        command.Parameters.AddWithValue("$dex", StoreWrite.Value(Dexterity));
        command.Parameters.AddWithValue("$dex_raw", StoreWrite.Value(RawDexterity));
        command.Parameters.AddWithValue("$dex_max", StoreWrite.Value(MaxDexterity));
        command.Parameters.AddWithValue("$magic", StoreWrite.Value(Magic));
        command.Parameters.AddWithValue("$magic_max", StoreWrite.Value(MaxMagic));
        command.Parameters.AddWithValue("$objects", StoreWrite.Value(ObjectsCarried));
        command.Parameters.AddWithValue("$objects_max", StoreWrite.Value(MaxObjectsCarried));
        command.Parameters.AddWithValue("$carried", StoreWrite.Value(CarriedCount));
        command.Parameters.AddWithValue("$games", StoreWrite.Value(GamesPlayed));
        command.Parameters.AddWithValue("$weather", StoreWrite.Value(Weather));
        command.Parameters.AddWithValue("$blind", IsBlind ? 1 : 0);
        command.Parameters.AddWithValue("$deaf", IsDeaf ? 1 : 0);
        command.Parameters.AddWithValue("$crippled", IsCrippled ? 1 : 0);
        command.Parameters.AddWithValue("$dumb", IsDumb ? 1 : 0);
        command.Parameters.AddWithValue("$str_buff", StrengthBuff ? 1 : 0);
        command.Parameters.AddWithValue("$str_debuff", StrengthDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$dex_buff", DexterityBuff ? 1 : 0);
        command.Parameters.AddWithValue("$dex_debuff", DexterityDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$sta_buff", StaminaBuff ? 1 : 0);
        command.Parameters.AddWithValue("$sta_debuff", StaminaDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$glow", Glow ? 1 : 0);
        command.ExecuteNonQuery();
    }
}

/// <summary>One item in a room-contents snapshot.</summary>
internal readonly record struct EncounterContentsItem(string Name, bool IsCreature, bool IsCarried);

/// <summary>One room-contents snapshot: the parent row plus its items. The only row type here that
/// writes two tables, which is why the child needs the parent's rowid.</summary>
internal sealed record EncounterContentsRow(
    long Key, long TimestampMs, string? Room, int CarriedCount,
    EncounterContentsItem[] Items) : IStoreRow
{
    private const string ParentSql = """
        INSERT INTO encounter_contents (encounter_started_at_ms, ts, room, carried_count)
        VALUES ($key, $ts, $room, $carried);
        """;

    private const string ItemSql = """
        INSERT INTO encounter_contents_items (contents_id, ord, name, is_creature, is_carried)
        VALUES ($parent, $ord, $name, $creature, $carried);
        """;

    public void Write(StoreWrite write)
    {
        var parent = write.Prepared(ParentSql);
        parent.Parameters.AddWithValue("$key", Key);
        parent.Parameters.AddWithValue("$ts", TimestampMs);
        parent.Parameters.AddWithValue("$room", StoreWrite.Value(Room));
        parent.Parameters.AddWithValue("$carried", CarriedCount);
        parent.ExecuteNonQuery();

        var parentId = write.LastInsertRowId();
        for (var i = 0; i < Items.Length; i++)
        {
            var item = Items[i];
            var command = write.Prepared(ItemSql);
            command.Parameters.AddWithValue("$parent", parentId);
            command.Parameters.AddWithValue("$ord", i);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$creature", item.IsCreature ? 1 : 0);
            command.Parameters.AddWithValue("$carried", item.IsCarried ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }
}

/// <summary>One classified CombatEvent - see the <c>encounter_events</c> table.</summary>
internal sealed record EncounterEventRow(
    long Key, long TimestampMs, string Kind, string? Actor, string? Npc, string? Weapon,
    string? Container, int? RangeLow, int? RangeHigh, int? HealthRung, string? HealthPhrase,
    string? RawText) : IStoreRow
{
    private const string Sql = """
        INSERT INTO encounter_events (
            encounter_started_at_ms, ts, kind, actor, npc, weapon, container,
            range_low, range_high, health_rung, health_phrase, raw_text
        ) VALUES (
            $key, $ts, $kind, $actor, $npc, $weapon, $container,
            $low, $high, $rung, $phrase, $raw
        );
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$ts", TimestampMs);
        command.Parameters.AddWithValue("$kind", Kind);
        command.Parameters.AddWithValue("$actor", StoreWrite.Value(Actor));
        command.Parameters.AddWithValue("$npc", StoreWrite.Value(Npc));
        command.Parameters.AddWithValue("$weapon", StoreWrite.Value(Weapon));
        command.Parameters.AddWithValue("$container", StoreWrite.Value(Container));
        command.Parameters.AddWithValue("$low", StoreWrite.Value(RangeLow));
        command.Parameters.AddWithValue("$high", StoreWrite.Value(RangeHigh));
        command.Parameters.AddWithValue("$rung", StoreWrite.Value(HealthRung));
        command.Parameters.AddWithValue("$phrase", StoreWrite.Value(HealthPhrase));
        command.Parameters.AddWithValue("$raw", StoreWrite.Value(RawText));
        command.ExecuteNonQuery();
    }
}

/// <summary>One resolved <c>value &lt;name&gt;</c> probe - see the <c>creature_values</c> table for
/// what <paramref name="Ambiguous"/> means.</summary>
internal sealed record CreatureValueRow(
    long Key, long TimestampMs, string Npc, int Value, bool Ambiguous) : IStoreRow
{
    private const string Sql = """
        INSERT INTO creature_values (encounter_started_at_ms, ts, npc, value, ambiguous)
        VALUES ($key, $ts, $npc, $value, $ambiguous);
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$key", Key);
        command.Parameters.AddWithValue("$ts", TimestampMs);
        command.Parameters.AddWithValue("$npc", Npc);
        command.Parameters.AddWithValue("$value", Value);
        command.Parameters.AddWithValue("$ambiguous", Ambiguous ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
