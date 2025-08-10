
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DynamicExpresso;
using plot_twist_back_end.Messages;

// Simple in-memory table: name, column names, and rows (each row is a dictionary column->value).
public class InMemoryTable
{
    public string Name { get; }
    public string[] Columns { get; }
    public List<Dictionary<string, object?>> Rows { get; } = new();

    public InMemoryTable(string name, string[] columns)
    {
        Name = name;
        Columns = columns;
    }
}

// CrossDataSetSelections: stores InMemoryTable instances, generates runtime POCO types
// that match each table schema (double/string + bool isSelected), compiles predicates with
// DynamicExpresso and evaluates matches between tables.
public class CrossDataSetSelections
{
    private readonly object _lock = new();
    private readonly Dictionary<string, InMemoryTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    // cache: deterministic signature -> generated CLR type
    private readonly Dictionary<string, Type> _typeCache = new();

    // mapping from generated type full name -> original column name -> generated property name
    private readonly Dictionary<string, Dictionary<string, string>> _propMaps = new();

    // store inferred CLR types per table for inspection
    private readonly Dictionary<string, Dictionary<string, Type>> _columnTypeMap = new(StringComparer.OrdinalIgnoreCase);

    // single dynamic module for runtime types
    private readonly ModuleBuilder _moduleBuilder;

    public CrossDataSetSelections()
    {
        var asmName = new AssemblyName("RuntimeTableTypes_" + Guid.NewGuid().ToString("N"));
        var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        _moduleBuilder = asm.DefineDynamicModule(asmName.Name!);
        Log($"ctor: module {_moduleBuilder.ScopeName}");
    }
    
    // logging helper that includes file, member and line number
    private static void Log(string msg,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        // Console.WriteLine($"[{Path.GetFileName(file)}:{line} {member}] {msg}");
    }

    // Convert JsonElement to CLR value. Numbers -> double, strings -> string, true/false -> bool, null -> null.
    private static object? ConvertJsonElement(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => je.GetRawText()
        };
    }

    // If any non-null numeric present -> double, otherwise -> string
    private static Type InferColumnType(IEnumerable<object?> values)
    {
        var styles = NumberStyles.Float | NumberStyles.AllowThousands;
        var cultures = new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture };

        foreach (var v in values)
        {
            if (v is null) continue;

            var t = v.GetType();
            // handle boxed numeric types
            if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)
                || t == typeof(int) || t == typeof(long) || t == typeof(short)
                || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort))
            {
                return typeof(double);
            }

            // handle numeric strings
            if (v is string s)
            {
                s = s.Trim();
                if (s.Length == 0) continue;

                foreach (var ci in cultures)
                {
                    if (double.TryParse(s, styles, ci, out _))
                        return typeof(double);
                }
            }
        }

        return typeof(string);
    }

    // Helper that emits a private field and simple get/set methods for a property on `tb`.
    // propertyName is used as-is (we don't sanitize), but we ensure uniqueness when generating.
    private static void CreateAutoProperty(TypeBuilder tb, string propertyName, Type propType)
    {
        // define a backing field
        var field = tb.DefineField("_" + propertyName, propType, FieldAttributes.Private);
        var prop = tb.DefineProperty(propertyName, PropertyAttributes.None, propType, Type.EmptyTypes);

        // getter
        var get = tb.DefineMethod("get_" + propertyName,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            propType, Type.EmptyTypes);
        var ig = get.GetILGenerator();
        ig.Emit(OpCodes.Ldarg_0);
        ig.Emit(OpCodes.Ldfld, field);
        ig.Emit(OpCodes.Ret);

        // setter
        var set = tb.DefineMethod("set_" + propertyName,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(void), new[] { propType });
        var iset = set.GetILGenerator();
        iset.Emit(OpCodes.Ldarg_0);
        iset.Emit(OpCodes.Ldarg_1);
        iset.Emit(OpCodes.Stfld, field);
        iset.Emit(OpCodes.Ret);

        prop.SetGetMethod(get);
        prop.SetSetMethod(set);
    }

    // Create or reuse a CLR type that matches the table's inferred schema.
    // Steps:
    //  - collect values for each column (and synthetic originalIndexPosition/isSelected)
    //  - infer types (double|string, isSelected=bool)
    //  - build deterministic signature (sorted keys)
    //  - generate runtime type if missing, cache type and property map
    private Type GetOrCreateTypeForTable(InMemoryTable table)
    {
        // gather column values (case-insensitive keys)
        var columnValues = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in table.Columns) columnValues[c] = new List<object?>();

        // ensure synthetic columns
        foreach (var synth in new[] { "originalIndexPosition", "isSelected" })
            if (!columnValues.ContainsKey(synth)) columnValues[synth] = new List<object?>();

        // populate values row-by-row
        foreach (var row in table.Rows)
        {
            foreach (var k in columnValues.Keys.ToList())
            {
                row.TryGetValue(k, out var v);
                columnValues[k].Add(v);
            }
        }

        // infer types; keep isSelected as bool explicitly
        var inferred = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in columnValues)
        {
            if (string.Equals(kv.Key, "isSelected", StringComparison.OrdinalIgnoreCase))
                inferred[kv.Key] = typeof(bool);
            else
                inferred[kv.Key] = InferColumnType(kv.Value);
        }

        // deterministic signature: sort by column name
        var signature = table.Name + ":" + string.Join(",", inferred.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.FullName));

        // return cached type if exists
        if (_typeCache.TryGetValue(signature, out var cached)) return cached;

        lock (_lock)
        {
            if (_typeCache.TryGetValue(signature, out cached)) return cached;

            // create a unique type name
            var typeName = "RtTable_" + Guid.NewGuid().ToString("N");
            var tb = _moduleBuilder.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

            var propMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // iterate in sorted order for deterministic property order
            foreach (var kv in inferred.OrderBy(kv => kv.Key))
            {
                var orig = kv.Key;

                // use original column name as property name (no sanitization)
                var propName = orig;

                // ensure uniqueness if duplicate column names appear (append suffix)
                var baseName = propName;
                int suffix = 1;
                while (!used.Add(propName))
                {
                    propName = baseName + "_" + suffix++;
                }

                propMap[orig] = propName;
                CreateAutoProperty(tb, propName, kv.Value);
            }

            var genType = tb.CreateTypeInfo()!.AsType();
            _typeCache[signature] = genType;
            _propMaps[genType.FullName!] = propMap;

            // store column types for inspection
            _columnTypeMap[table.Name] = inferred.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            Log($"Created runtime type {genType.FullName} for table '{table.Name}' with cols: {string.Join(",", inferred.Keys)}");
            return genType;
        }
    }

    // Add a DataSetInfo; convert JsonElements into CLR values and store rows.
    // table is static except for isSelected toggles as you indicated.
    public void AddDataset(DataSetInfo dataset)
    {
        lock (_lock)
        {
            if (_tables.ContainsKey(dataset.name))
                throw new InvalidOperationException($"Dataset already exists: {dataset.name}");

            var cols = dataset.fields.ToArray();
            var table = new InMemoryTable(dataset.name, cols);

            for (int r = 0; r < dataset.table.rows.Count; r++)
            {
                var srcRow = dataset.table.rows[r];
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < cols.Length; i++)
                {
                    object? val = null;
                    if (i < srcRow.Count) val = ConvertJsonElement(srcRow[i]);
                    dict[cols[i]] = val;
                }

                dict["originalIndexPosition"] = r;
                dict["isSelected"] = true;
                table.Rows.Add(dict);
            }

            _tables[dataset.name] = table;
            Log($"AddDataset: added '{dataset.name}' rows={table.Rows.Count}");

            // create runtime type now so we can inspect inferred types and prop maps; log the PrintTable output
            try
            {
                var _ = GetOrCreateTypeForTable(table);
                Log(PrintTable(dataset.name, 8));
                Console.WriteLine(PrintTable(dataset.name, 3));
            }
            catch (Exception ex)
            {
                Log($"AddDataset: type creation failed for '{dataset.name}' -> {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Update per-row isSelected flags using originalIndexPosition when present, otherwise by row index.
    public void UpdateSelectionsFromTable(string tableName, List<bool> selections)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException($"Table '{tableName}' not found.");

        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (row.TryGetValue("originalIndexPosition", out var idxObj) && idxObj is int originalIdx)
            {
                row["isSelected"] = (originalIdx >= 0 && originalIdx < selections.Count) ? selections[originalIdx] : false;
            }
            else
            {
                row["isSelected"] = (i < selections.Count) ? selections[i] : false;
            }
        }

        Log($"UpdateSelectionsFromTable: updated '{tableName}' with {selections.Count} selection flags");
    }
    
    // invoker
    private Func<object, object, bool> CreateInvoker(Delegate del)
    {
        var invokeMi = del.GetType().GetMethod("Invoke")
                       ?? throw new InvalidOperationException("Delegate has no Invoke method");

        var pars = invokeMi.GetParameters();
        if (pars.Length != 2)
            throw new InvalidOperationException("Predicate delegate must take two parameters.");

        var pAType = pars[0].ParameterType;
        var pBType = pars[1].ParameterType;

        var pa = Expression.Parameter(typeof(object), "a");
        var pb = Expression.Parameter(typeof(object), "b");

        var convertedA = Expression.Convert(pa, pAType);
        var convertedB = Expression.Convert(pb, pBType);

        var delConst = Expression.Constant(del);
        var invoke = Expression.Invoke(delConst, convertedA, convertedB);
        var body = Expression.Convert(invoke, typeof(bool));

        var lambdaExp = Expression.Lambda<Func<object, object, bool>>(body, pa, pb);
        return lambdaExp.Compile();
    }
    
    // compile safely to Delegate?
    private Delegate? TryCompileLambda(object lambdaObj, Type aType, Type bType, out string? err)
    {
        err = null;
        try
        {
            var lambdaType = lambdaObj.GetType();
            var compileGeneric = lambdaType.GetMethods()
                .FirstOrDefault(m => m.Name == "Compile" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

            Delegate? del = null;

            if (compileGeneric != null)
            {
                var delegateType = typeof(Func<,,>).MakeGenericType(aType, bType, typeof(bool));
                var make = compileGeneric.MakeGenericMethod(delegateType);
                var compiled = make.Invoke(lambdaObj, null);
                del = compiled as Delegate;
                if (del == null)
                {
                    err = "Compile<T>() did not return a Delegate";
                    return null;
                }
            }
            else
            {
                var compileNonGeneric = lambdaType.GetMethod("Compile", Type.EmptyTypes);
                if (compileNonGeneric == null)
                {
                    err = "Lambda object exposes no Compile() method";
                    return null;
                }
                var compiled = compileNonGeneric.Invoke(lambdaObj, null);
                del = compiled as Delegate;
                if (del == null)
                {
                    err = "Compile() did not return a Delegate";
                    return null;
                }
            }

            // ✅ Correctly validate the delegate's public signature
            var invokeMi = del.GetType().GetMethod("Invoke")
                ?? throw new InvalidOperationException("Delegate has no Invoke method");
            var invokeParams = invokeMi.GetParameters();
            if (invokeMi.ReturnType != typeof(bool) || invokeParams.Length != 2)
            {
                err = "Compiled delegate has wrong signature";
                return null;
            }

            return del;
        }
        catch (TargetInvocationException tie)
        {
            err = tie.InnerException?.Message ?? tie.Message;
            return null;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return null;
        }
    }
    
    // TryEvaluateMatches: for each B row return whether any selected A row satisfies predicate(X,Y).
    // predicateString expected in "X.ColumnA < Y.ColumnB" style (no quoting).
    public bool TryEvaluateMatches(string tableA, string tableB, string predicateString, out List<bool> result)
    {
        result = new List<bool>();

        if (!_tables.TryGetValue(tableA, out var tA) || !_tables.TryGetValue(tableB, out var tB))
        {
            Log($"TryEvaluateMatches: missing table(s) '{tableA}' or '{tableB}'");
            return false;
        }

        var typeA = GetOrCreateTypeForTable(tA);
        var typeB = GetOrCreateTypeForTable(tB);

        if (!_propMaps.TryGetValue(typeA.FullName!, out var mapA) || !_propMaps.TryGetValue(typeB.FullName!, out var mapB))
        {
            Log("TryEvaluateMatches: missing prop maps for generated types");
            return false;
        }

        Log($"TryEvaluateMatches: compiling predicate '{predicateString}' for A='{tableA}' B='{tableB}'");
        var lambda = CompilePredicate(typeA, typeB, predicateString, out string? compileErr);
        if (lambda == null)
        {
            Log($"Predicate compile failed: {compileErr}");
            return false;
        }
        
        Delegate? compiledDelegate = TryCompileLambda(lambda, typeA, typeB, out string? compileError);
        if (compiledDelegate is null)
        {
            Log($"Predicate compile failed: {compileError}");
            return false;
        }

        var evalFunc = CreateInvoker(compiledDelegate!);

        int bCount = tB.Rows.Count;

        var selectedAIndices = Enumerable.Range(0, tA.Rows.Count)
            .Where(i => tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb)
            .ToArray();

        var aSelectedInstances = selectedAIndices
            .Select(i => CreateTypedInstance(typeA, tA, i))
            .ToArray();

        var bInstances = new object[bCount];
        for (int i = 0; i < bCount; i++)
            bInstances[i] = CreateTypedInstance(typeB, tB, i);

        Log($"TryEvaluateMatches: selected A rows count = {selectedAIndices.Length}, B rows = {bCount}");

        var output = new bool[bCount];

        var localLogs = new ThreadLocal<List<string>>(() => new List<string>(), true);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

        Parallel.ForEach(Partitioner.Create(0, bCount), po, range =>
        {
            var buf = localLogs.Value;
            for (int ib = range.Item1; ib < range.Item2; ib++)
            {
                var yInst = bInstances[ib];
                bool hasMatch = false;

                for (int k = 0; k < aSelectedInstances.Length; k++)
                {
                    var xInst = aSelectedInstances[k];
                    int ia = selectedAIndices[k];

                    try
                    {
                        if (evalFunc(xInst, yInst))
                        {
                            hasMatch = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        buf.Add($"Evaluation exception for A#{ia} B#{ib}: {ex.GetType().Name} {ex.Message}");
                    }
                }

                output[ib] = hasMatch;
            }
        });

        // flush logs in arbitrary but safe order
        foreach (var l in localLogs.Values)
            foreach (var msg in l)
                Log(msg);
        localLogs.Dispose();

        result = output.ToList();
        Log($"TryEvaluateMatches: finished, result count={result.Count}");
        return true;
    }

    // Bidirectional evaluation: result for B rows, selectedFrom for A rows.
    public bool TryEvaluateMatchesBidirectional(
        string tableA,
        string tableB,
        string predicateString,
        out List<bool> result,
        out List<bool> selectedFrom)
    {
        result = new List<bool>();
        selectedFrom = new List<bool>();

        if (!_tables.TryGetValue(tableA, out var tA) || !_tables.TryGetValue(tableB, out var tB))
        {
            Log($"TryEvaluateMatchesBidirectional: missing table(s) '{tableA}' or '{tableB}'");
            return false;
        }

        var typeA = GetOrCreateTypeForTable(tA);
        var typeB = GetOrCreateTypeForTable(tB);

        Log($"TryEvaluateMatchesBidirectional: compiling predicate '{predicateString}'");
        var lambda = CompilePredicate(typeA, typeB, predicateString, out string? compileErr);
        if (lambda == null)
        {
            Log($"Predicate compile failed: {compileErr}");
            return false;
        }
        
        Delegate? compiledDelegate = TryCompileLambda(lambda, typeA, typeB, out string? compileError);
        if (compiledDelegate is null)
        {
            Log($"Predicate compile failed: {compileError}");
            return false;
        }
        
        var evalFunc = CreateInvoker(compiledDelegate!);

        int aCount = tA.Rows.Count;
        int bCount = tB.Rows.Count;

        var selArr = new bool[aCount];
        var resArr = new bool[bCount];

        var selectedAIndices = Enumerable.Range(0, aCount)
            .Where(i => tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb)
            .ToArray();

        var selectedBIndices = Enumerable.Range(0, bCount)
            .Where(j => tB.Rows[j].TryGetValue("isSelected", out var s) && s is bool sb && sb)
            .ToArray();

        var aSelectedInstances = selectedAIndices.Select(i => CreateTypedInstance(typeA, tA, i)).ToArray();
        var bSelectedInstances = selectedBIndices.Select(j => CreateTypedInstance(typeB, tB, j)).ToArray();

        var bInstances = new object[bCount];
        for (int j = 0; j < bCount; j++)
            bInstances[j] = CreateTypedInstance(typeB, tB, j);

        Log($"Bidirectional: selectedA={selectedAIndices.Length}, selectedB={selectedBIndices.Length}");

        var localLogs = new ThreadLocal<List<string>>(() => new List<string>(), true);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

        // For each selected A -> selArr
        if (aSelectedInstances.Length > 0 && bSelectedInstances.Length > 0)
        {
            Parallel.ForEach(Partitioner.Create(0, aSelectedInstances.Length), po, range =>
            {
                var buf = localLogs.Value;
                for (int k = range.Item1; k < range.Item2; k++)
                {
                    int ia = selectedAIndices[k];
                    var xInst = aSelectedInstances[k];
                    bool matched = false;

                    for (int jbIdx = 0; jbIdx < bSelectedInstances.Length; jbIdx++)
                    {
                        int jb = selectedBIndices[jbIdx];
                        var yInst = bSelectedInstances[jbIdx];
                        try
                        {
                            if (evalFunc(xInst, yInst))
                            {
                                matched = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            buf.Add($"Bidirectional eval exception A#{ia} B#{jb}: {ex.GetType().Name} {ex.Message}");
                        }
                    }

                    selArr[ia] = matched;
                }
            });
        }

        // For each B -> resArr
        if (bCount > 0 && aSelectedInstances.Length > 0)
        {
            Parallel.ForEach(Partitioner.Create(0, bCount), po, range =>
            {
                var buf = localLogs.Value;
                for (int jb = range.Item1; jb < range.Item2; jb++)
                {
                    var yInst = bInstances[jb];
                    bool hasMatch = false;

                    for (int k = 0; k < aSelectedInstances.Length; k++)
                    {
                        int ia = selectedAIndices[k];
                        var xInst = aSelectedInstances[k];
                        try
                        {
                            if (evalFunc(xInst, yInst))
                            {
                                hasMatch = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            buf.Add($"Bidirectional eval exception A#{ia} B#{jb}: {ex.GetType().Name} {ex.Message}");
                        }
                    }

                    resArr[jb] = hasMatch;
                }
            });
        }

        foreach (var l in localLogs.Values)
            foreach (var msg in l)
                Log(msg);
        localLogs.Dispose();

        result = resArr.ToList();
        selectedFrom = selArr.ToList();
        Log($"TryEvaluateMatchesBidirectional: finished resultCount={result.Count} selectedFromCount={selectedFrom.Count}");
        return true;
    }

    // Compile the predicate string into a DynamicExpresso Lambda expecting parameters X (typeA) and Y (typeB).
    // We expose Math and Convert and set default number type to double.
    private Lambda? CompilePredicate(Type typeA, Type typeB, string predicate, out string? error)
    {
        error = null;
        try
        {
            var interpreter = new Interpreter()
                .Reference(typeof(Math))
                .Reference(typeof(Convert))
                .SetDefaultNumberType(DefaultNumberType.Double);

            // register numeric helpers
            interpreter.SetFunction("Sqrt", (Func<double, double>)Math.Sqrt);
            interpreter.SetFunction("Pow", (Func<double, double, double>)Math.Pow);

            // parse predicate expecting parameters named X and Y
            var lambda = interpreter.Parse(predicate, new Parameter("X", typeA), new Parameter("Y", typeB));
            return lambda;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            Log($"CompilePredicate: exception {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Create an instance of the runtime-generated type and populate its properties from the row dictionary.
    // Conversion: target double -> Convert.ToDouble if possible; string -> ToString(); bool -> cast if bool else false.
    private object CreateTypedInstance(Type generatedType, InMemoryTable table, int rowIndex)
    {
        var inst = Activator.CreateInstance(generatedType)!;

        if (!_propMaps.TryGetValue(generatedType.FullName!, out var map))
        {
            throw new InvalidOperationException($"Missing property map for {generatedType.FullName}");
        }

        var row = table.Rows[rowIndex];

        foreach (var kv in map)
        {
            var original = kv.Key;
            var propName = kv.Value;
            var prop = generatedType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                Log($"CreateTypedInstance: property '{propName}' not found on type '{generatedType.FullName}'");
                continue;
            }

            row.TryGetValue(original, out var val);

            if (val == null)
            {
                // leave defaults for value types, null for reference types
                prop.SetValue(inst, prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null);
                continue;
            }

            var target = prop.PropertyType;

            try
            {
                object? setVal;
                if (target == typeof(double))
                {
                    if (val is double d) setVal = d;
                    else if (val is IConvertible) setVal = Convert.ToDouble(val);
                    else { setVal = double.TryParse(val.ToString(), out var parsed) ? parsed : (object?)null; }
                }
                else if (target == typeof(string))
                {
                    setVal = val.ToString();
                }
                else if (target == typeof(bool))
                {
                    setVal = (val is bool b) ? b : false;
                }
                else
                {
                    setVal = Convert.ChangeType(val, target);
                }

                if (setVal != null)
                    prop.SetValue(inst, setVal);
            }
            catch (Exception ex)
            {
                // log conversion failures for each property/row
                Log($"CreateTypedInstance: conversion failed table='{table.Name}' row={rowIndex} col='{original}' -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        return inst;
    }

    // Print the full table contents and the inferred CLR types for each column (human readable).
    public string PrintTable(string tableName, int maxRows = 50)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            return $"Table '{tableName}' not found.";

        // ensure we computed types
        try { GetOrCreateTypeForTable(table); } catch { /* ignore */ }

        _columnTypeMap.TryGetValue(table.Name, out var colTypes);
        colTypes ??= table.Columns.ToDictionary(c => c, c => typeof(string), StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Table: {table.Name}");
        sb.AppendLine($"Columns ({table.Columns.Length}):");
        foreach (var c in table.Columns)
        {
            var t = colTypes.TryGetValue(c, out var tt) ? tt : null;
            sb.AppendLine($"  - {c} : {(t?.Name ?? "unknown")}");
        }

        // synthetic columns
        if (colTypes.ContainsKey("originalIndexPosition")) sb.AppendLine("  - originalIndexPosition : int");
        if (colTypes.ContainsKey("isSelected")) sb.AppendLine("  - isSelected : bool");

        sb.AppendLine();
        sb.AppendLine($"Rows: {table.Rows.Count} (showing up to {maxRows})");
        var show = Math.Min(maxRows, table.Rows.Count);
        for (int i = 0; i < show; i++)
        {
            var row = table.Rows[i];
            sb.Append($"[{i}] ");
            var parts = new List<string>();
            foreach (var c in table.Columns)
            {
                row.TryGetValue(c, out var v);
                parts.Add($"{c}={(v is null ? "null" : v.ToString())}");
            }
            if (row.TryGetValue("originalIndexPosition", out var orig)) parts.Add($"originalIndexPosition={orig}");
            if (row.TryGetValue("isSelected", out var sel)) parts.Add($"isSelected={sel}");
            sb.AppendLine(string.Join(", ", parts));
        }
        if (show < table.Rows.Count) sb.AppendLine($"... ({table.Rows.Count - show} more rows)");

        var dump = sb.ToString();
        Log($"PrintTable: produced {Math.Min(table.Rows.Count, maxRows)} rows for '{tableName}'");
        return dump;
    }
}
