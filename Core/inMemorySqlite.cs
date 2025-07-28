using System.Data;
using System.Threading;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using plot_twist_back_end.Messages;

public class InMemorySqlite
{
    private readonly SqliteConnection _conn;  
    private readonly object _txLock = new object();
    private SqliteTransaction _currentTransaction;
    
    public InMemorySqlite()
    {
        // ":memory:" stored in RAM only
        // "Cache=Shared"  multiple connections share the same memory DB
        _conn = new SqliteConnection("Data Source=:memory:;Cache=Shared");
        _conn.Open();
    }
    
    public void AddDataset(DataSetInfo dataset)
    {
        // wait for any active transaction to complete
        lock (_txLock)
        {
            while (_currentTransaction != null)
                Monitor.Wait(_txLock);
        }

        // quote table name
        var quotedTable = $"\"{dataset.name}\"";

        // existing fields + two extras
        var cols = string.Join(", ", dataset.fields.Select(f => $"\"{f}\" TEXT"))
                   + ", \"originalIndexPosition\" INTEGER, \"isSelected\" BOOLEAN";

        // 1) Create table
        using var createCmd = _conn.CreateCommand();
        createCmd.CommandText = $"CREATE TABLE {quotedTable} ({cols});";
        createCmd.ExecuteNonQuery();

        // 2) Create index on originalIndexPosition for faster lookups
        using var indexCmd = _conn.CreateCommand();
        indexCmd.CommandText =
            $"CREATE INDEX IF NOT EXISTS \"idx_{dataset.name}_origPos\"" +
            $" ON {quotedTable}(\"originalIndexPosition\");";
        indexCmd.ExecuteNonQuery();
        
        // 2b) Create index on isSelected for efficient filtering
        using var isSelectedIndexCmd = _conn.CreateCommand();
        isSelectedIndexCmd.CommandText =
            $"CREATE INDEX IF NOT EXISTS \"idx_{dataset.name}_isSelected\"" +
            $" ON {quotedTable}(\"isSelected\");";
        isSelectedIndexCmd.ExecuteNonQuery();
        
        // build INSERT lists: fields + extras
        var colList = string.Join(", ", dataset.fields.Select(f => $"\"{f}\""))
                      + ", \"originalIndexPosition\", \"isSelected\"";

        // parameters: one per field, then two extras
        var paramList = string.Join(", ", dataset.fields.Select((f, i) => $"@p{i}"))
                        + ", @originalIndexPosition, @isSelected";

        // 3) Prepare INSERT command
        using var insertCmd = _conn.CreateCommand();
        insertCmd.CommandText =
            $"INSERT INTO {quotedTable} ({colList}) VALUES ({paramList});";

        // add parameters for fields
        for (int i = 0; i < dataset.fields.Length; i++)
        {
            insertCmd.Parameters.Add(new SqliteParameter($"@p{i}", DbType.String));
        }
        // add parameters for new columns
        insertCmd.Parameters.Add(new SqliteParameter("@originalIndexPosition", DbType.Int32));
        insertCmd.Parameters.Add(new SqliteParameter("@isSelected", DbType.Boolean));

        // 4) Fill the table
        using var tx = _conn.BeginTransaction();
        insertCmd.Transaction = tx;

        for (int rowIdx = 0; rowIdx < dataset.table.rows.Count; rowIdx++)
        {
            var row = dataset.table.rows[rowIdx];

            // set field values
            for (int i = 0; i < row.Count; i++)
            {
                var je = row[i];
                insertCmd.Parameters[i].Value =
                    je.ValueKind switch
                    {
                        JsonValueKind.String => je.GetString(),
                        JsonValueKind.Number => je.GetRawText(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => DBNull.Value,
                        _ => je.GetRawText()
                    };
            }

            // original index and isSelected
            insertCmd.Parameters["@originalIndexPosition"].Value = rowIdx;
            insertCmd.Parameters["@isSelected"].Value = true;

            insertCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
    
    // public void AddDataset(DataSetInfo dataset)
    // {
    //     // wait for any active transaction to complete
    //     lock (_txLock)
    //     {
    //         while (_currentTransaction != null)
    //             Monitor.Wait(_txLock);
    //     }
    //
    //     // quote table name
    //     var quotedTable = $"\"{dataset.name}\"";
    //
    //     // existing fields + two extras
    //     var cols = string.Join(", ", dataset.fields.Select(f => $"\"{f}\" TEXT")) +
    //                ", \"originalIndexPosition\" INTEGER, \"isSelected\" BOOLEAN";
    //
    //     using var createCmd = _conn.CreateCommand();
    //     createCmd.CommandText = $"CREATE TABLE {quotedTable} ({cols});";
    //     createCmd.ExecuteNonQuery();
    //
    //     // build INSERT lists: fields + extras
    //     var colList = string.Join(", ", dataset.fields.Select(f => $"\"{f}\"")) +
    //                   ", \"originalIndexPosition\", \"isSelected\"";
    //
    //     // parameters: one per field, then two extras
    //     var paramList = string.Join(", ", dataset.fields.Select((f, i) => $"@p{i}")) +
    //                     ", @originalIndexPosition, @isSelected";
    //
    //     using var insertCmd = _conn.CreateCommand();
    //     insertCmd.CommandText =
    //         $"INSERT INTO {quotedTable} ({colList}) VALUES ({paramList});";
    //
    //     // add parameters for fields
    //     for (int i = 0; i < dataset.fields.Length; i++)
    //     {
    //         insertCmd.Parameters.Add(new SqliteParameter($"@p{i}", DbType.String));
    //     }
    //
    //     // add parameters for new columns
    //     insertCmd.Parameters.Add(new SqliteParameter("@originalIndexPosition", DbType.Int32));
    //     insertCmd.Parameters.Add(new SqliteParameter("@isSelected", DbType.Boolean));
    //
    //     // fill the table
    //     for (int rowIdx = 0; rowIdx < dataset.table.rows.Count; rowIdx++)
    //     {
    //         var row = dataset.table.rows[rowIdx];
    //
    //         // set field values
    //         for (int i = 0; i < row.Count; i++)
    //         {
    //             var je = row[i];
    //             insertCmd.Parameters[i].Value =
    //                 je.ValueKind switch
    //                 {
    //                     JsonValueKind.String => je.GetString(),
    //                     JsonValueKind.Number => je.GetRawText(),
    //                     JsonValueKind.True => true,
    //                     JsonValueKind.False => false,
    //                     JsonValueKind.Null => DBNull.Value,
    //                     _ => je.GetRawText()
    //                 };
    //         }
    //
    //         // original index and isSelected
    //         insertCmd.Parameters["@originalIndexPosition"].Value = rowIdx;
    //         insertCmd.Parameters["@isSelected"].Value = true;
    //
    //         insertCmd.ExecuteNonQuery();
    //     }
    // }
    //
    // public bool TryEvaluateMatches(
    //     string tableA,
    //     string tableB,
    //     string predicateString,
    //     out List<bool> result)
    // {
    //     // wait for any active transaction
    //     lock (_txLock)
    //     {
    //         while (_currentTransaction != null)
    //             Monitor.Wait(_txLock);
    //     }
    //
    //     result = new List<bool>();
    //
    //     var qA = $"\"{tableA}\"";
    //     var qB = $"\"{tableB}\"";
    //
    //     var sql = $@"
    //     SELECT
    //       Y.originalIndexPosition,
    //       EXISTS (
    //         SELECT 1
    //           FROM {qA} AS X
    //          WHERE X.isSelected = 1
    //            AND {predicateString}
    //       ) AS HasMatch
    //     FROM {qB} AS Y
    //     ORDER BY Y.originalIndexPosition;
    // ";
    //
    //     try
    //     {
    //         using var cmd = _conn.CreateCommand();
    //         cmd.CommandText = sql;
    //
    //         using var reader = cmd.ExecuteReader();
    //         while (reader.Read())
    //         {
    //             result.Add(!reader.IsDBNull(1) && reader.GetBoolean(1));
    //         }
    //         return true;
    //     }
    //     catch
    //     {
    //         result = new List<bool>();  
    //         return false;
    //     }
    // }
    
    public bool TryEvaluateMatches(
        string tableA,
        string tableB,
        string predicateString,
        out List<bool> result)
    {
        // wait for any active transaction
        lock (_txLock)
        {
            while (_currentTransaction != null)
                Monitor.Wait(_txLock);
        }

        result = new List<bool>();

        var qA = $"\"{tableA}\"";
        var qB = $"\"{tableB}\"";

        // moved predicate into the JOIN and aggregated
        var sql = $@"
    SELECT
      Y.originalIndexPosition,
      CASE WHEN COUNT(X.rowid) > 0 THEN 1 ELSE 0 END AS HasMatch
    FROM {qB} AS Y
    LEFT JOIN {qA} AS X
      ON X.isSelected = 1
     AND {predicateString}
    GROUP BY Y.originalIndexPosition
    ORDER BY Y.originalIndexPosition;
    ";

        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // SQLite returns 0/1 for the CASE; treat nonzero as true
                result.Add(reader.GetInt32(1) != 0);
            }
            return true;
        }
        catch
        {
            result = new List<bool>();  
            return false;
        }
    }

    
    public bool TryEvaluateMatchesBidirectional(
        string tableA,
        string tableB,
        string predicateString,
        out List<bool> result,
        out List<bool> selectedFrom)
    {
        // wait for any active transaction
        lock(_txLock)
        {
            while (_currentTransaction != null)
                Monitor.Wait(_txLock);
        }

        result = new List<bool>(); 
        selectedFrom = new List<bool>();

        var qA = $"\"{tableA}\"";
        var qB = $"\"{tableB}\"";

        var countA_sql = $"SELECT COUNT(*) FROM {qA};";

        var exists_sql = $@"
            SELECT
              Y.originalIndexPosition,
              EXISTS (
                SELECT 1
                  FROM {qA} AS X
                 WHERE X.isSelected = 1
                   AND {predicateString}
              ) AS HasMatch
            FROM {qB} AS Y
            ORDER BY Y.originalIndexPosition;
        ";

        var distinctX_sql = $@"
            SELECT DISTINCT X.originalIndexPosition
              FROM {qA} AS X
        INNER JOIN {qB} AS Y
                ON Y.isSelected = 1
               AND {predicateString};
        ";

        try
        {
            int countA;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = countA_sql;
                countA = Convert.ToInt32(cmd.ExecuteScalar());
            }
            selectedFrom = Enumerable.Repeat(false, countA).ToList();

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = exists_sql;
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    result.Add(!rdr.IsDBNull(1) && rdr.GetBoolean(1));
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = distinctX_sql;
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    if (!rdr.IsDBNull(0))
                    {
                        var idx = rdr.GetInt32(0);
                        if (idx >= 0 && idx < selectedFrom.Count)
                            selectedFrom[idx] = true;
                    }
                }
            }

            return true;
        }
        catch
        {
            result       = new List<bool>();
            selectedFrom = new List<bool>();
            return false;
        }
    }

    public void UpdateSelectionsFromTable(string tableName, List<bool> selections)
    {
        // 1) Serialize entire bool list into one JSON array
        var json = JsonSerializer.Serialize(selections);
        var qTable = $"\"{tableName}\"";

        // 2) One transaction, one command, no per‑row round‑trips
        using var tx  = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
        -- extract each element by its index
        UPDATE {qTable}
           SET isSelected = json_extract(@j, '$[' || originalIndexPosition || ']')
         WHERE originalIndexPosition < json_array_length(@j);
    ";
        var p = cmd.CreateParameter();
        p.ParameterName = "@j";
        p.DbType        = DbType.String;
        p.Value         = json;
        cmd.Parameters.Add(p);

        cmd.ExecuteNonQuery();
        tx.Commit();
    }
    
    // public void UpdateSelectionsFromTable(
    //     string tableName,
    //     List<bool> selections)
    // {
    //     var conn = this._conn;
    //     var qTable = $"\"{tableName}\"";
    //
    //     SqliteTransaction tx;
    //     lock (_txLock)
    //     {
    //         while (_currentTransaction != null)
    //             Monitor.Wait(_txLock);
    //
    //         _currentTransaction = conn.BeginTransaction();
    //         tx = _currentTransaction;
    //     }
    //
    //     using var cmd = conn.CreateCommand();
    //     cmd.Transaction = tx;
    //
    //     cmd.CommandText = $@"
    //         UPDATE {qTable}
    //            SET isSelected = @sel
    //          WHERE originalIndexPosition = @idx;
    //     ";
    //
    //     var pSel = cmd.CreateParameter();
    //     pSel.ParameterName = "@sel";
    //     pSel.DbType        = DbType.Boolean;
    //     cmd.Parameters.Add(pSel);
    //
    //     var pIdx = cmd.CreateParameter();
    //     pIdx.ParameterName = "@idx";
    //     pIdx.DbType        = DbType.Int32;
    //     cmd.Parameters.Add(pIdx);
    //
    //     for (int i = 0; i < selections.Count; i++)
    //     {
    //         pSel.Value = selections[i];
    //         pIdx.Value = i;
    //         cmd.ExecuteNonQuery();
    //     }
    //
    //     tx.Commit();
    //
    //     lock (_txLock)
    //     {
    //         _currentTransaction.Dispose();
    //         _currentTransaction = null;
    //         Monitor.PulseAll(_txLock);
    //     }
    // }
}
