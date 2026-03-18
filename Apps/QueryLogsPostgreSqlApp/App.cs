/*
Technitium DNS Server
Copyright (C) 2025  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.ApplicationCommon;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace QueryLogsPostgreSql
{
    /// <summary>
    /// DNS application that logs all incoming DNS requests and their responses
    /// in a PostgreSQL database. Implements both logging and querying interfaces
    /// so that logs can be viewed from the DNS Server web console.
    /// </summary>
    public sealed class App : IDnsApplication, IDnsQueryLogger, IDnsQueryLogs
    {
        #region variables

        IDnsServer? _dnsServer;

        bool _enableLogging;
        int _maxQueueSize;
        int _maxLogDays;
        int _maxLogRecords;
        string? _databaseName;
        string? _connectionString;

        Channel<LogEntry>? _channel;
        ChannelWriter<LogEntry>? _channelWriter;
        Thread? _consumerThread;
        const int BULK_INSERT_COUNT = 1000;
        const int BULK_INSERT_ERROR_DELAY = 10000;

        readonly Timer _cleanupTimer;
        const int CLEAN_UP_TIMER_INITIAL_INTERVAL = 5 * 1000;
        const int CLEAN_UP_TIMER_PERIODIC_INTERVAL = 15 * 60 * 1000;

        bool _isStartupInit = true;

        #endregion

        #region constructor

        public App()
        {
            _cleanupTimer = new Timer(async delegate (object? state)
            {
                try
                {
                    await using (NpgsqlConnection connection = new NpgsqlConnection(_connectionString + $" Database={_databaseName};"))
                    {
                        await connection.OpenAsync();

                        if (_maxLogRecords > 0)
                        {
                            int totalRecords;

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT Count(*) FROM dns_logs;";

                                totalRecords = Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
                            }

                            int recordsToRemove = totalRecords - _maxLogRecords;
                            if (recordsToRemove > 0)
                            {
                                await using (NpgsqlCommand command = connection.CreateCommand())
                                {
                                    command.CommandText = $"DELETE FROM dns_logs WHERE dlid IN (SELECT dlid FROM dns_logs ORDER BY dlid LIMIT {recordsToRemove});";

                                    await command.ExecuteNonQueryAsync();
                                }
                            }
                        }

                        if (_maxLogDays > 0)
                        {
                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "DELETE FROM dns_logs WHERE \"timestamp\" < @timestamp;";

                                command.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.AddDays(_maxLogDays * -1));

                                await command.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _dnsServer?.WriteLog(ex);
                }
                finally
                {
                    try
                    {
                        _cleanupTimer?.Change(CLEAN_UP_TIMER_PERIODIC_INTERVAL, Timeout.Infinite);
                    }
                    catch (ObjectDisposedException)
                    { }
                }
            });
        }

        #endregion

        #region IDisposable

        bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _enableLogging = false; //turn off logging

            _cleanupTimer?.Dispose();

            StopChannel();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion

        #region private

        /// <summary>
        /// Build a connection string targeting the specific database.
        /// </summary>
        /// <param name="databaseName">The database to connect to.</param>
        /// <returns>A connection string with the Database parameter appended.</returns>
        private string GetConnectionString(string databaseName)
        {
            return _connectionString + $" Database={databaseName};";
        }

        private void StartNewChannel(int maxQueueSize)
        {
            ChannelWriter<LogEntry>? existingChannelWriter = _channelWriter;

            //start new channel and consumer thread
            BoundedChannelOptions options = new BoundedChannelOptions(maxQueueSize);
            options.SingleWriter = true;
            options.SingleReader = true;
            options.FullMode = BoundedChannelFullMode.DropWrite;

            _channel = Channel.CreateBounded<LogEntry>(options);
            _channelWriter = _channel.Writer;
            ChannelReader<LogEntry> channelReader = _channel.Reader;

            _consumerThread = new Thread(async delegate ()
            {
                try
                {
                    List<LogEntry> logs = new List<LogEntry>(BULK_INSERT_COUNT);
                    StringBuilder sb = new StringBuilder(4096);

                    while (!_disposed && await channelReader.WaitToReadAsync())
                    {
                        while (!_disposed && (logs.Count < BULK_INSERT_COUNT) && channelReader.TryRead(out LogEntry log))
                        {
                            logs.Add(log);
                        }

                        if (logs.Count < 1)
                            continue;

                        await BulkInsertLogsAsync(logs, sb);

                        logs.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _dnsServer?.WriteLog(ex);
                }
            });

            _consumerThread.Name = GetType().Name;
            _consumerThread.IsBackground = true;
            _consumerThread.Start();

            //complete old channel to stop its consumer thread
            existingChannelWriter?.TryComplete();
        }

        private void StopChannel()
        {
            _channel?.Writer.TryComplete();
        }

        /// <summary>
        /// Bulk insert a batch of log entries into the PostgreSQL database.
        /// Uses a multi-value INSERT statement with parameterized queries.
        /// </summary>
        /// <param name="logs">The list of log entries to insert.</param>
        /// <param name="sb">A reusable StringBuilder for building the INSERT statement.</param>
        private async Task BulkInsertLogsAsync(List<LogEntry> logs, StringBuilder sb)
        {
            try
            {
                await using (NpgsqlConnection connection = new NpgsqlConnection(GetConnectionString(_databaseName!)))
                {
                    await connection.OpenAsync();

                    await using (NpgsqlCommand command = connection.CreateCommand())
                    {
                        sb.Length = 0;
                        sb.Append("INSERT INTO dns_logs (server, \"timestamp\", client_ip, protocol, response_type, response_rtt, rcode, qname, qtype, qclass, answer) VALUES ");

                        for (int i = 0; i < logs.Count; i++)
                        {
                            if (i == 0)
                                sb.Append($"(@server{i}, @timestamp{i}, @client_ip{i}, @protocol{i}, @response_type{i}, @response_rtt{i}, @rcode{i}, @qname{i}, @qtype{i}, @qclass{i}, @answer{i})");
                            else
                                sb.Append($", (@server{i}, @timestamp{i}, @client_ip{i}, @protocol{i}, @response_type{i}, @response_rtt{i}, @rcode{i}, @qname{i}, @qtype{i}, @qclass{i}, @answer{i})");
                        }
                        command.CommandText = sb.ToString();

                        for (int i = 0; i < logs.Count; i++)
                        {
                            LogEntry log = logs[i];

                            NpgsqlParameter paramServer = new NpgsqlParameter("@server" + i, NpgsqlDbType.Varchar);
                            NpgsqlParameter paramTimestamp = new NpgsqlParameter("@timestamp" + i, NpgsqlDbType.Timestamp);
                            NpgsqlParameter paramClientIp = new NpgsqlParameter("@client_ip" + i, NpgsqlDbType.Varchar);
                            NpgsqlParameter paramProtocol = new NpgsqlParameter("@protocol" + i, NpgsqlDbType.Smallint);
                            NpgsqlParameter paramResponseType = new NpgsqlParameter("@response_type" + i, NpgsqlDbType.Smallint);
                            NpgsqlParameter paramResponseRtt = new NpgsqlParameter("@response_rtt" + i, NpgsqlDbType.Double);
                            NpgsqlParameter paramRcode = new NpgsqlParameter("@rcode" + i, NpgsqlDbType.Smallint);
                            NpgsqlParameter paramQname = new NpgsqlParameter("@qname" + i, NpgsqlDbType.Varchar);
                            NpgsqlParameter paramQtype = new NpgsqlParameter("@qtype" + i, NpgsqlDbType.Smallint);
                            NpgsqlParameter paramQclass = new NpgsqlParameter("@qclass" + i, NpgsqlDbType.Smallint);
                            NpgsqlParameter paramAnswer = new NpgsqlParameter("@answer" + i, NpgsqlDbType.Text);

                            command.Parameters.Add(paramServer);
                            command.Parameters.Add(paramTimestamp);
                            command.Parameters.Add(paramClientIp);
                            command.Parameters.Add(paramProtocol);
                            command.Parameters.Add(paramResponseType);
                            command.Parameters.Add(paramResponseRtt);
                            command.Parameters.Add(paramRcode);
                            command.Parameters.Add(paramQname);
                            command.Parameters.Add(paramQtype);
                            command.Parameters.Add(paramQclass);
                            command.Parameters.Add(paramAnswer);

                            paramServer.Value = _dnsServer?.ServerDomain ?? (object)DBNull.Value;
                            paramTimestamp.Value = log.Timestamp;
                            paramClientIp.Value = log.RemoteEP.Address.ToString();
                            paramProtocol.Value = (short)log.Protocol;

                            DnsServerResponseType responseType;

                            if (log.Response.Tag == null)
                                responseType = DnsServerResponseType.Recursive;
                            else
                                responseType = (DnsServerResponseType)log.Response.Tag;

                            paramResponseType.Value = (short)responseType;

                            if ((responseType == DnsServerResponseType.Recursive) && (log.Response.Metadata is not null))
                                paramResponseRtt.Value = log.Response.Metadata.RoundTripTime;
                            else
                                paramResponseRtt.Value = DBNull.Value;

                            paramRcode.Value = (short)log.Response.RCODE;

                            if (log.Request.Question.Count > 0)
                            {
                                DnsQuestionRecord query = log.Request.Question[0];

                                paramQname.Value = query.Name.ToLowerInvariant();
                                paramQtype.Value = (short)query.Type;
                                paramQclass.Value = (short)query.Class;
                            }
                            else
                            {
                                paramQname.Value = DBNull.Value;
                                paramQtype.Value = DBNull.Value;
                                paramQclass.Value = DBNull.Value;
                            }

                            if (log.Response.Answer.Count == 0)
                            {
                                paramAnswer.Value = DBNull.Value;
                            }
                            else if ((log.Response.Answer.Count > 2) && log.Response.IsZoneTransfer)
                            {
                                paramAnswer.Value = "[ZONE TRANSFER]";
                            }
                            else
                            {
                                string? answer = null;

                                foreach (DnsResourceRecord record in log.Response.Answer)
                                {
                                    if (answer is null)
                                        answer = record.Type.ToString() + " " + record.RDATA.ToString();
                                    else
                                        answer += ", " + record.Type.ToString() + " " + record.RDATA.ToString();
                                }

                                if (answer?.Length > 4000)
                                    answer = answer.Substring(0, 4000);

                                paramAnswer.Value = answer ?? (object)DBNull.Value;
                            }
                        }

                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _dnsServer?.WriteLog(ex);

                await Task.Delay(BULK_INSERT_ERROR_DELAY);
            }
        }

        #endregion

        #region public

        /// <summary>
        /// Initialize the application: parse configuration, create the database
        /// and table if they don't exist, create indexes, and start the logging channel.
        /// </summary>
        /// <param name="dnsServer">The DNS server instance providing server context.</param>
        /// <param name="config">JSON configuration string from dnsApp.config.</param>
        public async Task InitializeAsync(IDnsServer dnsServer, string config)
        {
            try
            {
                _dnsServer = dnsServer;

                using JsonDocument jsonDocument = JsonDocument.Parse(config);
                JsonElement jsonConfig = jsonDocument.RootElement;

                bool enableLogging = jsonConfig.GetPropertyValue("enableLogging", false);
                int maxQueueSize = jsonConfig.GetPropertyValue("maxQueueSize", 1000000);
                _maxLogDays = jsonConfig.GetPropertyValue("maxLogDays", 0);
                _maxLogRecords = jsonConfig.GetPropertyValue("maxLogRecords", 0);
                _databaseName = jsonConfig.GetPropertyValue("databaseName", "DnsQueryLogs");
                _connectionString = jsonConfig.GetPropertyValue("connectionString", null);

                if (_connectionString is null)
                    throw new Exception("Please specify a valid connection string in 'connectionString' parameter.");

                if (_connectionString.Replace(" ", "").Contains("Database=", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("The 'connectionString' parameter must not define 'Database'. Configure the 'databaseName' parameter above instead.");

                if (!_connectionString.TrimEnd().EndsWith(';'))
                    _connectionString += ";";

                async Task ApplyConfig()
                {
                    if (enableLogging)
                    {
                        //create database if it does not exist
                        await using (NpgsqlConnection connection = new NpgsqlConnection(GetConnectionString("postgres")))
                        {
                            await connection.OpenAsync();

                            bool dbExists;

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbname;";
                                command.Parameters.AddWithValue("@dbname", _databaseName!);

                                dbExists = await command.ExecuteScalarAsync() is not null;
                            }

                            if (!dbExists)
                            {
                                await using (NpgsqlCommand command = connection.CreateCommand())
                                {
                                    //database names cannot be parameterized in CREATE DATABASE
                                    command.CommandText = $"CREATE DATABASE \"{_databaseName}\";";

                                    await command.ExecuteNonQueryAsync();
                                }
                            }
                        }

                        //create table and indexes
                        await using (NpgsqlConnection connection = new NpgsqlConnection(GetConnectionString(_databaseName!)))
                        {
                            await connection.OpenAsync();

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = @"
CREATE TABLE IF NOT EXISTS dns_logs
(
    dlid SERIAL PRIMARY KEY,
    server VARCHAR(255),
    ""timestamp"" TIMESTAMP NOT NULL,
    client_ip VARCHAR(39) NOT NULL,
    protocol SMALLINT NOT NULL,
    response_type SMALLINT NOT NULL,
    response_rtt DOUBLE PRECISION,
    rcode SMALLINT NOT NULL,
    qname VARCHAR(255),
    qtype SMALLINT,
    qclass SMALLINT,
    answer TEXT
);
";

                                await command.ExecuteNonQueryAsync();
                            }

                            //add server column if upgrading from older schema
                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "ALTER TABLE dns_logs ADD COLUMN IF NOT EXISTS server VARCHAR(255);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_server ON dns_logs (server);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_timestamp ON dns_logs (\"timestamp\");";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_client_ip ON dns_logs (client_ip);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_protocol ON dns_logs (protocol);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_response_type ON dns_logs (response_type);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_rcode ON dns_logs (rcode);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_qname ON dns_logs (qname);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_qtype ON dns_logs (qtype);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_qclass ON dns_logs (qclass);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_timestamp_client_ip ON dns_logs (\"timestamp\", client_ip);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_timestamp_qname ON dns_logs (\"timestamp\", qname);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_client_qname ON dns_logs (client_ip, qname);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_query ON dns_logs (qname, qtype);";

                                await command.ExecuteNonQueryAsync();
                            }

                            await using (NpgsqlCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE INDEX IF NOT EXISTS index_all ON dns_logs (server, \"timestamp\", client_ip, protocol, response_type, rcode, qname, qtype, qclass);";

                                await command.ExecuteNonQueryAsync();
                            }
                        }

                        if (!_enableLogging || (_maxQueueSize != maxQueueSize))
                            StartNewChannel(maxQueueSize);
                    }
                    else
                    {
                        StopChannel();
                    }

                    _enableLogging = enableLogging;
                    _maxQueueSize = maxQueueSize;

                    if ((_maxLogDays > 0) || (_maxLogRecords > 0))
                        _cleanupTimer.Change(CLEAN_UP_TIMER_INITIAL_INTERVAL, Timeout.Infinite);
                    else
                        _cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                if (_isStartupInit)
                {
                    //this is the first time this app is being initialized
                    ThreadPool.QueueUserWorkItem(async delegate (object? state)
                    {
                        try
                        {
                            const int MAX_RETRIES = 20;
                            const int RETRY_DELAY = 30000; //30 seconds
                            int retryCount = 0;

                            while (true)
                            {
                                try
                                {
                                    await ApplyConfig();
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    if (ex is not NpgsqlException)
                                    {
                                        _dnsServer?.WriteLog(ex);
                                        return;
                                    }

                                    retryCount++;

                                    if (retryCount < MAX_RETRIES)
                                    {
                                        _dnsServer?.WriteLog($"Failed to connect to the PostgreSQL server. Please check the app config and make sure the database server is online. Retrying in {RETRY_DELAY / 1000} seconds... (Attempt {retryCount})");
                                        _dnsServer?.WriteLog(ex);

                                        await Task.Delay(RETRY_DELAY);
                                    }
                                    else
                                    {
                                        _dnsServer?.WriteLog($"Failed to connect to the PostgreSQL server after {retryCount} retries. Please check the app config and make sure the database server is online.");
                                        _dnsServer?.WriteLog(ex);
                                        return;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _dnsServer?.WriteLog(ex);
                        }
                    });
                }
                else
                {
                    //init via API call
                    await ApplyConfig();
                }
            }
            finally
            {
                _isStartupInit = false; //reset flag
            }
        }

        /// <summary>
        /// Insert a DNS query log entry into the async channel for bulk insertion.
        /// </summary>
        /// <param name="timestamp">The time the query was received.</param>
        /// <param name="request">The DNS request datagram.</param>
        /// <param name="remoteEP">The remote endpoint (client IP and port).</param>
        /// <param name="protocol">The transport protocol used (UDP, TCP, DoT, DoH, DoQ).</param>
        /// <param name="response">The DNS response datagram.</param>
        /// <returns>A completed task.</returns>
        public Task InsertLogAsync(DateTime timestamp, DnsDatagram request, IPEndPoint remoteEP, DnsTransportProtocol protocol, DnsDatagram response)
        {
            if (_enableLogging)
                _channelWriter?.TryWrite(new LogEntry(timestamp, request, remoteEP, protocol, response));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Query the DNS logs from the PostgreSQL database with filtering and pagination.
        /// </summary>
        /// <param name="pageNumber">The page number to retrieve (1-based).</param>
        /// <param name="entriesPerPage">The number of entries per page.</param>
        /// <param name="descendingOrder">Whether to sort results in descending order.</param>
        /// <param name="start">Optional start timestamp filter.</param>
        /// <param name="end">Optional end timestamp filter.</param>
        /// <param name="clientIpAddress">Optional client IP address filter.</param>
        /// <param name="protocol">Optional transport protocol filter.</param>
        /// <param name="responseType">Optional response type filter.</param>
        /// <param name="rcode">Optional DNS response code filter.</param>
        /// <param name="qname">Optional query name filter (supports * wildcard).</param>
        /// <param name="qtype">Optional query type filter.</param>
        /// <param name="qclass">Optional query class filter.</param>
        /// <returns>A page of DNS log entries matching the specified criteria.</returns>
        public async Task<DnsLogPage> QueryLogsAsync(long pageNumber, int entriesPerPage, bool descendingOrder, DateTime? start, DateTime? end, IPAddress clientIpAddress, DnsTransportProtocol? protocol, DnsServerResponseType? responseType, DnsResponseCode? rcode, string qname, DnsResourceRecordType? qtype, DnsClass? qclass)
        {
            if (pageNumber == 0)
                pageNumber = 1;

            if (qname is not null)
                qname = qname.ToLowerInvariant();

            string whereClause = $"server = '{_dnsServer?.ServerDomain}' AND ";

            if (start is not null)
                whereClause += "\"timestamp\" >= @start AND ";

            if (end is not null)
                whereClause += "\"timestamp\" <= @end AND ";

            if (clientIpAddress is not null)
                whereClause += "client_ip = @client_ip AND ";

            if (protocol is not null)
                whereClause += "protocol = @protocol AND ";

            if (responseType is not null)
                whereClause += "response_type = @response_type AND ";

            if (rcode is not null)
                whereClause += "rcode = @rcode AND ";

            if (qname is not null)
            {
                if (qname.Contains('*'))
                {
                    whereClause += "qname LIKE @qname AND ";
                    qname = qname.Replace("*", "%");
                }
                else
                {
                    whereClause += "qname = @qname AND ";
                }
            }

            if (qtype is not null)
                whereClause += "qtype = @qtype AND ";

            if (qclass is not null)
                whereClause += "qclass = @qclass AND ";

            if (!string.IsNullOrEmpty(whereClause))
                whereClause = whereClause.Substring(0, whereClause.Length - 5);

            await using (NpgsqlConnection connection = new NpgsqlConnection(GetConnectionString(_databaseName!)))
            {
                await connection.OpenAsync();

                //find total entries
                long totalEntries;

                await using (NpgsqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Count(*) FROM dns_logs" + (string.IsNullOrEmpty(whereClause) ? ";" : " WHERE " + whereClause + ";");

                    if (start is not null)
                        command.Parameters.AddWithValue("@start", start);

                    if (end is not null)
                        command.Parameters.AddWithValue("@end", end);

                    if (clientIpAddress is not null)
                        command.Parameters.AddWithValue("@client_ip", clientIpAddress.ToString());

                    if (protocol is not null)
                        command.Parameters.AddWithValue("@protocol", (short)protocol);

                    if (responseType is not null)
                        command.Parameters.AddWithValue("@response_type", (short)responseType);

                    if (rcode is not null)
                        command.Parameters.AddWithValue("@rcode", (short)rcode);

                    if (qname is not null)
                        command.Parameters.AddWithValue("@qname", qname);

                    if (qtype is not null)
                        command.Parameters.AddWithValue("@qtype", (short)qtype);

                    if (qclass is not null)
                        command.Parameters.AddWithValue("@qclass", (short)qclass);

                    totalEntries = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
                }

                long totalPages = (totalEntries / entriesPerPage) + (totalEntries % entriesPerPage > 0 ? 1 : 0);

                if ((pageNumber > totalPages) || (pageNumber < 0))
                    pageNumber = totalPages;

                long endRowNum;
                long startRowNum;

                if (descendingOrder)
                {
                    endRowNum = totalEntries - ((pageNumber - 1) * entriesPerPage);
                    startRowNum = endRowNum - entriesPerPage;
                }
                else
                {
                    endRowNum = pageNumber * entriesPerPage;
                    startRowNum = endRowNum - entriesPerPage;
                }

                List<DnsLogEntry> entries = new List<DnsLogEntry>(entriesPerPage);

                await using (NpgsqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT * FROM (
    SELECT
        ROW_NUMBER() OVER (
            ORDER BY dlid
        ) row_num,
        ""timestamp"",
        client_ip,
        protocol,
        response_type,
        response_rtt,
        rcode,
        qname,
        qtype,
        qclass,
        answer
    FROM
        dns_logs
" + (string.IsNullOrEmpty(whereClause) ? "" : "WHERE " + whereClause) + @"
) t
WHERE
    row_num > @start_row_num AND row_num <= @end_row_num
ORDER BY row_num" + (descendingOrder ? " DESC" : "");

                    command.Parameters.AddWithValue("@start_row_num", startRowNum);
                    command.Parameters.AddWithValue("@end_row_num", endRowNum);

                    if (start is not null)
                        command.Parameters.AddWithValue("@start", start);

                    if (end is not null)
                        command.Parameters.AddWithValue("@end", end);

                    if (clientIpAddress is not null)
                        command.Parameters.AddWithValue("@client_ip", clientIpAddress.ToString());

                    if (protocol is not null)
                        command.Parameters.AddWithValue("@protocol", (short)protocol);

                    if (responseType is not null)
                        command.Parameters.AddWithValue("@response_type", (short)responseType);

                    if (rcode is not null)
                        command.Parameters.AddWithValue("@rcode", (short)rcode);

                    if (qname is not null)
                        command.Parameters.AddWithValue("@qname", qname);

                    if (qtype is not null)
                        command.Parameters.AddWithValue("@qtype", (short)qtype);

                    if (qclass is not null)
                        command.Parameters.AddWithValue("@qclass", (short)qclass);

                    await using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            double? responseRtt;

                            if (reader.IsDBNull(5))
                                responseRtt = null;
                            else
                                responseRtt = reader.GetDouble(5);

                            DnsQuestionRecord? question;

                            if (reader.IsDBNull(7))
                                question = null;
                            else
                                question = new DnsQuestionRecord(reader.GetString(7), (DnsResourceRecordType)reader.GetInt16(8), (DnsClass)reader.GetInt16(9), false);

                            string? answer;

                            if (reader.IsDBNull(10))
                                answer = null;
                            else
                                answer = reader.GetString(10);

                            entries.Add(new DnsLogEntry(reader.GetInt64(0), reader.GetDateTime(1), IPAddress.Parse(reader.GetString(2)), (DnsTransportProtocol)reader.GetInt16(3), (DnsServerResponseType)reader.GetInt16(4), responseRtt, (DnsResponseCode)reader.GetInt16(6), question, answer));
                        }
                    }
                }

                return new DnsLogPage(pageNumber, totalPages, totalEntries, entries);
            }
        }

        #endregion

        #region properties

        public string Description
        { get { return "Logs all incoming DNS requests and their responses in a PostgreSQL database that can be queried from the DNS Server web console."; } }

        #endregion

        /// <summary>
        /// Internal structure representing a pending log entry in the async channel,
        /// waiting to be bulk-inserted into the database.
        /// </summary>
        readonly struct LogEntry
        {
            #region variables

            public readonly DateTime Timestamp;
            public readonly DnsDatagram Request;
            public readonly IPEndPoint RemoteEP;
            public readonly DnsTransportProtocol Protocol;
            public readonly DnsDatagram Response;

            #endregion

            #region constructor

            public LogEntry(DateTime timestamp, DnsDatagram request, IPEndPoint remoteEP, DnsTransportProtocol protocol, DnsDatagram response)
            {
                Timestamp = timestamp;
                Request = request;
                RemoteEP = remoteEP;
                Protocol = protocol;
                Response = response;
            }

            #endregion
        }
    }
}
