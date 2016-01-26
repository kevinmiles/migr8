﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Migr8.Internals
{
    class SqlServerExclusiveDbConnection : IExclusiveDbConnection
    {
        readonly SqlConnection _connection;
        readonly SqlTransaction _transaction;

        public SqlServerExclusiveDbConnection(string connectionString)
        {
            _connection = new SqlConnection(connectionString);
            _connection.Open();
            _transaction = _connection.BeginTransaction(IsolationLevel.Serializable);
        }

        public void Dispose()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        public void Complete()
        {
            _transaction.Commit();
        }

        public HashSet<string> GetTableNames()
        {
            var tableNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            using (var command = CreateCommand())
            {
                command.CommandText = "SELECT [TABLE_NAME] FROM [information_schema].[tables]";

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tableNames.Add((string)reader["TABLE_NAME"]);
                    }
                }
            }

            return tableNames;
        }

        public SqlCommand CreateCommand()
        {
            var sqlCommand = _connection.CreateCommand();
            sqlCommand.Transaction = _transaction;
            return sqlCommand;
        }

        public IEnumerable<T> Select<T>(string columnName, string sqlQuery)
        {
            var results = new List<T>();

            using (var command = CreateCommand())
            {
                command.CommandText = sqlQuery;

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        results.Add((T)reader[columnName]);
                    }
                }
            }

            return results;
        }

        public void LogMigration(IExecutableSqlMigration migration, string migrationTableName)
        {
            using (var command = CreateCommand())
            {
                command.CommandText = $@"
INSERT INTO [{migrationTableName}] (
    [MigrationId],
    [Sql],
    [Description],
    [Time],
    [UserName],
    [UserDomainName],
    [MachineName]
) VALUES (
    @id,
    @sql,
    @description,
    @time,
    @userName,
    @userDomainName,
    @machineName
)
";

                command.Parameters.Add("id", SqlDbType.NVarChar, 200).Value = migration.Id;
                command.Parameters.Add("sql", SqlDbType.NVarChar).Value = migration.Sql;
                command.Parameters.Add("description", SqlDbType.NVarChar).Value = migration.Description;
                command.Parameters.Add("time", SqlDbType.DateTime2).Value = DateTime.Now;
                command.Parameters.Add("userName", SqlDbType.NVarChar).Value = Environment.UserName;
                command.Parameters.Add("userDomainName", SqlDbType.NVarChar).Value = Environment.UserDomainName;
                command.Parameters.Add("machineName", SqlDbType.NVarChar).Value = Environment.MachineName;

                command.ExecuteNonQuery();
            }
        }

        public void CreateMigrationTable(string migrationTableName)
        {
            using (var command = CreateCommand())
            {
                command.CommandText =
                    $@"
CREATE TABLE [{migrationTableName}] (
    [Id] INT IDENTITY(1,1),
    [MigrationId] NVARCHAR(200) NOT NULL,
    [Sql] NVARCHAR(MAX) NOT NULL,
    [Description] NVARCHAR(MAX) NOT NULL,
    [Time] DATETIME2 NOT NULL,
    [UserName] NVARCHAR(MAX) NOT NULL,
    [UserDomainName] NVARCHAR(MAX) NOT NULL,
    [MachineName] NVARCHAR(MAX) NOT NULL,

    CONSTRAINT [PK_{migrationTableName}_Id] PRIMARY KEY ([Id])
);
";

                command.ExecuteNonQuery();
            }

            using (var command = CreateCommand())
            {
                command.CommandText = 
                    $@"
ALTER TABLE [{migrationTableName}] 
    ADD CONSTRAINT [UNIQUE_{migrationTableName}_MigrationId] UNIQUE ([MigrationId]);
";

                command.ExecuteNonQuery();
            }
        }

        public void ExecuteStatement(string sqlStatement)
        {
            using (var command = CreateCommand())
            {
                command.CommandText = sqlStatement;
                command.ExecuteNonQuery();
            }

        }
    }
}