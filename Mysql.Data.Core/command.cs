// Copyright � 2004, 2016, Oracle and/or its affiliates. All rights reserved.
//
// MySQL Connector/NET is licensed under the terms of the GPLv2
// <http://www.gnu.org/licenses/old-licenses/gpl-2.0.html>, like most 
// MySQL Connectors. There are special exceptions to the terms and 
// conditions of the GPLv2 as it is applied to this software, see the 
// FLOSS License Exception
// <http://www.mysql.com/about/legal/licensing/foss-exception.html>.
//
// This program is free software; you can redistribute it and/or modify 
// it under the terms of the GNU General Public License as published 
// by the Free Software Foundation; version 2 of the License.
//
// This program is distributed in the hope that it will be useful, but 
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY 
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License 
// for more details.
//
// You should have received a copy of the GNU General Public License along 
// with this program; if not, write to the Free Software Foundation, Inc., 
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using System;
using System.IO;
using System.Data;
using System.Data.Common;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MySql.Data.Common;

namespace MySql.Data.MySqlClient
{
    /// <include file='docs/mysqlcommand.xml' path='docs/ClassSummary/*'/> 
    public sealed partial class MySqlCommand : DbCommand, IDisposable
    {
        MySqlConnection connection;
        string cmdText;
        private PreparableStatement statement;
        private int commandTimeout;
        private bool resetSqlSelect;
        CommandTimer commandTimer;
        private bool useDefaultTimeout;
        private static List<string> keywords = null;
        private bool disposed = false;

        /// <include file='docs/mysqlcommand.xml' path='docs/ctor1/*'/>
        public MySqlCommand()
        {
            CommandType = System.Data.CommandType.Text;
            Parameters = new MySqlParameterCollection(this);
            cmdText = String.Empty;
            useDefaultTimeout = true;
            UpdatedRowSource = UpdateRowSource.Both;
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/ctor2/*'/>
        public MySqlCommand(string cmdText)
          : this()
        {
            CommandText = cmdText;
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/ctor3/*'/>
        public MySqlCommand(string cmdText, MySqlConnection connection)
          : this(cmdText)
        {
            Connection = connection;
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/ctor4/*'/>
        public MySqlCommand(string cmdText, MySqlConnection connection, MySqlTransaction transaction)
          : this(cmdText, connection)
        {
            Transaction = transaction;
        }

#region Destructor
        ~MySqlCommand()
        {
            Dispose(false);
        }
#endregion

#region Properties


        /// <include file='docs/mysqlcommand.xml' path='docs/LastInseredId/*'/>
        [Browsable(false)]
        public Int64 LastInsertedId { get; internal set; }

        /// <include file='docs/mysqlcommand.xml' path='docs/CommandText/*'/>
        [Category("Data")]
        [Description("Command text to execute")]
        public override string CommandText
        {
            get { return cmdText; }
            set
            {
                cmdText = value ?? string.Empty;
                statement = null;
                BatchableCommandText = null;
                if (cmdText != null && cmdText.EndsWith("DEFAULT VALUES", StringComparison.OrdinalIgnoreCase))
                {
                    cmdText = cmdText.Substring(0, cmdText.Length - 14);
                    cmdText = cmdText + "() VALUES ()";
                }
            }
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/CommandTimeout/*'/>
        [Category("Misc")]
        [Description("Time to wait for command to execute")]
        [DefaultValue(30)]
        public override int CommandTimeout
        {
            get { return useDefaultTimeout ? 30 : commandTimeout; }
            set
            {
                if (commandTimeout < 0)
                    Throw(new ArgumentException("Command timeout must not be negative"));

                // Timeout in milliseconds should not exceed maximum for 32 bit
                // signed integer (~24 days), because underlying driver (and streams)
                // use milliseconds expressed ints for timeout values.
                // Hence, truncate the value.
                int timeout = Math.Min(value, Int32.MaxValue / 1000);
                if (timeout != value)
                {
                    MySqlTrace.LogWarning(connection.ServerThread,
                    "Command timeout value too large ("
                    + value + " seconds). Changed to max. possible value ("
                    + timeout + " seconds)");
                }
                commandTimeout = timeout;
                useDefaultTimeout = false;
            }
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/CommandType/*'/>
        [Category("Data")]
        public override CommandType CommandType { get; set; }

        /// <include file='docs/mysqlcommand.xml' path='docs/IsPrepared/*'/>
        [Browsable(false)]
        public bool IsPrepared => statement != null && statement.IsPrepared;

        /// <include file='docs/mysqlcommand.xml' path='docs/Connection/*'/>
        [Category("Behavior")]
        [Description("Connection used by the command")]
        public new MySqlConnection Connection
        {
            get { return connection; }
            set
            {
                /*
                * The connection is associated with the transaction
                * so set the transaction object to return a null reference if the connection 
                * is reset.
                */
                if (connection != value)
                    Transaction = null;

                connection = value;

                // if the user has not already set the command timeout, then
                // take the default from the connection
                if (connection == null) return;

                if (useDefaultTimeout)
                {
                    commandTimeout = (int)connection.Settings.DefaultCommandTimeout;
                    useDefaultTimeout = false;
                }

                EnableCaching = connection.Settings.TableCaching;
                CacheAge = connection.Settings.DefaultTableCacheAge;
            }
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/Parameters/*'/>
        [Category("Data")]
        [Description("The parameters collection")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public new MySqlParameterCollection Parameters { get; }


        /// <include file='docs/mysqlcommand.xml' path='docs/Transaction/*'/>
        [Browsable(false)]
        public new MySqlTransaction Transaction { get; set; }

        public bool EnableCaching { get; set; }

        public int CacheAge { get; set; }

        internal List<MySqlCommand> Batch { get; private set; }

        internal bool Canceled { get; private set; }

        internal string BatchableCommandText { get; private set; }

        internal bool InternallyCreated { get; set; }

        /// <summary>
        /// Gets or sets how command results are applied to the DataRow when used by the 
        /// Update method of the DbDataAdapter. 
        /// </summary>
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the command object should be visible in a Windows Form Designer control. 
        /// </summary>
        [Browsable(false)]
        public override bool DesignTimeVisible { get; set; }

        protected override DbParameter CreateDbParameter()
        {
            return new MySqlParameter();
        }

        protected override DbConnection DbConnection
        {
            get { return Connection; }
            set { Connection = (MySqlConnection)value; }
        }

        protected override DbParameterCollection DbParameterCollection
        {
            get { return Parameters; }
        }

        protected override DbTransaction DbTransaction
        {
            get { return Transaction; }
            set { Transaction = (MySqlTransaction)value; }
        }

#endregion

#region Methods

        /// <summary>
        /// Attempts to cancel the execution of a currently active command
        /// </summary>
        /// <remarks>
        /// Cancelling a currently active query only works with MySQL versions 5.0.0 and higher.
        /// </remarks>
        public override void Cancel()
        {
            connection.CancelQuery(connection.ConnectionTimeout);
            Canceled = true;
        }

        /// <summary>
        /// Creates a new instance of a <see cref="MySqlParameter"/> object.
        /// </summary>
        /// <remarks>
        /// This method is a strongly-typed version of <see cref="System.Data.IDbCommand.CreateParameter"/>.
        /// </remarks>
        /// <returns>A <see cref="MySqlParameter"/> object.</returns>
        /// 
        public new MySqlParameter CreateParameter()
        {
            return (MySqlParameter)CreateDbParameter();
        }

        /// <summary>
        /// Check the connection to make sure
        ///		- it is open
        ///		- it is not currently being used by a reader
        ///		- and we have the right version of MySQL for the requested command type
        /// </summary>
        private void CheckState()
        {
            // There must be a valid and open connection.
            if (connection == null)
                Throw(new InvalidOperationException("Connection must be valid and open."));

            if (connection.State != ConnectionState.Open && !connection.SoftClosed)
                Throw(new InvalidOperationException("Connection must be valid and open."));

            // Data readers have to be closed first
            if (connection.IsInUse && !this.InternallyCreated)
                Throw(new MySqlException("There is already an open DataReader associated with this Connection which must be closed first."));
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/ExecuteNonQuery/*'/>
        public override int ExecuteNonQuery()
        {
            int records = -1;

            // give our interceptors a shot at it first
            if (connection?.commandInterceptor != null && connection.commandInterceptor.ExecuteNonQuery(CommandText, ref records))
                return records;

            // ok, none of our interceptors handled this so we default
            using (MySqlDataReader reader = ExecuteReader())
            {
                reader.Close();
                return reader.RecordsAffected;
            }
        }

        internal void ClearCommandTimer()
        {
            if (commandTimer == null) return;

            commandTimer.Dispose();
            commandTimer = null;
        }

        internal void Close(MySqlDataReader reader)
        {
            statement?.Close(reader);
            ResetSqlSelectLimit();
            if (statement != null)
                connection?.driver?.CloseQuery(connection, statement.StatementId);
            ClearCommandTimer();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            return ExecuteReader(behavior);
        }

        /// <summary>
        /// Reset reader to null, to avoid "There is already an open data reader"
        /// on the next ExecuteReader(). Used in error handling scenarios.
        /// </summary>
        private void ResetReader()
        {
            if (connection?.Reader == null) return;

            connection.Reader.Close();
            connection.Reader = null;
        }

        /// <summary>
        /// Reset SQL_SELECT_LIMIT that could have been modified by CommandBehavior.
        /// </summary>
        internal void ResetSqlSelectLimit()
        {
            // if we are supposed to reset the sql select limit, do that here
            if (!resetSqlSelect) return;

            resetSqlSelect = false;
            MySqlCommand command = new MySqlCommand("SET SQL_SELECT_LIMIT=DEFAULT", connection);
            command.InternallyCreated = true;
            command.ExecuteNonQuery();
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/ExecuteReader/*'/>
        public new MySqlDataReader ExecuteReader()
        {
            return ExecuteReader(CommandBehavior.Default);
        }


        /// <include file='docs/mysqlcommand.xml' path='docs/ExecuteReader1/*'/>
        public new MySqlDataReader ExecuteReader(CommandBehavior behavior)
        {

            // give our interceptors a shot at it first
            MySqlDataReader interceptedReader = null;
            if (connection?.commandInterceptor != null && connection.commandInterceptor.ExecuteReader(CommandText, behavior, ref interceptedReader))
                return interceptedReader;

            // interceptors didn't handle this so we fall through
            bool success = false;
            CheckState();
            Driver driver = connection.driver;

            cmdText = cmdText.Trim();
            if (String.IsNullOrEmpty(cmdText))
                Throw(new InvalidOperationException(Resources.CommandTextNotInitialized));

            string sql = cmdText.Trim(';');

            lock (driver)
            {

                // We have to recheck that there is no reader, after we got the lock
                if (connection.Reader != null)
                {
                    Throw(new MySqlException(Resources.DataReaderOpen));
                }

                commandTimer = new CommandTimer(connection, CommandTimeout);

                LastInsertedId = -1;

                if (CommandType == CommandType.TableDirect)
                    sql = "SELECT * FROM " + sql;
                else if (CommandType == CommandType.Text)
                {
                    // validates single word statetment (maybe is a stored procedure call)
                    if (sql.IndexOf(" ") == -1)
                    {
                        if (AddCallStatement(sql))
                            sql = "call " + sql;
                    }
                }

                // if we are on a replicated connection, we are only allow readonly statements
                if (connection.Settings.Replication && !InternallyCreated)
                    EnsureCommandIsReadOnly(sql);

                if (statement == null || !statement.IsPrepared)
                {
                    if (CommandType == CommandType.StoredProcedure)
                        statement = new StoredProcedure(this, sql);
                    else
                        statement = new PreparableStatement(this, sql);
                }

                // stored procs are the only statement type that need do anything during resolve
                statement.Resolve(false);

                // Now that we have completed our resolve step, we can handle our
                // command behaviors
                HandleCommandBehaviors(behavior);

                try
                {
                    MySqlDataReader reader = new MySqlDataReader(this, statement, behavior);
                    connection.Reader = reader;
                    Canceled = false;
                    // execute the statement
                    statement.Execute();
                    // wait for data to return
                    reader.NextResult();
                    success = true;
                    return reader;
                }
                catch (TimeoutException tex)
                {
                    connection.HandleTimeoutOrThreadAbort(tex);
                    throw; //unreached
                }
                catch (IOException ioex)
                {
                    connection.Abort(); // Closes connection without returning it to the pool
                    throw new MySqlException(Resources.FatalErrorDuringExecute, ioex);
                }
                catch (MySqlException ex)
                {

                    if (ex.InnerException is TimeoutException)
                        throw; // already handled

                    try
                    {
                        ResetReader();
                        ResetSqlSelectLimit();
                    }
                    catch (Exception)
                    {
                        // Reset SqlLimit did not work, connection is hosed.
                        Connection.Abort();
                        throw new MySqlException(ex.Message, true, ex);
                    }

                    // if we caught an exception because of a cancel, then just return null
                    if (ex.IsQueryAborted)
                        return null;
                    if (ex.IsFatal)
                        Connection.Close();
                    if (ex.Number == 0)
                        throw new MySqlException(Resources.FatalErrorDuringExecute, ex);
                    throw;
                }
                finally
                {
                    if (connection != null)
                    {
                        if (connection.Reader == null)
                        {
                            // Something went seriously wrong,  and reader would not
                            // be able to clear timeout on closing.
                            // So we clear timeout here.
                            ClearCommandTimer();
                        }
                        if (!success)
                        {
                            // ExecuteReader failed.Close Reader and set to null to 
                            // prevent subsequent errors with DataReaderOpen
                            ResetReader();
                        }
                    }
                }
            }
        }

        private void EnsureCommandIsReadOnly(string sql)
        {
            sql = StringUtility.ToLowerInvariant(sql);
            if (!sql.StartsWith("select") && !sql.StartsWith("show"))
                Throw(new MySqlException(Resources.ReplicatedConnectionsAllowOnlyReadonlyStatements));
            if (sql.EndsWith("for update") || sql.EndsWith("lock in share mode"))
                Throw(new MySqlException(Resources.ReplicatedConnectionsAllowOnlyReadonlyStatements));
        }

        private bool IsReadOnlyCommand(string sql)
        {
            sql = sql.ToLower();
            return (sql.StartsWith("select") || sql.StartsWith("show"))
              && !(sql.EndsWith("for update") || sql.EndsWith("lock in share mode"));
        }


        /// <include file='docs/mysqlcommand.xml' path='docs/ExecuteScalar/*'/>
        public override object ExecuteScalar()
        {
            LastInsertedId = -1;
            object val = null;

            // give our interceptors a shot at it first
            if (connection != null &&
                connection.commandInterceptor.ExecuteScalar(CommandText, ref val))
                return val;

            using (MySqlDataReader reader = ExecuteReader())
            {
                if (reader.Read())
                    val = reader.GetValue(0);
            }

            return val;
        }

        private void HandleCommandBehaviors(CommandBehavior behavior)
        {
            if ((behavior & CommandBehavior.SchemaOnly) != 0)
            {
                new MySqlCommand("SET SQL_SELECT_LIMIT=0", connection).ExecuteNonQuery();
                resetSqlSelect = true;
            }
            else if ((behavior & CommandBehavior.SingleRow) != 0)
            {
                new MySqlCommand("SET SQL_SELECT_LIMIT=1", connection).ExecuteNonQuery();
                resetSqlSelect = true;
            }
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/Prepare2/*'/>
        private void Prepare(int cursorPageSize)
        {
            using (new CommandTimer(Connection, CommandTimeout))
            {
                // if the length of the command text is zero, then just return
                string psSQL = CommandText;
                if (psSQL == null ||
                     psSQL.Trim().Length == 0)
                    return;

                statement = CommandType == CommandType.StoredProcedure ? new StoredProcedure(this, CommandText) : new PreparableStatement(this, CommandText);

                statement.Resolve(true);
                statement.Prepare();
            }
        }

        /// <include file='docs/mysqlcommand.xml' path='docs/Prepare/*'/>
        public override void Prepare()
        {
            if (connection == null)
                Throw(new InvalidOperationException("The connection property has not been set."));
            if (connection.State != ConnectionState.Open)
                Throw(new InvalidOperationException("The connection is not open."));
            if (connection.Settings.IgnorePrepare)
                return;

            Prepare(0);
        }
    #endregion

    #region Private Methods

    internal long EstimatedSize()
        {
            return CommandText.Length + Parameters.Cast<MySqlParameter>().Sum(parameter => parameter.EstimatedSize());
        }

        /// <summary>
        /// Verifies if a query is valid even if it has not spaces or is a stored procedure call
        /// </summary>
        /// <param name="query">Query to validate</param>
        /// <returns>If it is necessary to add call statement</returns>
        private bool AddCallStatement(string query)
        {
            if (string.IsNullOrEmpty(query)) return false;

            string keyword = query.ToUpper();
            int indexChar = keyword.IndexOfAny(new char[] { '(', '"', '@', '\'', '`' });
            if (indexChar > 0)
                keyword = keyword.Substring(0, indexChar);

            if (keywords == null)
                keywords = new List<string>(Utils.ReadResource("keywords.txt").Replace("\r", "").Split('\n'));

            return !keywords.Contains(keyword);
        }

    #endregion


    #region Batching support

    internal void AddToBatch(MySqlCommand command)
        {
            if (Batch == null)
                Batch = new List<MySqlCommand>();
            Batch.Add(command);
        }

        internal string GetCommandTextForBatching()
        {
            if (BatchableCommandText == null)
            {
                // if the command starts with insert and is "simple" enough, then
                // we can use the multi-value form of insert
                if (String.Compare(CommandText.Substring(0, 6), "INSERT", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    MySqlCommand cmd = new MySqlCommand("SELECT @@sql_mode", Connection);
                    string sql_mode = StringUtility.ToUpperInvariant(cmd.ExecuteScalar().ToString());
                    MySqlTokenizer tokenizer = new MySqlTokenizer(CommandText);
                    tokenizer.AnsiQuotes = sql_mode.IndexOf("ANSI_QUOTES") != -1;
                    tokenizer.BackslashEscapes = sql_mode.IndexOf("NO_BACKSLASH_ESCAPES") == -1;
                    string token = StringUtility.ToLowerInvariant(tokenizer.NextToken());
                    while (token != null)
                    {
                        if (StringUtility.ToUpperInvariant(token) == "VALUES" &&
                            !tokenizer.Quoted)
                        {
                            token = tokenizer.NextToken();
                            Debug.Assert(token == "(");

                            // find matching right paren, and ensure that parens 
                            // are balanced.
                            int openParenCount = 1;
                            while (token != null)
                            {
                                BatchableCommandText += token;
                                token = tokenizer.NextToken();

                                if (token == "(")
                                    openParenCount++;
                                else if (token == ")")
                                    openParenCount--;

                                if (openParenCount == 0)
                                    break;
                            }

                            if (token != null)
                                BatchableCommandText += token;
                            token = tokenizer.NextToken();
                            if (token != null && (token == "," ||
                                StringUtility.ToUpperInvariant(token) == "ON"))
                            {
                                BatchableCommandText = null;
                                break;
                            }
                        }
                        token = tokenizer.NextToken();
                    }
                }
                // Otherwise use the command verbatim
                else BatchableCommandText = CommandText;
            }

            return BatchableCommandText;
        }

#endregion

        // This method is used to throw all exceptions from this class.  
        private void Throw(Exception ex)
        {
            connection?.Throw(ex);
            throw ex;
        }

        public new void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (!disposing)
                return;

            if (statement != null && statement.IsPrepared)
                statement.CloseStatement();

            ResetReader();

            base.Dispose(disposing);

            disposed = true;
        }
    }
}

