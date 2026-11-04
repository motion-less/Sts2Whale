using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Sts2AgentBridge;

/// <summary>
/// 长跑内存诊断：把进程内所有静态 <see cref="Dictionary{TKey,TValue}"/> 按条目数排出来。
///
/// 起因：跑满 9.7 小时后堆里有两张各 144 MB 的
/// <c>Dictionary&lt;AbstractModel,uint&gt;</c> / <c>Dictionary&lt;uint,AbstractModel&gt;</c>
/// （约 600 万条），但光看类型名判断不出是谁建的。这里直接反射读静态字段，
/// 一次就能看到「声明类型 + 字段名 + 条目数」，不用反编译。
/// </summary>
internal static class DiagDicts
{
    public static Dictionary<string, object> Scan(int minCount)
    {
        var rows = new List<(string Owner, string Field, int Count)>();
        int scannedAssemblies = 0, scannedFields = 0;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
            catch { continue; }
            scannedAssemblies++;

            foreach (Type type in types)
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch { continue; }

                foreach (FieldInfo field in fields)
                {
                    scannedFields++;
                    Type fieldType = field.FieldType;
                    if (!fieldType.IsGenericType) continue;
                    if (fieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
                    // 只认「键或值是模型」的字典 + 大条目数，避免把普通小表全扫出来。
                    Type[] args = fieldType.GetGenericArguments();
                    bool modelKeyed = args[0].Name.Contains("Model", StringComparison.Ordinal)
                        || args[1].Name.Contains("Model", StringComparison.Ordinal);
                    if (!modelKeyed) continue;

                    object? value;
                    try { value = field.GetValue(null); } catch { continue; }
                    if (value is not IDictionary dict) continue;
                    if (dict.Count < minCount) continue;
                    rows.Add((type.FullName ?? type.Name, field.Name, dict.Count));
                }
            }
        }

        rows.Sort((a, b) => b.Count.CompareTo(a.Count));
        var list = new List<object>();
        foreach (var row in rows)
            list.Add(new Dictionary<string, object>
            {
                ["owner"] = row.Owner,
                ["field"] = row.Field,
                ["count"] = row.Count,
            });

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["minCount"] = minCount,
            ["assemblies"] = scannedAssemblies,
            ["fields"] = scannedFields,
            ["maxCount"] = list.Count > 0 ? rows[0].Count : 0,
            ["items"] = list,
        };
    }

    /// <summary>把某个静态字典清空（只给确认过是缓存、清掉不掉功能的对象用）。</summary>
    public static bool Clear(string owner, string field, out int removed)
    {
        removed = 0;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try { type = assembly.GetType(owner); } catch { continue; }
            if (type == null) continue;
            FieldInfo? info;
            try { info = type.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { continue; }
            if (info == null) continue;
            object? value;
            try { value = info.GetValue(null); } catch { continue; }
            if (value is not IDictionary dict) continue;
            removed = dict.Count;
            try { dict.Clear(); } catch { return false; }
            return true;
        }
        return false;
    }
}
