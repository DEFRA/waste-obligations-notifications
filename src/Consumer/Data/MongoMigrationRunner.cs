using System.Reflection;
using AdaskoTheBeAsT.MongoDbMigrations;
using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using AdaskoTheBeAsT.MongoDbMigrations.Core.Contracts;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationRunner : IMongoMigrationRunner
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<MongoMigrationRunner> _logger;
    private readonly MongoMigrationCompletion _criticalCompletion;
    private readonly Assembly _migrationAssembly;
    private readonly MongoMigration[] _migrations;
    private readonly MongoMigrationOptions _options;
    private readonly TimeProvider _timeProvider;

    public MongoMigrationRunner(
        IMongoDatabase database,
        ILogger<MongoMigrationRunner> logger,
        MongoMigrationCompletion criticalCompletion,
        Assembly? migrationAssembly = null,
        IOptions<MongoMigrationOptions>? options = null,
        TimeProvider? timeProvider = null
    )
    {
        _database = database;
        _logger = logger;
        _criticalCompletion = criticalCompletion;
        _migrationAssembly = migrationAssembly ?? typeof(MongoMigration).Assembly;
        _options = options?.Value ?? new MongoMigrationOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrations = _migrationAssembly
            .GetTypes()
            .Where(type => type.IsAssignableTo(typeof(MongoMigration)) && !type.IsAbstract)
            .Select(type => (MongoMigration)Activator.CreateInstance(type)!)
            .OrderBy(migration => migration.Version)
            .ToArray();
    }

    public async Task<bool> CheckCompletion(CancellationToken cancellationToken)
    {
        var critical = _migrations.Where(migration => migration.Critical).ToArray();
        if (critical.Length == 0)
            _criticalCompletion.MarkCompleted();

        // The engine appends history only after a successful migration. "d" denotes Up direction.
        var history = await _database
            .GetCollection<BsonDocument>("_migrations")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken);
        var applied = history
            .Where(entry => entry.GetValue("d", false) == BsonBoolean.True)
            .Select(entry => new MigrationVersion(entry.GetValue("v", "0.0.0").AsString))
            .ToHashSet();

        if (!_criticalCompletion.IsCompleted)
        {
            if (critical.Any(migration => !applied.Contains(migration.Version)))
                return false;

            if (critical.Length > 0 && !await critical[^1].ValidateSchema(_database, cancellationToken))
                return false;

            _criticalCompletion.MarkCompleted();
        }

        return _migrations.All(migration => applied.Contains(migration.Version));
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await RunMigrations(execution.Token, deadline);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Mongo migration operation exceeded its configured timeout.");
        }
    }

    private async Task RunMigrations(CancellationToken cancellationToken, CancellationTokenSource deadline)
    {
        using var engine = new MigrationEngineBuilder().UseDatabase(
            _database.Client,
            _database.DatabaseNamespace.DatabaseName
        );

        var configuredEngine = engine
            .UseAssembly(_migrationAssembly)
            .UseSchemeValidation(false)
            .UseBeforeMigration(migration =>
                deadline.CancelAfter(
                    TimeSpan.FromSeconds(
                        migration is MongoMigration { Critical: true }
                            ? _options.CriticalOperationTimeoutSeconds
                            : _options.AttemptTimeoutSeconds
                    )
                )
            )
            .UseAfterMigration(
                (migration, success) =>
                {
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    LogMigrationCompletion(migration, success);
                }
            );
        var critical = _migrations.LastOrDefault(migration => migration.Critical);
        if (critical is not null && !_criticalCompletion.IsCompleted)
        {
            var history = await _database
                .GetCollection<BsonDocument>("_migrations")
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(cancellationToken);
            var currentVersion = history
                .Select(entry => new MigrationVersion(entry.GetValue("v", "0.0.0").AsString))
                .Append(critical.Version)
                .Max();
            // Never target an earlier version: RunAsync(target) would roll back a newer environment.
            var prerequisite = await configuredEngine.RunAsync(currentVersion, cancellationToken);
            await CheckCompletion(cancellationToken);
            if (!prerequisite.Success || !_criticalCompletion.IsCompleted)
                throw new InvalidOperationException(
                    "Critical Mongo migrations or current schema validation are incomplete."
                );
        }

        MigrationResult result;
        try
        {
            result = await configuredEngine.RunAsync(cancellationToken);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                await CheckCompletion(cancellationToken);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException("Mongo migrations did not complete successfully.");
        }

        if (!await CheckCompletion(cancellationToken))
        {
            throw new InvalidOperationException("Mongo migration history or current schema validation is incomplete.");
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Mongo migrations completed. Current version is {CurrentVersion}. Applied {AppliedMigrationCount} migration(s).",
                result.CurrentVersion,
                result.InterimSteps.Count
            );
        }
    }

    private void LogMigrationCompletion(IMigration migration, bool success)
    {
        if (success && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Mongo migration {MigrationName} version {MigrationVersion} completed.",
                migration.Name,
                migration.Version
            );
        }
        else if (!success && _logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(
                "Mongo migration {MigrationName} version {MigrationVersion} failed.",
                migration.Name,
                migration.Version
            );
        }
    }
}
