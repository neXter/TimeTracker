using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

/// <summary>An app update must never lose or corrupt recorded data.</summary>
public class MigrationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), "tt-mig-" + Guid.NewGuid().ToString("N") + ".db");
    private static void Cleanup(string path)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) { try { File.Delete(f); } catch { } }
    }

    private static readonly Migration V2 = new(2, "ALTER TABLE app ADD COLUMN notes TEXT; UPDATE app SET notes='migrated';");

    [Fact]
    public void Current_database_layout_has_consecutive_versions_starting_at_one()
    {
        var v = Db.DefaultMigrations.Select(m => m.Version).ToList();
        Assert.Equal(Enumerable.Range(1, v.Count), v);
        Assert.Equal(v[^1], Db.SchemaVersion);
    }

    [Fact]
    public void Fresh_database_gets_the_newest_version()
    {
        var path = TempDb();
        try { using var db = new Db(path); Assert.Equal(Db.SchemaVersion.ToString(), db.GetMeta("schema_version")); }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Upgrading_keeps_all_data_settings_and_names_and_makes_a_backup()
    {
        var path = TempDb();
        try
        {
            // "old app": layout version 1, with real data and settings
            using (var old = new Db(path, new[] { Db.DefaultMigrations[0] }))
            {
                var set = new Settings(old); set.Set("language", "de"); set.Set("idleMinutes", 10); set.SetPin("1234");
                long app = old.Insert("INSERT INTO app(exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc) VALUES('C:\\g.exe','g.exe','g','fallback',0,1)");
                old.Exec("INSERT INTO foreground_interval(app_id,start_utc,end_utc) VALUES($0,1000,61000)", app);
                old.Exec("INSERT INTO device_interval(state,start_utc,end_utc) VALUES('active',1000,61000)");
                new AliasService(old).Save(new AliasService.SaveRequest { AppIds = new() { app }, Name = "My Game", MatchKind = "exe_path" });
            }

            // "new app": knows a version 2 migration
            using (var upgraded = new Db(path, new[] { Db.DefaultMigrations[0], V2 }))
            {
                Assert.Equal("2", upgraded.GetMeta("schema_version"));
                Assert.Equal("migrated", upgraded.Scalar<string>("SELECT notes FROM app"));      // the new migration ran
                Assert.Equal(1, upgraded.Scalar<long>("SELECT COUNT(*) FROM foreground_interval"));
                Assert.Equal(1, upgraded.Scalar<long>("SELECT COUNT(*) FROM device_interval"));
                var s = new Settings(upgraded);
                Assert.Equal("de", s.Language); Assert.Equal(10, s.IdleMinutes); Assert.True(s.VerifyPin("1234"));
                Assert.Contains(new AliasService(upgraded).List(), a => a.DisplayName == "My Game");
            }
            Assert.True(File.Exists(path + ".pre-v2.bak"), "a backup of the old data should exist");
            using var bak = new Db(path + ".pre-v2.bak", new[] { Db.DefaultMigrations[0] });     // the backup is a valid v1 database
            Assert.Equal("1", bak.GetMeta("schema_version"));
            Assert.Equal(1, bak.Scalar<long>("SELECT COUNT(*) FROM foreground_interval"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Reopening_an_up_to_date_database_changes_nothing_and_makes_no_new_backup()
    {
        var path = TempDb();
        try
        {
            using (var a = new Db(path)) a.SetMeta("marker", "x");
            using (var b = new Db(path)) Assert.Equal("x", b.GetMeta("marker"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*.bak"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void An_older_app_refuses_a_database_written_by_a_newer_version_without_touching_it()
    {
        var path = TempDb();
        try
        {
            using (var newer = new Db(path, new[] { Db.DefaultMigrations[0], V2 })) newer.SetMeta("marker", "keep");
            var ex = Assert.Throws<NewerDatabaseException>(() => new Db(path, new[] { Db.DefaultMigrations[0] }));
            Assert.Equal(2, ex.FoundVersion); Assert.Equal(1, ex.SupportedVersion);
            using var again = new Db(path, new[] { Db.DefaultMigrations[0], V2 });
            Assert.Equal("keep", again.GetMeta("marker"));
            Assert.Equal("2", again.GetMeta("schema_version"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_failing_migration_rolls_back_and_leaves_the_old_data_usable()
    {
        var path = TempDb();
        try
        {
            using (var old = new Db(path, new[] { Db.DefaultMigrations[0] })) old.SetMeta("marker", "safe");
            var broken = new Migration(2, "ALTER TABLE app ADD COLUMN ok TEXT; ALTER TABLE does_not_exist ADD COLUMN x TEXT;");
            Assert.ThrowsAny<Exception>(() => new Db(path, new[] { Db.DefaultMigrations[0], broken }));
            using var still = new Db(path, new[] { Db.DefaultMigrations[0] });
            Assert.Equal("safe", still.GetMeta("marker"));
            Assert.Equal("1", still.GetMeta("schema_version"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_database_created_by_release_1_0_opens_unchanged()
    {
        // Exactly what 1.0.0 wrote: schema 1 plus the javaw/Minecraft default rule.
        var path = TempDb();
        try
        {
            using (var v1 = new Db(path, new[] { Db.DefaultMigrations[0] }))
                Assert.Equal(1, v1.Scalar<long>("SELECT COUNT(*) FROM app_alias"));
            using var now = new Db(path);
            Assert.Equal(1, now.Scalar<long>("SELECT COUNT(*) FROM app_alias"));      // not seeded twice
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*.bak"));
        }
        finally { Cleanup(path); }
    }
}
