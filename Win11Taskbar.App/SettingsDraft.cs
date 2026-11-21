using System.Reflection;
using Win11Taskbar.Core;

namespace Win11Taskbar.App;

internal static class SettingsDraft
{
    static readonly PropertyInfo[] Properties = typeof(Settings).GetProperties()
        .Where(property => property.CanRead && property.CanWrite)
        .ToArray();

    internal static Settings Clone(Settings source)
    {
        var copy = new Settings();
        Copy(source, copy);
        return copy;
    }

    internal static void Copy(Settings source, Settings destination)
    {
        foreach (var property in Properties)
        {
            var value = property.GetValue(source);
            if (!Equals(value, property.GetValue(destination)))
                property.SetValue(destination, value);
        }
    }

    internal static bool Equal(Settings first, Settings second) =>
        Properties.All(property => Equals(property.GetValue(first), property.GetValue(second)));
}
