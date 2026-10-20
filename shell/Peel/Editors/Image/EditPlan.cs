// What the Edit window has been set to, and the parameters that follow from it.
//
// Every range, choice and default here is read from the entry's schema
// (ParamSchema), never written down a second time: image.edit allows 0 to 4 for
// the three factors today, and if the engine widens that the sliders widen with
// it. Only parameters the user actually changed are sent, so the engine's own
// defaults stay the single definition of neutral.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Peel.Editors.Image;

internal sealed class EditPlan
{
    private static readonly string[] FallbackRotations = { "0", "90", "180", "270" };
    private static readonly string[] FallbackFlips = { "none", "horizontal", "vertical" };

    private readonly ParamSchema _schema;

    internal EditPlan(ParamSchema schema)
    {
        _schema = schema;
        Rotations = schema.Choices("rotate") is { Count: > 0 } rotations ? rotations : FallbackRotations;
        Flips = schema.Choices("flip") is { Count: > 0 } flips ? flips : FallbackFlips;
        DefaultRotate = schema.Text("rotate", "0");
        DefaultFlip = schema.Text("flip", "none");
        Reset();
    }

    internal IReadOnlyList<string> Rotations { get; }

    internal IReadOnlyList<string> Flips { get; }

    internal string DefaultRotate { get; }

    internal string DefaultFlip { get; }

    internal string Rotate { get; set; } = "0";

    internal string Flip { get; set; } = "none";

    // The three factors are quantized once, here, when they are set. The
    // preview, the readout and the parameters all read these same values, so a
    // slider left at 0.996 by a drag or by UI Automation is 1.00 everywhere,
    // never 0.996 on screen and 1.00 in the file.
    private double _brightness = Adjust.Neutral;
    private double _contrast = Adjust.Neutral;
    private double _saturation = Adjust.Neutral;

    internal double Brightness
    {
        get => _brightness;
        set => _brightness = Quantize("brightness", value);
    }

    internal double Contrast
    {
        get => _contrast;
        set => _contrast = Quantize("contrast", value);
    }

    internal double Saturation
    {
        get => _saturation;
        set => _saturation = Quantize("saturation", value);
    }

    /// <summary>
    /// A factor as the plan keeps it: clamped to the schema's range and rounded
    /// to the step. NaN (which a slider never holds, but a bound value might)
    /// becomes the default rather than reaching the preview or the engine.
    /// </summary>
    internal double Quantize(string name, double value)
    {
        if (!double.IsFinite(value))
        {
            value = _schema.Number(name, Adjust.Neutral);
        }
        var clamped = Math.Clamp(value, Minimum(name), Maximum(name));
        return Math.Round(clamped, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The value a readout shows for a factor: the one that is sent.</summary>
    internal static string Readout(double factor) => factor.ToString("0.00", CultureInfo.InvariantCulture);

    internal double Minimum(string name) => _schema.Minimum(name, 0.0);

    internal double Maximum(string name) => _schema.Maximum(name, 4.0);

    internal double Default(string name) => Quantize(name, _schema.Number(name, Adjust.Neutral));

    /// <summary>Degrees clockwise, for turning the preview.</summary>
    internal int RotateDegrees =>
        int.TryParse(Rotate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees) ? degrees : 0;

    internal bool FlipsHorizontally => Flip == "horizontal";

    internal bool FlipsVertically => Flip == "vertical";

    internal bool AnyChange => ToParams().Count > 0;

    internal void Reset()
    {
        Rotate = DefaultRotate;
        Flip = DefaultFlip;
        Brightness = Default("brightness");
        Contrast = Default("contrast");
        Saturation = Default("saturation");
    }

    /// <summary>
    /// Only what differs from the engine's own default. A run with no
    /// parameters at all is still legal (the engine saves the file unchanged),
    /// so this never has to invent one.
    /// </summary>
    internal Dictionary<string, string> ToParams()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Rotate != DefaultRotate)
        {
            parameters["rotate"] = Rotate;
        }
        if (Flip != DefaultFlip)
        {
            parameters["flip"] = Flip;
        }
        AddFactor(parameters, "brightness", Brightness);
        AddFactor(parameters, "contrast", Contrast);
        AddFactor(parameters, "saturation", Saturation);
        return parameters;
    }

    private void AddFactor(IDictionary<string, string> parameters, string name, double value)
    {
        // value is already quantized (the setters), and so is the default, so
        // the comparison is exact and the string is the same number the
        // preview was painted with.
        if (value == Default(name))
        {
            return;
        }
        parameters[name] = value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
