using System.Reflection;
using DbUp.Engine;

namespace Mucka.Store;

/// <summary>
/// Every schema migration, in execution order. This list IS the schema - there is no second
/// description of the current shape anywhere, deliberately, because two descriptions drift.
///
/// <para><b>Registered explicitly, never discovered.</b> DbUp's usual entry point,
/// <c>WithScriptsEmbeddedInAssembly</c>, calls <c>Assembly.GetManifestResourceNames()</c> to find
/// scripts. The app publishes Android with <c>PublishTrimmed=true</c>, where a discovery step that
/// silently returns zero scripts leaves a stranger's database un-migrated with nothing thrown and
/// nothing journaled - a failure nobody would see until an INSERT hit a missing column weeks later.
/// Naming each resource in a literal array cannot return zero without failing loudly here.</para>
///
/// <para><b>Order comes from the NAME, not from this array.</b> DbUp sorts by script name before
/// executing (<c>ScriptSorter</c>) and the journal reads back ordered by name too, so a script called
/// <c>10_x</c> would run before <c>2_x</c>. Names are zero-padded to four digits and a guard test
/// enforces it.</para>
///
/// <para><b>An already-released script is never edited.</b> It has run on machines we cannot reach;
/// editing it changes what a fresh install gets without changing what those machines have, which is
/// the one way to make two databases that can never converge. A guard test pins each script's hash.
/// Change the schema by adding the next number.</para>
/// </summary>
internal static class MigrationScripts
{
    /// <summary>DbUp's journal table. Named here because <see cref="LegacySchemaAdopter"/> has to ask
    /// whether it exists before DbUp has had a chance to create it.</summary>
    internal const string JournalTable = "SchemaVersions";

    /// <summary>The shape every database enters the journaled era at - v0.20.0, the last release
    /// before the journal. An ordinary script: its DDL is entirely CREATE ... IF NOT EXISTS, so it is
    /// idempotent, and running it against a legacy file that already has those tables does nothing.
    /// That is what lets a fresh install and a v0.20.0 install arrive at the same rung by the same
    /// text, rather than by two code paths that have to be kept equal.</summary>
    internal const string BaselineName = "0001_baseline.sql";

    /// <summary>Every script, in execution order.</summary>
    internal static IReadOnlyList<SqlScript> All { get; } =
    [
        Load(BaselineName),
        Load("0002_drop_level.sql"),
        Load("0003_persona_sessions.sql"),
    ];

    /// <summary>Script names, for the guard tests.</summary>
    internal static IReadOnlyList<string> AllNames { get; } = [.. All.Select(s => s.Name)];

    /// <summary>
    /// Reads one embedded script by its exact resource name. Never enumerates: a wrong name throws
    /// here, at startup, naming the resource - which is the failure we want, rather than a migration
    /// that silently is not there.
    /// </summary>
    private static SqlScript Load(string fileName)
    {
        var resource = $"Mucka.Store.Migrations.{fileName}";
        var assembly = typeof(MigrationScripts).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Migration script '{resource}' is not embedded in {assembly.GetName().Name}. " +
                "Check the EmbeddedResource item in Mucka.Store.csproj.");
        using var reader = new StreamReader(stream);
        return new SqlScript(fileName, reader.ReadToEnd());
    }
}
