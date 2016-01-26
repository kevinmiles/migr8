﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Migr8.Internals.Databases;

namespace Migr8.Internals
{
    class DatabaseMigratorCore
    {
        readonly IWriter _writer;
        readonly string _connectionString;
        readonly IDb _db;
        readonly string _migrationTableName;

        public DatabaseMigratorCore(IWriter writer, string connectionString, string migrationTableName = null, IDb db = null)
        {
            _writer = writer;
            _connectionString = connectionString;
            _db = db ?? new SqlServerDb();
            _migrationTableName = migrationTableName ?? Options.DefaultMigrationTableName;
        }

        public void Execute(IEnumerable<IExecutableSqlMigration> migrations)
        {
            var executableSqlMigrations = migrations.ToList();

            if (executableSqlMigrations.Count == 0)
            {
                _writer.Write("Found no migrations");
                return;
            }

            _writer.Write($"Migr8 found {executableSqlMigrations.Count} migrations");

            AssertHasNoDuplicateIds(executableSqlMigrations);

            while (true)
            {
                var didExecuteMigration = ExecuteNextMigration(executableSqlMigrations);

                if (!didExecuteMigration)
                {
                    _writer.Write("No more migrations to run");
                    break;
                }
            }
        }

        static void AssertHasNoDuplicateIds(IEnumerable<IExecutableSqlMigration> executableSqlMigrations)
        {
            var duplicatedMigrations = executableSqlMigrations
                .GroupBy(m => m.Id)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicatedMigrations.Any())
            {
                var duplicatedIds = string.Join(", ", duplicatedMigrations.SelectMany(g => g.Select(m => m.Id)));

                throw new MigrationException(
                    $"Cannot execute migrations because the following migrations are duplicates: {duplicatedIds}");
            }
        }

        bool ExecuteNextMigration(List<IExecutableSqlMigration> migrations)
        {
            using (var connection = _db.GetExclusiveDbConnection(_connectionString))
            {
                EnsureMigrationTableExists(connection);

                var nextMigration = GetNextMigration(connection, migrations);

                if (nextMigration == null)
                {
                    return false;
                }

                try
                {
                    ExecuteMigration(nextMigration, connection);

                    connection.Complete();

                    return true;
                }
                catch (Exception exception)
                {
                    throw new MigrationException($"Could not execute migration with ID '{nextMigration.Id}': {nextMigration.Sql}", exception);
                }
            }
        }

        void ExecuteMigration(IExecutableSqlMigration migration, IExclusiveDbConnection connection)
        {
            LogMigration(connection, migration);

            var id = migration.Id;
            var sql = migration.Sql;

            const RegexOptions options = RegexOptions.Multiline
                                         | RegexOptions.IgnorePatternWhitespace
                                         | RegexOptions.IgnoreCase;

            const string searchPattern = @"^\s*GO\s* ($ | \-\- .*$)";

            var sqlStatements = Regex.Split(sql, searchPattern, options)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim(' ', '\r', '\n'));

            foreach (var sqlStatement in sqlStatements)
            {
                connection.ExecuteStatement(sqlStatement);
            }

            _writer.Write($"Migration {id} executed");
        }

        IExecutableSqlMigration GetNextMigration(IExclusiveDbConnection connection, List<IExecutableSqlMigration> migrations)
        {
            var executedMigrationIds = connection
                .Select<string>("MigrationId", $"SELECT [MigrationId] FROM [{_migrationTableName}]")
                .ToList();

            var remainingMigrations = migrations
                .Where(m => !executedMigrationIds.Contains(m.Id))
                .ToList();

            VerifyMigrationBandit(executedMigrationIds, remainingMigrations);

            remainingMigrations.Sort((m1, m2) => CompareMigrationId(m1.Id, m2.Id));

            var nextMigration = remainingMigrations.FirstOrDefault();

            return nextMigration;
        }

        static void VerifyMigrationBandit(IEnumerable<string> executedMigrationIds, List<IExecutableSqlMigration> remainingMigrations)
        {
            var executedMigrationsByBranch = executedMigrationIds
                .Select(MigrationId.FromString)
                .GroupBy(id => id.BranchSpecification)
                .ToList();

            var migrationsThatShouldHaveBeenExecutedByNow = remainingMigrations
                .Where(m => ShouldHaveBeenExecutedByNow(executedMigrationsByBranch, m))
                .ToList();

            if (migrationsThatShouldHaveBeenExecutedByNow.Any())
            {
                var ids = string.Join(", ", migrationsThatShouldHaveBeenExecutedByNow.Select(m => m.Id));

                throw new MigrationException($"Cannot execute migrations because migration the following migrations have NOT been executed by now, even though they should have been: {ids}");
            }
        }

        static bool ShouldHaveBeenExecutedByNow(IEnumerable<IGrouping<string, MigrationId>> executedMigrationsByBranch, IExecutableSqlMigration executableSqlMigration)
        {
            var migrationId = MigrationId.FromString(executableSqlMigration.Id);

            var migrationsForThisParticularBranch = executedMigrationsByBranch.FirstOrDefault(b => b.Key == migrationId.BranchSpecification)?.ToList() 
                ?? new List<MigrationId>();

            return migrationsForThisParticularBranch.Any(id => id.CompareTo(migrationId) > 0);
        }

        class MigrationId : IComparable<MigrationId>
        {
            public static MigrationId FromString(string migrationId)
            {
                var tokens = migrationId.Split('-');

                return new MigrationId(int.Parse(tokens.First()), string.Join("-", tokens.Skip(1)));
            }

            MigrationId(int sequenceNumber, string branchSpecification)
            {
                SequenceNumber = sequenceNumber;
                BranchSpecification = branchSpecification;
            }

            public int SequenceNumber { get; }
            public string BranchSpecification { get; }

            public int CompareTo(MigrationId other)
            {
                var majorCompare = SequenceNumber.CompareTo(other.SequenceNumber);

                return majorCompare != 0
                    ? majorCompare
                    : string.Compare(BranchSpecification, other.BranchSpecification, StringComparison.InvariantCultureIgnoreCase);
            }
        }

        static int CompareMigrationId(string id1, string id2)
        {
            var v1 = MigrationId.FromString(id1);
            var v2 = MigrationId.FromString(id2);

            return v1.CompareTo(v2);
        }

        void EnsureMigrationTableExists(IExclusiveDbConnection connection)
        {
            var tableNames = connection.GetTableNames();

            if (!tableNames.Contains(_migrationTableName))
            {
                CreateMigrationTable(_migrationTableName, connection);
            }
        }

        void CreateMigrationTable(string migrationTableName, IExclusiveDbConnection connection)
        {
            try
            {
                connection.CreateMigrationTable(migrationTableName);

                _writer.Write($"Created migration table '{migrationTableName}'");
            }
            catch (Exception exception)
            {
                throw new MigrationException($"Could not create migration table '{migrationTableName}'", exception);
            }
        }

        void LogMigration(IExclusiveDbConnection connection, IExecutableSqlMigration migration)
        {
            connection.LogMigration( migration, _migrationTableName);
        }
    }
}