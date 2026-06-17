using System.Linq;
using Microsoft.Data.Sqlite;
using Starlight.Database;
using Starlight.SDK.Database.Impl.Entities;
using Xunit;

namespace Starlight.Tests;

public sealed class StarlightDatabaseTests
{
    // Verifies that schema initialization materializes both the mapped table and
    // the declared secondary index for the test entity.
    [Fact]
    public async Task Initialize_CreatesTableAndIndexes()
    {
        await using var scope = await CreateInitializedDatabaseAsync();
        await using var connection = await OpenConnectionAsync(scope.Path);

        Assert.Equal(1L, await ExecuteScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts';"));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_player_accounts_id';"));
    }

    // Verifies the basic persistence roundtrip: insert through the unit of work,
    // commit it, then load the row back by primary key.
    [Fact]
    public async Task SaveChanges_InsertsAndFindsEntity()
    {
        await using var scope = await CreateInitializedDatabaseAsync();
        var account = CreateAccount("alice", null);

        scope.Database.Add(account);

        var affected = await scope.Database.SaveChangesAsync();
        var loaded = await scope.Database.FindAsync<AccountEntity>(account.Id);

        Assert.Equal(1, affected);
        Assert.NotNull(loaded);
        Assert.Equal("alice", loaded!.Username);
        Assert.Null(loaded.Email);
    }

    // Verifies that auto-increment primary keys are written back to the tracked
    // CLR entity once the insert transaction commits.
    [Fact]
    public async Task SaveChanges_AssignsAutoIncrementPrimaryKey()
    {
        await using var scope = await CreateInitializedDatabaseAsync();
        var account = CreateAccount("alice", "alice@example.test");

        Assert.Equal(0u, account.Id);

        scope.Database.Add(account);
        await scope.Database.SaveChangesAsync();

        Assert.NotEqual(0u, account.Id);
    }

    // Verifies that modifying a tracked entity marks it dirty and persists the
    // updated values on the next SaveChangesAsync call.
    [Fact]
    public async Task SaveChanges_UpdatesModifiedTrackedEntity()
    {
        await using var scope = await CreateInitializedDatabaseAsync();
        var account = CreateAccount("alice", "alice@example.test");

        scope.Database.Add(account);
        await scope.Database.SaveChangesAsync();

        account.Username = "alice_v2";
        account.UpdatedAt = account.UpdatedAt.AddMinutes(1);

        var affected = await scope.Database.SaveChangesAsync();
        var loaded = await scope.Database.FindAsync<AccountEntity>(account.Id);

        Assert.Equal(1, affected);
        Assert.NotNull(loaded);
        Assert.Equal("alice_v2", loaded!.Username);
    }

    // Verifies that removing a tracked entity deletes the backing row and makes
    // subsequent key lookups return no result.
    [Fact]
    public async Task SaveChanges_DeletesEntity()
    {
        await using var scope = await CreateInitializedDatabaseAsync();
        var account = CreateAccount("alice", "alice@example.test");

        scope.Database.Add(account);
        await scope.Database.SaveChangesAsync();

        scope.Database.Remove(account);

        var affected = await scope.Database.SaveChangesAsync();
        var loaded = await scope.Database.FindAsync<AccountEntity>(account.Id);

        Assert.Equal(1, affected);
        Assert.Null(loaded);
    }

    // Verifies that nullable comparisons are translated to SQL null semantics
    // rather than relying on client-side filtering.
    [Fact]
    public async Task QueryAsync_TranslatesNullComparisons()
    {
        await using var scope = await CreateInitializedDatabaseAsync();

        scope.Database.Add(CreateAccount("alice", null));
        scope.Database.Add(CreateAccount("bob", null));
        scope.Database.Add(CreateAccount("carol", "carol@example.test"));
        await scope.Database.SaveChangesAsync();

        var nullEmailUsers = await scope.Database.QueryAsync<AccountEntity>(x => x.Email == null);
        var nonNullEmailUsers = await scope.Database.QueryAsync<AccountEntity>(x => x.Email != null);

        Assert.Equal(["alice", "bob"], nullEmailUsers.Select(x => x.Username).OrderBy(x => x).ToArray());
        Assert.Equal(["carol"], nonNullEmailUsers.Select(x => x.Username).ToArray());
    }

    // Verifies that the supported string predicates are translated with proper
    // LIKE escaping for literal %, _ and backslash characters.
    [Fact]
    public async Task QueryAsync_TranslatesStringContainsStartsWithEndsWith()
    {
        await using var scope = await CreateInitializedDatabaseAsync();

        scope.Database.Add(CreateAccount("100%hero", "a@example.test"));
        scope.Database.Add(CreateAccount("100xhero", "b@example.test"));
        scope.Database.Add(CreateAccount("hero_name", "c@example.test"));
        scope.Database.Add(CreateAccount("heroXname", "d@example.test"));
        scope.Database.Add(CreateAccount("path\\tail", "e@example.test"));
        scope.Database.Add(CreateAccount("path-tail", "f@example.test"));
        await scope.Database.SaveChangesAsync();

        var containsPercent = await scope.Database.QueryAsync<AccountEntity>(x => x.Username.Contains("%"));
        var startsWithUnderscore = await scope.Database.QueryAsync<AccountEntity>(x => x.Username.StartsWith("hero_"));
        var endsWithBackslash = await scope.Database.QueryAsync<AccountEntity>(x => x.Username.EndsWith("\\tail"));

        Assert.Equal(["100%hero"], containsPercent.Select(x => x.Username).ToArray());
        Assert.Equal(["hero_name"], startsWithUnderscore.Select(x => x.Username).ToArray());
        Assert.Equal(["path\\tail"], endsWithBackslash.Select(x => x.Username).ToArray());
    }

    // Verifies that Enumerable.Contains becomes an IN clause and that an empty
    // candidate set short-circuits to no matches.
    [Fact]
    public async Task QueryAsync_TranslatesEnumerableContains()
    {
        await using var scope = await CreateInitializedDatabaseAsync();

        scope.Database.Add(CreateAccount("alice", "a@example.test"));
        scope.Database.Add(CreateAccount("bob", "b@example.test"));
        scope.Database.Add(CreateAccount("carol", "c@example.test"));
        await scope.Database.SaveChangesAsync();

        var selectedUsers = new[] { "alice", "carol" };
        var noUsers = Array.Empty<string>();

        var selected = await scope.Database.QueryAsync<AccountEntity>(x => Enumerable.Contains(selectedUsers, x.Username));
        var empty = await scope.Database.QueryAsync<AccountEntity>(x => Enumerable.Contains(noUsers, x.Username));

        Assert.Equal(["alice", "carol"], selected.Select(x => x.Username).OrderBy(x => x).ToArray());
        Assert.Empty(empty);
    }

    // Verifies that the Single terminal path preserves the expected error when
    // more than one row matches the predicate.
    [Fact]
    public async Task Queryable_Single_ThrowsWhenMultipleRowsMatch()
    {
        await using var scope = await CreateInitializedDatabaseAsync();

        scope.Database.Add(CreateAccount("alice", null));
        scope.Database.Add(CreateAccount("bob", null));
        await scope.Database.SaveChangesAsync();

        Assert.Throws<InvalidOperationException>(() =>
            scope.Database.Set<AccountEntity>()
                .Where(x => x.Email == null)
                .Single());
    }

    private static AccountEntity CreateAccount(string username, string? email) => new() {
        Username = username,
        Email = email,
        Password = $"{username}_pw",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<SqliteConnection> OpenConnectionAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path
        }.ToString());

        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private static async Task<TestDatabaseScope> CreateInitializedDatabaseAsync()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Starlight.Tests");
        Directory.CreateDirectory(tempDirectory);

        var path = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.db");
        var database = new StarlightDatabase(new StarlightDatabaseOptions {
            Path = path,
            UseWal = false,
            AllowClientEvaluation = false
        });

        await database.InitializeAsync([typeof(AccountEntity)]);
        return new TestDatabaseScope(path, database);
    }

    private sealed class TestDatabaseScope(string path, StarlightDatabase database) : IAsyncDisposable
    {
        public string Path { get; } = path;
        public StarlightDatabase Database { get; } = database;

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
        }
    }
}
