using UnityEditor;
using UnityEngine;
using System;
using System.ComponentModel;
using System.Reflection;

/// <summary>
/// 自定义属性绘制器：读取枚举的 [Description] 特性，在 Inspector 下拉框中显示中文
/// </summary>
[CustomPropertyDrawer(typeof(Enum), true)]
public class LocalizedEnumDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        // 如果不是枚举类型，使用 Unity 默认绘制
        if (property.propertyType != SerializedPropertyType.Enum)
        {
            EditorGUI.PropertyField(position, property, label, true);
            return;
        }

        // 获取枚举的实际类型（支持 ScriptableObject 中的嵌套结构）
        Type enumType = fieldInfo.FieldType;
        if (enumType == null || !enumType.IsEnum)
        {
            EditorGUI.PropertyField(position, property, label, true);
            return;
        }

        // 构建显示名称数组（读取 [Description] 特性）
        string[] enumNames = property.enumNames;
        string[] displayNames = new string[enumNames.Length];

        for (int i = 0; i < enumNames.Length; i++)
        {
            FieldInfo enumField = enumType.GetField(enumNames[i]);
            DescriptionAttribute[] attributes =
                (DescriptionAttribute[])enumField.GetCustomAttributes(typeof(DescriptionAttribute), false);
            displayNames[i] = attributes.Length > 0 ? attributes[0].Description : enumNames[i];
        }

        // 绘制下拉框
        EditorGUI.BeginChangeCheck();
        int newIndex = EditorGUI.Popup(position, label.text, property.enumValueIndex, displayNames);
        if (EditorGUI.EndChangeCheck())
        {
            property.enumValueIndex = newIndex;
        }
    }
}