using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamicExpresso;
using plot_twist_back_end.Messages;

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

    // existing parallelism toggle
    private bool _useParallelEvaluation = true;

    // NEW: independent precompute toggle (controls whether precompute runs at all)
    private bool _usePrecompute = true;

    // --- Match precompute cache (LRU capped) ---
    private readonly int _matchCacheCapacity = 20;
    private readonly LinkedList<(string Key, MatchCacheValue Value)> _matchCacheLru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, MatchCacheValue Value)>> _matchCacheMap
        = new(StringComparer.Ordinal);

    private static void Log(string msg,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        // Console.WriteLine($"[{Path.GetFileName(file)}:{line} {member}] {msg}");
    }

    public CrossDataSetSelections()
    {
        var asmName = new AssemblyName("RuntimeTableTypes_" + Guid.NewGuid().ToString("N"));
        var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        _moduleBuilder = asm.DefineDynamicModule(asmName.Name!);
        Log($"ctor: module {_moduleBuilder.ScopeName}");
    }

    // Match cache value: always has BToA, AToB is optional (null if not computed)
    private class MatchCacheValue
    {
        public List<int>[] BToA { get; }
        public List<int>[]? AToB { get; set; }
        public int ACount { get; }
        public int BCount { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public bool HasAToB => AToB != null;

        public MatchCacheValue(int aCount, int bCount, bool computeAToB)
        {
            ACount = aCount;
            BCount = bCount;
            BToA = new List<int>[bCount];
            for (int j = 0; j < bCount; j++) BToA[j] = new List<int>();

            if (computeAToB)
            {
                AToB = new List<int>[aCount];
                for (int i = 0; i < aCount; i++) AToB[i] = new List<int>();
            }
            else
            {
                AToB = null;
            }
        }
    }

    private static string MakeMatchCacheKey(string tableA, string tableB, string predicate)
    {
        return tableA + "|" + tableB + "|" + predicate;
    }

    private bool TryGetCachedMatches(string key, out MatchCacheValue? value)
    {
        lock (_lock)
        {
            if (_matchCacheMap.TryGetValue(key, out var node))
            {
                _matchCacheLru.Remove(node);
                _matchCacheLru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    private void AddCachedMatches(string key, MatchCacheValue value)
    {
        lock (_lock)
        {
            if (_matchCacheMap.TryGetValue(key, out var existing))
            {
                _matchCacheLru.Remove(existing);
                _matchCacheMap.Remove(key);
            }

            var node = new LinkedListNode<(string, MatchCacheValue)>((key, value));
            _matchCacheLru.AddFirst(node);
            _matchCacheMap[key] = node;

            if (_matchCacheMap.Count > _matchCacheCapacity)
            {
                var last = _matchCacheLru.Last!;
                _matchCacheMap.Remove(last.Value.Key);
                _matchCacheLru.RemoveLast();
            }
        }
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
            if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)
                || t == typeof(int) || t == typeof(long) || t == typeof(short)
                || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort))
            {
                return typeof(double);
            }

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

    private static void CreateAutoProperty(TypeBuilder tb, string propertyName, Type propType)
    {
        var field = tb.DefineField("_" + propertyName, propType, FieldAttributes.Private);
        var prop = tb.DefineProperty(propertyName, PropertyAttributes.None, propType, Type.EmptyTypes);

        var get = tb.DefineMethod("get_" + propertyName,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            propType, Type.EmptyTypes);
        var ig = get.GetILGenerator();
        ig.Emit(OpCodes.Ldarg_0);
        ig.Emit(OpCodes.Ldfld, field);
        ig.Emit(OpCodes.Ret);

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

    private Type GetOrCreateTypeForTable(InMemoryTable table)
    {
        var columnValues = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in table.Columns) columnValues[c] = new List<object?>();

        foreach (var synth in new[] { "originalIndexPosition", "isSelected" })
            if (!columnValues.ContainsKey(synth)) columnValues[synth] = new List<object?>();

        foreach (var row in table.Rows)
        {
            foreach (var k in columnValues.Keys.ToList())
            {
                row.TryGetValue(k, out var v);
                columnValues[k].Add(v);
            }
        }

        var inferred = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in columnValues)
        {
            if (string.Equals(kv.Key, "isSelected", StringComparison.OrdinalIgnoreCase))
                inferred[kv.Key] = typeof(bool);
            else
                inferred[kv.Key] = InferColumnType(kv.Value);
        }

        var signature = table.Name + ":" + string.Join(",", inferred.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.FullName));

        if (_typeCache.TryGetValue(signature, out var cached)) return cached;

        lock (_lock)
        {
            if (_typeCache.TryGetValue(signature, out cached)) return cached;

            var typeName = "RtTable_" + Guid.NewGuid().ToString("N");
            var tb = _moduleBuilder.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

            var propMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in inferred.OrderBy(kv => kv.Key))
            {
                var orig = kv.Key;

                // Replace spaces with underscores for property names
                var propName = orig.Replace(' ', '_');

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

            _columnTypeMap[table.Name] = inferred.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            Log($"Created runtime type {genType.FullName} for table '{table.Name}' with cols: {string.Join(",", inferred.Keys)}");
            return genType;
        }
    }

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

    public void UpdateSelectionsFromTable(string tableName, List<bool> selections)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException($"Table '{tableName}' not found.");

        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (row.TryGetValue("originalIndexPosition", out var idxObj) && idxObj is int originalIdx)
            {
                row["isSelected"] = (originalIdx >= 0 && originalIdx < selections.Count) && selections[originalIdx];
            }
            else
            {
                row["isSelected"] = (i < selections.Count) ? selections[i] : false;
            }
        }

        Log($"UpdateSelectionsFromTable: updated '{tableName}' with {selections.Count} selection flags");
    }

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

    private List<string> RunIndexedLoop(int count, Action<int, List<string>> body)
    {
        if (count <= 0) return new List<string>();

        if (_useParallelEvaluation)
        {
            var localLogs = new ThreadLocal<List<string>>(() => new List<string>(), true);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

            Parallel.ForEach(Partitioner.Create(0, count), po, range =>
            {
                var buf = localLogs.Value;
                for (int i = range.Item1; i < range.Item2; i++)
                    body(i, buf);
            });

            var merged = new List<string>();
            foreach (var l in localLogs.Values) merged.AddRange(l);
            localLogs.Dispose();
            return merged;
        }
        else
        {
            var seqLogs = new List<string>();
            for (int i = 0; i < count; i++)
                body(i, seqLogs);
            return seqLogs;
        }
    }

    // Precompute matches B->A and optionally A->B (tryBidirectional)

    private MatchCacheValue? PrecomputeMatches(Type typeA, Type typeB, InMemoryTable tA, InMemoryTable tB, string predicate, bool tryBidirectional, out string? error)
    {
        error = null;

        // GLOBAL precompute gate: if disabled, bail early
        if (!_usePrecompute)
        {
            error = "Precompute disabled by runtime flag";
            return null;
        }

        try
        {
            var lambda = CompilePredicate(typeA, typeB, predicate, out string? compileErr);
            if (lambda == null) { error = compileErr; return null; }

            var compiledDelegate = TryCompileLambda(lambda, typeA, typeB, out string? compileError);
            if (compiledDelegate == null) { error = compileError; return null; }

            var evalFunc = CreateInvoker(compiledDelegate);

            int aCount = tA.Rows.Count;
            int bCount = tB.Rows.Count;

            var cacheVal = new MatchCacheValue(aCount, bCount, computeAToB: tryBidirectional);

            // create typed instances once (now parallel when enabled)
            var aInstances = new object[aCount];
            var bInstances = new object[bCount];

            if (_useParallelEvaluation)
            {
                Parallel.For(0, aCount, i => aInstances[i] = CreateTypedInstance(typeA, tA, i));
                Parallel.For(0, bCount, j => bInstances[j] = CreateTypedInstance(typeB, tB, j));
            }
            else
            {
                for (int i = 0; i < aCount; i++) aInstances[i] = CreateTypedInstance(typeA, tA, i);
                for (int j = 0; j < bCount; j++) bInstances[j] = CreateTypedInstance(typeB, tB, j);
            }

            // compute B->A lists. RunIndexedLoop already respects _useParallelEvaluation.
            var logs = RunIndexedLoop(bCount, (jb, buf) =>
            {
                var yInst = bInstances[jb];
                var list = new List<int>();
                for (int ia = 0; ia < aCount; ia++)
                {
                    try
                    {
                        if (evalFunc(aInstances[ia], yInst))
                            list.Add(ia);
                    }
                    catch (Exception ex)
                    {
                        buf.Add($"Precompute exception A#{ia} B#{jb}: {ex.GetType().Name} {ex.Message}");
                    }
                }
                cacheVal.BToA[jb] = list;
            });

            foreach (var msg in logs) Log(msg);

            // If we need AToB populate it — parallelized and thread-safe using per-index locks
            if (tryBidirectional)
            {
                // ensure AToB exists (it was created in ctor when computeAToB==true)
                var locks = new object[aCount];
                for (int i = 0; i < aCount; i++) locks[i] = new object();

                if (_useParallelEvaluation)
                {
                    Parallel.For(0, bCount, jb =>
                    {
                        var list = cacheVal.BToA[jb];
                        foreach (var ia in list)
                        {
                            // per-ia lock to avoid concurrent List.Add on same list
                            lock (locks[ia])
                            {
                                cacheVal.AToB![ia].Add(jb);
                            }
                        }
                    });
                }
                else
                {
                    for (int jb = 0; jb < bCount; jb++)
                    {
                        var list = cacheVal.BToA[jb];
                        foreach (var ia in list)
                            cacheVal.AToB![ia].Add(jb);
                    }
                }
            }

            return cacheVal;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

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

        var key = MakeMatchCacheKey(tableA, tableB, predicateString);

        MatchCacheValue? cacheEntry = null;
        // Precompute happens only if both the per-call flag and the global flag are true
        if (_usePrecompute)
        {
            if (!TryGetCachedMatches(key, out cacheEntry))
            {
                Log($"TryEvaluateMatches: precomputing matches for key {key}");
                var pre = PrecomputeMatches(typeA, typeB, tA, tB, predicateString, tryBidirectional: false, out string? preErr);
                if (pre == null)
                {
                    Log($"Precompute failed: {preErr}");
                    return false;
                }
                AddCachedMatches(key, pre);
                cacheEntry = pre;
            }
        }

        // selected A boolean map (fast lookup)
        var selectedA = new bool[tA.Rows.Count];
        for (int i = 0; i < tA.Rows.Count; i++)
        {
            if (tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb) selectedA[i] = true;
        }

        int bCount = tB.Rows.Count;
        var output = new bool[bCount];

        if (_usePrecompute && cacheEntry != null)
        {
            // use cached B->A lists; loop B (parallel or not via helper)
            RunIndexedLoop(bCount, (ib, buf) =>
            {
                var matches = cacheEntry.BToA[ib];
                bool has = false;
                for (int k = 0; k < matches.Count; k++)
                {
                    if (selectedA[matches[k]]) { has = true; break; }
                }
                output[ib] = has;
            });

            result = output.ToList();
            Log($"TryEvaluateMatches: finished using precompute, result count={result.Count}");
            return true;
        }
        else
        {
            Log($"TryEvaluateMatches: compiling predicate '{predicateString}' for A='{tableA}' B='{tableB}'");
            var lambda = CompilePredicate(typeA, typeB, predicateString, out string? compileErr);
            if (lambda == null) { Log($"Predicate compile failed: {compileErr}"); return false; }

            Delegate? compiledDelegate = TryCompileLambda(lambda, typeA, typeB, out string? compileError);
            if (compiledDelegate is null) { Log($"Predicate compile failed: {compileError}"); return false; }

            var evalFunc = CreateInvoker(compiledDelegate!);

            var selectedAIndices = Enumerable.Range(0, tA.Rows.Count)
                .Where(i => tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb)
                .ToArray();

            var aSelectedInstances = selectedAIndices
                .Select(i => CreateTypedInstance(typeA, tA, i))
                .ToArray();

            var bInstances = new object[bCount];
            for (int i = 0; i < bCount; i++)
                bInstances[i] = CreateTypedInstance(typeB, tB, i);

            var logs = RunIndexedLoop(bCount, (ib, buf) =>
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
            });

            foreach (var msg in logs) Log(msg);

            result = output.ToList();
            Log($"TryEvaluateMatches: finished, result count={result.Count}");
            return true;
        }
    }

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

        var key = MakeMatchCacheKey(tableA, tableB, predicateString);

        MatchCacheValue? cacheEntry = null;
        if (_usePrecompute)
        {
            if (!TryGetCachedMatches(key, out cacheEntry))
            {
                Log($"TryEvaluateMatchesBidirectional: precomputing matches for key {key}");
                var pre = PrecomputeMatches(typeA, typeB, tA, tB, predicateString, tryBidirectional: true, out string? preErr);
                if (pre == null)
                {
                    Log($"Precompute failed: {preErr}");
                    return false;
                }
                AddCachedMatches(key, pre);
                cacheEntry = pre;
            }
            else
            {
                // If cache exists but lacks AToB, derive it from BToA (parallel if enabled)
                if (!cacheEntry.HasAToB)
                {
                    // allocate under global lock once
                    lock (_lock)
                    {
                        if (!cacheEntry.HasAToB)
                        {
                            cacheEntry.AToB = new List<int>[cacheEntry.ACount];
                            for (int i = 0; i < cacheEntry.ACount; i++) cacheEntry.AToB[i] = new List<int>();
                        }
                    }

                    var aCountLocal = cacheEntry.ACount;
                    var bCountLocal = cacheEntry.BCount;
                    var locks = new object[aCountLocal];
                    for (int i = 0; i < aCountLocal; i++) locks[i] = new object();

                    if (_useParallelEvaluation)
                    {
                        Parallel.For(0, bCountLocal, jb =>
                        {
                            var list = cacheEntry.BToA[jb];
                            foreach (var ia in list)
                            {
                                lock (locks[ia])
                                {
                                    cacheEntry.AToB![ia].Add(jb);
                                }
                            }
                        });
                    }
                    else
                    {
                        for (int jb = 0; jb < bCountLocal; jb++)
                        {
                            var list = cacheEntry.BToA[jb];
                            foreach (var ia in list)
                                cacheEntry.AToB![ia].Add(jb);
                        }
                    }
                }
            }
        }

        int aCount = tA.Rows.Count;
        int bCount = tB.Rows.Count;

        var selArr = new bool[aCount];
        var resArr = new bool[bCount];

        if (_usePrecompute && cacheEntry != null)
        {
            // Build selectedB boolean map
            var selectedB = new bool[bCount];
            for (int j = 0; j < bCount; j++)
                if (tB.Rows[j].TryGetValue("isSelected", out var s) && s is bool sb && sb) selectedB[j] = true;

            // For each A, check its AToB list
            RunIndexedLoop(aCount, (ia, buf) =>
            {
                var list = cacheEntry.AToB![ia];
                bool matched = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (selectedB[list[k]]) { matched = true; break; }
                }
                selArr[ia] = matched;
            });

            // For each B, check its BToA list against selectedA map
            var selectedA = new bool[aCount];
            for (int i = 0; i < aCount; i++)
                if (tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb) selectedA[i] = true;

            RunIndexedLoop(bCount, (jb, buf) =>
            {
                var list = cacheEntry.BToA[jb];
                bool has = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (selectedA[list[k]]) { has = true; break; }
                }
                resArr[jb] = has;
            });

            result = resArr.ToList();
            selectedFrom = selArr.ToList();
            Log($"TryEvaluateMatchesBidirectional: finished using precompute resultCount={result.Count} selectedFromCount={selectedFrom.Count}");
            return true;
        }
        else
        {
            Log($"TryEvaluateMatchesBidirectional: compiling predicate '{predicateString}'");
            var lambda = CompilePredicate(typeA, typeB, predicateString, out string? compileErr);
            if (lambda == null) { Log($"Predicate compile failed: {compileErr}"); return false; }

            Delegate? compiledDelegate = TryCompileLambda(lambda, typeA, typeB, out string? compileError);
            if (compiledDelegate is null) { Log($"Predicate compile failed: {compileError}"); return false; }

            var evalFunc = CreateInvoker(compiledDelegate!);

            var selectedAIndices = Enumerable.Range(0, aCount)
                .Where(i => tA.Rows[i].TryGetValue("isSelected", out var s) && s is bool sb && sb)
                .ToArray();

            var selectedBIndices = Enumerable.Range(0, bCount)
                .Where(j => tB.Rows[j].TryGetValue("isSelected", out var s) && s is bool sb && sb)
                .ToArray();

            var aSelectedInstances = selectedAIndices.Select(i => CreateTypedInstance(typeA, tA, i)).ToArray();
            var bSelectedInstances = selectedBIndices.Select(j => CreateTypedInstance(typeB, tB, j)).ToArray();

            var bInstances = new object[bCount];
            if (_useParallelEvaluation)
                Parallel.For(0, bCount, j => bInstances[j] = CreateTypedInstance(typeB, tB, j));
            else
                for (int j = 0; j < bCount; j++) bInstances[j] = CreateTypedInstance(typeB, tB, j);

            Log($"Bidirectional: selectedA={selectedAIndices.Length}, selectedB={selectedBIndices.Length}");

            var logsA = RunIndexedLoop(aCount, (ia, buf) =>
            {
                var xInst = CreateTypedInstance(typeA, tA, ia);
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
            });

            var logsB = RunIndexedLoop(bCount, (jb, buf) =>
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
            });

            foreach (var msg in logsA) Log(msg);
            foreach (var msg in logsB) Log(msg);

            result = resArr.ToList();
            selectedFrom = selArr.ToList();
            Log($"TryEvaluateMatchesBidirectional: finished resultCount={result.Count} selectedFromCount={selectedFrom.Count}");
            return true;
        }
    }

    private Lambda? CompilePredicate(Type typeA, Type typeB, string predicate, out string? error)
    {
        error = null;
        try
        {
            var interpreter = new Interpreter()
                .Reference(typeof(Math))
                .Reference(typeof(Convert))
                .SetDefaultNumberType(DefaultNumberType.Double);

            interpreter.SetFunction("Sqrt", (Func<double, double>)Math.Sqrt);
            interpreter.SetFunction("Pow", (Func<double, double, double>)Math.Pow);

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
                Log($"CreateTypedInstance: conversion failed table='{table.Name}' row={rowIndex} col='{original}' -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        return inst;
    }

    public string PrintTable(string tableName, int maxRows = 50)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            return $"Table '{tableName}' not found.";

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

// Simple in-memory table type (kept here for completeness)
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
