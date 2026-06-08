using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQLite.Engine.Vdbe;

namespace SQLite.Engine.Func;

/// <summary>
/// JSON scalar functions matching SQLite's JSON1 extension:
/// json(), json_array(), json_array_length(), json_extract(), json_insert(),
/// json_object(), json_remove(), json_replace(), json_set(), json_type(),
/// json_valid(), json_quote(), json_group_array (aggregate), json_group_object (aggregate).
/// </summary>
public static class JsonFunctions
{
    /// <summary>
    /// Execute a JSON function. Returns true if the function was recognized, false otherwise.
    /// </summary>
    public static bool Execute(string name, Mem[] registers, int destReg, int firstArg, int argCount)
    {
        switch (name)
        {
            case "json":
                return Json(registers, destReg, firstArg, argCount);
            case "json_array":
                return JsonArray(registers, destReg, firstArg, argCount);
            case "json_array_length":
                return JsonArrayLength(registers, destReg, firstArg, argCount);
            case "json_extract":
                return JsonExtract(registers, destReg, firstArg, argCount);
            case "json_insert":
                return JsonModify(registers, destReg, firstArg, argCount, ModifyMode.Insert);
            case "json_object":
                return JsonObject(registers, destReg, firstArg, argCount);
            case "json_remove":
                return JsonRemove(registers, destReg, firstArg, argCount);
            case "json_replace":
                return JsonModify(registers, destReg, firstArg, argCount, ModifyMode.Replace);
            case "json_set":
                return JsonModify(registers, destReg, firstArg, argCount, ModifyMode.Set);
            case "json_type":
                return JsonType(registers, destReg, firstArg, argCount);
            case "json_valid":
                return JsonValid(registers, destReg, firstArg, argCount);
            case "json_quote":
                return JsonQuote(registers, destReg, firstArg, argCount);
            case "json_patch":
                return JsonPatch(registers, destReg, firstArg, argCount);
            default:
                return false;
        }
    }

    /// <summary>
    /// Execute JSON aggregate step. Returns true if recognized.
    /// </summary>
    public static bool ExecuteAggStep(string name, Mem[] registers, int destReg, int firstArg, int argCount)
    {
        switch (name)
        {
            case "json_group_array":
                if (argCount >= 1)
                {
                    var acc = registers[destReg];
                    var list = acc.AggState as List<object?> ?? new List<object?>();
                    list.Add(registers[firstArg].ToObject());
                    acc.AggState = list;
                    acc.AggCount++;
                }
                return true;
            case "json_group_object":
                if (argCount >= 2)
                {
                    var acc = registers[destReg];
                    var dict = acc.AggState as List<KeyValuePair<string, object?>> ?? new List<KeyValuePair<string, object?>>();
                    string key = registers[firstArg].ToText();
                    object? val = registers[firstArg + 1].ToObject();
                    dict.Add(new KeyValuePair<string, object?>(key, val));
                    acc.AggState = dict;
                    acc.AggCount++;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Execute JSON aggregate final. Returns true if recognized.
    /// </summary>
    public static bool ExecuteAggFinal(string name, Mem[] registers, int destReg)
    {
        switch (name)
        {
            case "json_group_array":
            {
                var acc = registers[destReg];
                var list = acc.AggState as List<object?> ?? new List<object?>();
                var arr = new JsonArray();
                foreach (var item in list)
                    arr.Add(ToJsonNode(item));
                acc.SetText(arr.ToJsonString());
                return true;
            }
            case "json_group_object":
            {
                var acc = registers[destReg];
                var dict = acc.AggState as List<KeyValuePair<string, object?>> ?? new List<KeyValuePair<string, object?>>();
                var obj = new JsonObject();
                foreach (var kv in dict)
                    obj[kv.Key] = ToJsonNode(kv.Value);
                acc.SetText(obj.ToJsonString());
                return true;
            }
            default:
                return false;
        }
    }

    // ─── Scalar implementations ─────────────────────────────────────────────

    /// <summary>json(X) — validates and minifies JSON text.</summary>
    private static bool Json(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        string input = v.ToText();
        try
        {
            var node = JsonNode.Parse(input);
            registers[destReg].SetText(node?.ToJsonString() ?? "null");
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    /// <summary>json_array(...) — builds a JSON array from arguments.</summary>
    private static bool JsonArray(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        var arr = new JsonArray();
        for (int i = 0; i < argCount; i++)
            arr.Add(MemToJsonNode(registers[firstArg + i]));
        registers[destReg].SetText(arr.ToJsonString());
        return true;
    }

    /// <summary>json_array_length(json[, path]) — number of elements in array.</summary>
    private static bool JsonArrayLength(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var node = JsonNode.Parse(v.ToText());
            if (argCount >= 2)
            {
                string path = registers[firstArg + 1].ToText();
                node = NavigatePath(node, path);
            }
            if (node is JsonArray arr)
                registers[destReg].SetInt(arr.Count);
            else
                registers[destReg].SetInt(0);
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    /// <summary>json_extract(json, path1[, path2, ...]) — extract values by path.</summary>
    private static bool JsonExtract(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 2) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var root = JsonNode.Parse(v.ToText());

            if (argCount == 2)
            {
                string path = registers[firstArg + 1].ToText();
                var result = NavigatePath(root, path);
                SetMemFromJsonNode(registers[destReg], result);
            }
            else
            {
                // Multiple paths → return JSON array of results
                var arr = new JsonArray();
                for (int i = 1; i < argCount; i++)
                {
                    string path = registers[firstArg + i].ToText();
                    var result = NavigatePath(root, path);
                    arr.Add(result?.DeepClone());
                }
                registers[destReg].SetText(arr.ToJsonString());
            }
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    /// <summary>json_object(key1, val1, ...) — builds JSON object from key-value pairs.</summary>
    private static bool JsonObject(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        var obj = new JsonObject();
        for (int i = 0; i + 1 < argCount; i += 2)
        {
            string key = registers[firstArg + i].ToText();
            var val = MemToJsonNode(registers[firstArg + i + 1]);
            obj[key] = val;
        }
        registers[destReg].SetText(obj.ToJsonString());
        return true;
    }

    /// <summary>json_remove(json, path1, ...) — remove elements at paths.</summary>
    private static bool JsonRemove(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 2) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var root = JsonNode.Parse(v.ToText());
            for (int i = 1; i < argCount; i++)
            {
                string path = registers[firstArg + i].ToText();
                RemoveAtPath(root, path);
            }
            registers[destReg].SetText(root?.ToJsonString() ?? "null");
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    private enum ModifyMode { Insert, Replace, Set }

    /// <summary>json_insert/json_replace/json_set(json, path, value, ...) — modify JSON.</summary>
    private static bool JsonModify(Mem[] registers, int destReg, int firstArg, int argCount, ModifyMode mode)
    {
        if (argCount < 3) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var root = JsonNode.Parse(v.ToText());
            for (int i = 1; i + 1 < argCount; i += 2)
            {
                string path = registers[firstArg + i].ToText();
                var newValue = MemToJsonNode(registers[firstArg + i + 1]);
                root = SetAtPath(root, path, newValue, mode);
            }
            registers[destReg].SetText(root?.ToJsonString() ?? "null");
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    /// <summary>json_type(json[, path]) — returns the JSON type as a string.</summary>
    private static bool JsonType(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var node = JsonNode.Parse(v.ToText());
            if (argCount >= 2)
            {
                string path = registers[firstArg + 1].ToText();
                node = NavigatePath(node, path);
            }
            registers[destReg].SetText(GetJsonTypeName(node));
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    /// <summary>json_valid(json) — 1 if valid JSON, 0 otherwise.</summary>
    private static bool JsonValid(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        if (v.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            JsonNode.Parse(v.ToText());
            registers[destReg].SetInt(1);
        }
        catch
        {
            registers[destReg].SetInt(0);
        }
        return true;
    }

    /// <summary>json_quote(value) — wraps a SQL value as JSON.</summary>
    private static bool JsonQuote(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 1) { registers[destReg].SetNull(); return true; }
        var v = registers[firstArg];
        var node = MemToJsonNode(v);
        registers[destReg].SetText(node?.ToJsonString() ?? "null");
        return true;
    }

    /// <summary>json_patch(target, patch) — RFC 7396 merge patch.</summary>
    private static bool JsonPatch(Mem[] registers, int destReg, int firstArg, int argCount)
    {
        if (argCount < 2) { registers[destReg].SetNull(); return true; }
        var tv = registers[firstArg];
        var pv = registers[firstArg + 1];
        if (tv.Type == MemType.Null || pv.Type == MemType.Null) { registers[destReg].SetNull(); return true; }

        try
        {
            var target = JsonNode.Parse(tv.ToText());
            var patch = JsonNode.Parse(pv.ToText());
            var result = MergePatch(target, patch);
            registers[destReg].SetText(result?.ToJsonString() ?? "null");
        }
        catch
        {
            registers[destReg].SetNull();
        }
        return true;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static JsonNode? MemToJsonNode(Mem v)
    {
        return v.Type switch
        {
            MemType.Null => null,
            MemType.Int64 => JsonValue.Create(v.IntValue),
            MemType.Double => JsonValue.Create(v.RealValue),
            MemType.Text => TryParseAsJson(v.TextValue) ?? JsonValue.Create(v.TextValue),
            MemType.Blob => JsonValue.Create(Convert.ToBase64String(v.BlobValue ?? Array.Empty<byte>())),
            _ => null,
        };
    }

    private static JsonNode? TryParseAsJson(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // Only treat as JSON if it starts with { or [ (not bare strings/numbers)
        char first = text[0];
        if (first != '{' && first != '[') return null;
        try { return JsonNode.Parse(text); }
        catch { return null; }
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            string s => JsonValue.Create(s),
            byte[] b => JsonValue.Create(Convert.ToBase64String(b)),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    private static void SetMemFromJsonNode(Mem dest, JsonNode? node)
    {
        if (node == null)
        {
            dest.SetNull();
            return;
        }

        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<long>(out var l))
                dest.SetInt(l);
            else if (jv.TryGetValue<double>(out var d))
                dest.SetDouble(d);
            else if (jv.TryGetValue<bool>(out var b))
                dest.SetInt(b ? 1 : 0);
            else if (jv.TryGetValue<string>(out var s))
                dest.SetText(s);
            else
                dest.SetText(node.ToJsonString());
        }
        else
        {
            // Arrays and objects are returned as JSON text
            dest.SetText(node.ToJsonString());
        }
    }

    private static string GetJsonTypeName(JsonNode? node)
    {
        if (node == null) return "null";
        if (node is JsonObject) return "object";
        if (node is JsonArray) return "array";
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<long>(out _)) return "integer";
            if (jv.TryGetValue<double>(out _)) return "real";
            if (jv.TryGetValue<bool>(out _)) return "true"; // SQLite returns "true"/"false"
            if (jv.TryGetValue<string>(out _)) return "text";
        }
        return "null";
    }

    /// <summary>
    /// Navigate a JSON path like "$.key.subkey[0]" or "$[0].name".
    /// Supports: $ (root), .key (object member), [n] (array index).
    /// </summary>
    private static JsonNode? NavigatePath(JsonNode? root, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return root;

        var current = root;
        int i = 0;

        // Skip leading $
        if (i < path.Length && path[i] == '$') i++;

        while (i < path.Length && current != null)
        {
            if (path[i] == '.')
            {
                i++; // skip dot
                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                string key = path[start..i];
                if (current is JsonObject obj)
                    current = obj[key];
                else
                    return null;
            }
            else if (path[i] == '[')
            {
                i++; // skip [
                int start = i;
                while (i < path.Length && path[i] != ']') i++;
                string indexStr = path[start..i];
                if (i < path.Length) i++; // skip ]

                if (int.TryParse(indexStr, out int idx))
                {
                    if (current is JsonArray arr && idx >= 0 && idx < arr.Count)
                        current = arr[idx];
                    else
                        return null;
                }
                else
                {
                    // Bracket notation for keys: ["key"]
                    string key = indexStr.Trim('"', '\'');
                    if (current is JsonObject obj)
                        current = obj[key];
                    else
                        return null;
                }
            }
            else
            {
                break;
            }
        }

        return current;
    }

    private static void RemoveAtPath(JsonNode? root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path)) return;

        // Find parent and last key/index
        int lastDot = path.LastIndexOf('.');
        int lastBracket = path.LastIndexOf('[');
        int splitPoint = Math.Max(lastDot, lastBracket);

        if (splitPoint <= 0) return; // Can't remove root

        string parentPath = path[..splitPoint];
        string lastSegment = path[splitPoint..];

        var parent = NavigatePath(root, parentPath);
        if (parent == null) return;

        if (lastSegment.StartsWith('.'))
        {
            string key = lastSegment[1..];
            if (parent is JsonObject obj)
                obj.Remove(key);
        }
        else if (lastSegment.StartsWith('['))
        {
            string indexStr = lastSegment.Trim('[', ']');
            if (int.TryParse(indexStr, out int idx) && parent is JsonArray arr && idx >= 0 && idx < arr.Count)
                arr.RemoveAt(idx);
        }
    }

    private static JsonNode? SetAtPath(JsonNode? root, string path, JsonNode? newValue, ModifyMode mode)
    {
        if (root == null || string.IsNullOrEmpty(path) || path == "$")
        {
            // Replace root
            return mode == ModifyMode.Insert ? root : newValue;
        }

        int lastDot = path.LastIndexOf('.');
        int lastBracket = path.LastIndexOf('[');
        int splitPoint = Math.Max(lastDot, lastBracket);

        if (splitPoint <= 0) return root;

        string parentPath = path[..splitPoint];
        string lastSegment = path[splitPoint..];

        var parent = NavigatePath(root, parentPath);
        if (parent == null) return root;

        if (lastSegment.StartsWith('.'))
        {
            string key = lastSegment[1..];
            if (parent is JsonObject obj)
            {
                bool exists = obj.ContainsKey(key);
                if (mode == ModifyMode.Insert && exists) return root;
                if (mode == ModifyMode.Replace && !exists) return root;
                obj[key] = newValue?.DeepClone();
            }
        }
        else if (lastSegment.StartsWith('['))
        {
            string indexStr = lastSegment.Trim('[', ']');
            if (int.TryParse(indexStr, out int idx) && parent is JsonArray arr)
            {
                bool exists = idx >= 0 && idx < arr.Count;
                if (mode == ModifyMode.Insert && exists) return root;
                if (mode == ModifyMode.Replace && !exists) return root;
                if (exists)
                    arr[idx] = newValue?.DeepClone();
                else if (mode != ModifyMode.Replace)
                    arr.Add(newValue?.DeepClone());
            }
        }

        return root;
    }

    /// <summary>RFC 7396 merge patch.</summary>
    private static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObj)
            return patch?.DeepClone();

        JsonObject result;
        if (target is JsonObject targetObj)
            result = targetObj.DeepClone().AsObject();
        else
            result = new JsonObject();

        foreach (var kv in patchObj)
        {
            if (kv.Value == null)
            {
                result.Remove(kv.Key);
            }
            else
            {
                JsonNode? existing = result[kv.Key];
                result[kv.Key] = MergePatch(existing, kv.Value);
            }
        }

        return result;
    }
}
