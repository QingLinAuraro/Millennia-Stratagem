using System;
using System.ComponentModel;
using System.Reflection;

/// <summary>
/// 枚举扩展方法：获取 [Description] 特性中的中文描述
/// 未标记特性时返回枚举成员名称本身
/// </summary>
public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        if (value == null) return string.Empty;

        Type type = value.GetType();
        string name = Enum.GetName(type, value);
        if (name == null) return string.Empty;

        FieldInfo field = type.GetField(name);
        if (field == null) return name;

        DescriptionAttribute attribute =
            Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;

        return attribute?.Description ?? name;
    }
}