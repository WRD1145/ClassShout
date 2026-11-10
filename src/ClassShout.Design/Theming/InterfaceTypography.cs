using Avalonia;
using Avalonia.Controls;

namespace ClassShout.Design.Theming;

public static class InterfaceTypography
{
    private static readonly string[] Keys =
    [
        "Md3.Type.DisplayLarge",
        "Md3.Type.DisplayMedium",
        "Md3.Type.DisplaySmall",
        "Md3.Type.HeadlineLarge",
        "Md3.Type.HeadlineMedium",
        "Md3.Type.HeadlineSmall",
        "Md3.Type.TitleLarge",
        "Md3.Type.TitleMedium",
        "Md3.Type.TitleSmall",
        "Md3.Type.BodyLarge",
        "Md3.Type.BodyMedium",
        "Md3.Type.BodySmall",
        "Md3.Type.LabelLarge",
        "Md3.Type.LabelMedium",
        "Md3.Type.LabelSmall",
        "Md3.Line.16",
        "Md3.Line.20",
        "Md3.Line.24",
        "Md3.Line.28",
        "Md3.Line.32",
        "Md3.Line.36",
        "Md3.Line.40",
        "Md3.Line.44",
        "Md3.Line.52",
        "Md3.Line.64",
    ];
    private static ResourceDictionary? _overrides;

    public static void Apply(Application app, int percent)
    {
        if (_overrides is not null)
            app.Resources.MergedDictionaries.Remove(_overrides);

        var resources = new ResourceDictionary();
        foreach (var key in Keys)
        {
            if (app.TryFindResource(key, out var value) && value is double baseline)
                resources[key] = baseline * percent / 100d;
        }
        app.Resources.MergedDictionaries.Add(resources);
        _overrides = resources;
    }
}
