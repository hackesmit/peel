// Float: pin a small always-on-top thumbnail of each dropped file that can be
// dragged out again as the file itself, or closed.
//
// One window per file, offset so a multi-file drop fans out instead of
// stacking. Each window is borderless and topmost, shows a thumbnail for an
// image or the file's associated icon and name otherwise, and starts a normal
// file drag when you pull it, so it drops into Explorer or any app exactly as
// the original file would. Right-click or Escape closes it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Peel.Jobs;

namespace Peel.Actions;

internal static class FloatAction
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico",
    };

    /// <summary>True when the path is a file WPF can decode as a thumbnail. A
    /// folder whose name ends in an image extension (for example a folder called
    /// "holiday.jpg") is not an image, so it is excluded here.</summary>
    internal static bool IsImage(string path) =>
        !Directory.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path));

    internal static void Run(IReadOnlyList<string> files, JobRunner jobs)
    {
        var opened = 0;
        for (var i = 0; i < files.Count; i++)
        {
            try
            {
                new FloatWindow(files[i], i).Show();
                opened++;
            }
            catch (Exception error)
            {
                Log.Write($"float: {files[i]} could not be shown", error);
            }
        }
        Log.Write($"float: opened {opened} of {files.Count} window(s)");
        if (opened < files.Count)
        {
            jobs.Report($"Floated {opened} of {files.Count}; the rest could not be shown.");
        }
    }
}

internal sealed class FloatWindow : Window
{
    private const double Thumb = 160;
    private readonly string _path;
    private Point _pressed;
    private bool _dragging;

    internal FloatWindow(string path, int index)
    {
        _path = path;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Topmost = true;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var cursor = System.Windows.Forms.Cursor.Position;
        Left = cursor.X + 16 + index * 24;
        Top = cursor.Y + 16 + index * 24;

        Content = BuildContent(path);

        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            _pressed = e.GetPosition(this);
            _dragging = false;
        };
        MouseMove += OnMouseMove;
    }

    private Border BuildContent(string path)
    {
        var panel = new StackPanel { Margin = new Thickness(6) };
        var shown = false;
        if (FloatAction.IsImage(path))
        {
            try
            {
                panel.Children.Add(new Image
                {
                    Source = Thumbnail(path),
                    Stretch = Stretch.Uniform,
                    MaxWidth = Thumb,
                    MaxHeight = Thumb,
                });
                shown = true;
            }
            catch (Exception error)
            {
                // A corrupt or unreadable image must not stop the file floating:
                // fall back to the icon and name tile below.
                Log.Write($"float: could not decode {path} as an image", error);
            }
        }
        if (!shown)
        {
            var icon = AssociatedIcon(path);
            if (icon is not null)
            {
                panel.Children.Add(new Image { Source = icon, Width = 48, Height = 48, Margin = new Thickness(0, 4, 0, 4) });
            }
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path),
                Foreground = Brushes.White,
                MaxWidth = Thumb,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = panel,
            ToolTip = path,
        };
    }

    private static BitmapImage Thumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = (int)Thumb;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource? AssociatedIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception error)
        {
            Log.Write($"float: no icon for {path}", error);
            return null;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging)
        {
            return;
        }
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressed.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pressed.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { _path });
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception error)
        {
            Log.Write($"float: dragging {_path} out failed", error);
        }
        finally
        {
            _dragging = false;
        }
    }
}
