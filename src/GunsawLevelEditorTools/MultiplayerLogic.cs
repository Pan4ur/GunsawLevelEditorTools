using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GunsawLevelEditorTools;

internal static class MultiplayerLogic
{
    private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> Members =
        new Dictionary<Type, Dictionary<string, MemberInfo>>();

    internal static bool TryGetData(MonoBehaviour marker, out object data)
    {
        data = null;
        if (marker == null || marker.GetType().Name != "CustomPropMarker")
            return false;
        var instance = Read(marker, "Instance");
        if (instance == null)
            return false;
        data = Read(instance, "Data");
        return data != null;
    }

    internal static bool IsType(object data, string name)
    {
        return data != null && data.GetType().Name == name;
    }

    internal static int ReadInt(object source, string name)
    {
        var value = Read(source, name);
        return value is int result ? result : 0;
    }

    internal static string ReadString(object source, string name)
    {
        return Read(source, name) as string;
    }

    private static object Read(object source, string name)
    {
        if (source == null)
            return null;
        var type = source.GetType();
        Dictionary<string, MemberInfo> typeMembers;
        if (!Members.TryGetValue(type, out typeMembers))
        {
            typeMembers = new Dictionary<string, MemberInfo>();
            Members.Add(type, typeMembers);
        }

        MemberInfo member;
        if (!typeMembers.TryGetValue(name, out member))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            member = type.GetField(name, flags);
            if (member == null)
                member = type.GetProperty(name, flags);
            typeMembers.Add(name, member);
        }

        var field = member as FieldInfo;
        if (field != null)
            return field.GetValue(source);
        var property = member as PropertyInfo;
        return property == null ? null : property.GetValue(source, null);
    }
}