// Brightness, contrast and saturation, done exactly the way the engine does.
//
// The Edit window promises a live preview, and a preview that is only roughly
// the result is worse than none: the user sets a slider by eye, the engine
// writes something else, and the file has already replaced the view they
// judged. So this is not "a brightness filter". It is pillow's ImageEnhance,
// pixel for pixel:
//
//   enhance(f) is Image.blend(degenerate, image, f), which is
//   trunc(d + f * (p - d)) per channel, clamped to a byte.
//   Brightness: d = 0.
//   Contrast:   d = int(mean(L) + 0.5), where the mean is of the image as it
//               is at that point in the chain, not of the original.
//   Saturation: d = L of the pixel.
//
// Alpha is never touched: each enhancer copies the source's alpha channel into
// its degenerate image (ImageEnhance does `degenerate.putalpha(...)` when the
// mode has an A band), so blending leaves it exactly as it was. Dimming a
// translucent PNG does not make it more transparent.
//
// L is pillow's ITU-R 601-2 luma in fixed point, so a value is never half a
// level away from pillow's. The order is the engine's order in
// families/image.py: brightness, then contrast, then saturation, each skipped
// at 1.0. Verified against pillow 13 in EditorPlanTests.
using System;

namespace Peel.Editors.Image;

internal static class Adjust
{
    /// <summary>A factor that changes nothing, so the engine is not asked to.</summary>
    internal const double Neutral = 1.0;

    /// <summary>pillow's RGB to L: (R*19595 + G*38470 + B*7471 + 0x8000) >> 16.</summary>
    internal static int Luma(int red, int green, int blue) =>
        (red * 19595 + green * 38470 + blue * 7471 + 0x8000) >> 16;

    /// <summary>
    /// The mean L of a BGRA buffer, rounded the way ImageEnhance.Contrast
    /// rounds it (int(mean + 0.5)). Alpha takes no part, as in convert("L").
    /// </summary>
    internal static int MeanLuma(byte[] bgra, int pixels)
    {
        if (pixels <= 0)
        {
            return 0;
        }
        long total = 0;
        for (var i = 0; i < pixels; i++)
        {
            var at = i * 4;
            total += Luma(bgra[at + 2], bgra[at + 1], bgra[at]);
        }
        return (int)(total / (double)pixels + 0.5);
    }

    /// <summary>Apply the three factors to a Bgra32 buffer, in place.</summary>
    internal static void Apply(byte[] bgra, int pixels, double brightness, double contrast, double saturation)
    {
        if (brightness != Neutral)
        {
            Blend(bgra, pixels, static _ => (0, 0, 0), brightness);
        }
        if (contrast != Neutral)
        {
            var mean = MeanLuma(bgra, pixels);
            Blend(bgra, pixels, _ => (mean, mean, mean), contrast);
        }
        if (saturation != Neutral)
        {
            Blend(bgra, pixels, static pixel =>
            {
                var grey = Luma(pixel.Red, pixel.Green, pixel.Blue);
                return (grey, grey, grey);
            }, saturation);
        }
    }

    private readonly record struct Pixel(int Blue, int Green, int Red);

    private static void Blend(
        byte[] bgra,
        int pixels,
        Func<Pixel, (int Blue, int Green, int Red)> degenerate,
        double factor)
    {
        for (var i = 0; i < pixels; i++)
        {
            var at = i * 4;
            var pixel = new Pixel(bgra[at], bgra[at + 1], bgra[at + 2]);
            var (blue, green, red) = degenerate(pixel);
            bgra[at] = Mix(blue, pixel.Blue, factor);
            bgra[at + 1] = Mix(green, pixel.Green, factor);
            bgra[at + 2] = Mix(red, pixel.Red, factor);
        }
    }

    /// <summary>
    /// pillow's blend of one channel: the cast truncates towards zero before
    /// the clamp, which is the difference between matching the engine and
    /// being one level off across a whole photo.
    /// </summary>
    private static byte Mix(int degenerate, int value, double factor) =>
        (byte)Math.Clamp((int)(degenerate + factor * (value - degenerate)), 0, 255);
}
