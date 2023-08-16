﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System.Data.Common;
using Microsoft.SqlTools.ServiceLayer.Connection.ReliableConnection;
using Microsoft.Data.SqlClient;

namespace Microsoft.SqlTools.ServiceLayer.Connection
{
    /// <summary>
    /// Factory class to create SqlClientConnections
    /// The purpose of the factory is to make it easier to mock out the database
    /// in 'offline' unit test scenarios.
    /// </summary>
    public class SqlConnectionFactory : ISqlConnectionFactory
    {
        /// <summary>
        /// Creates a new SqlConnection object
        /// </summary>
        /// <param name="retryProvider">Optional retry provider to handle errors in a special way</param>
        public DbConnection CreateSqlConnection(string connectionString, string azureAccountToken, SqlRetryLogicBaseProvider retryProvider = null)
        {
            RetryPolicy connectionRetryPolicy = RetryPolicyFactory.CreateDefaultConnectionRetryPolicy();
            RetryPolicy commandRetryPolicy = RetryPolicyFactory.CreateDefaultConnectionRetryPolicy();
            return new ReliableSqlConnection(connectionString, connectionRetryPolicy, commandRetryPolicy, azureAccountToken, retryProvider);
        }
    }
}
