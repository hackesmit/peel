// Organize and Split: the pages of a PDF as a grid of thumbnails.
//
// Organize: drag a page to where it goes, turn it, delete it. The order on
// screen is the order written. Split: the pages stay in order and the user
// puts cuts between them, or types the ranges; with no cut the engine's
// default, one file per page, is what happens and the window says so.
//
// The drag is the window's own (mouse capture and a marker line), not an OLE
// drag: a page is not a file, nothing outside the window could take it, and an
// OLE drag would bring a drag image and Peel's own wheel along with it.
//
// Every card is a Button, so UI Automation sees each page by name ("Page 3")
// with its bounds, and the scripted test drags a real mouse from one card to
// another. The line at the bottom says what will be written ("Keeping all 3
// pages in the order 3, 1, 2, 1 turned").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Peel.Engine;

namespace Peel.Editors.Pages;

internal partial class PagesEditor : EditorWindowBase
{
    private const double CardHeight = 150;

    private readonly string _id;
    private readonly bool _split;
    private readonly IReadOnlyList<BitmapSource> _thumbs;
    private readonly PagePlan _plan;
    private readonly List<Button> _cards = new();
    private int _selected = -1;
    private int _pressed = -1;
    private Point _pressedAt;
    private bool _dragging;
    private bool _writing;
    private string? _rangeProblem;

    internal PagesEditor(ActionEntry entry, string path, IReadOnlyList<BitmapSource> thumbs, ParamSchema schema)
    {
        InitializeComponent();
        _id = entry.Id;
        _split = entry.Id == "pdf.split";
        _thumbs = thumbs;
        _plan = new PagePlan(thumbs.Count);
        var name = System.IO.Path.GetFileName(path);
        Title = $"{entry.Label} {name}";
        Heading.Text = entry.Label;
        Subject.Text = thumbs.Count == 1 ? $"{name}, 1 page" : $"{name}, {thumbs.Count} pages";
        ApplyButton.Content = entry.Label;

        if (_split)
        {
            RangePanel.Visibility = Visibility.Visible;
            RangeLabel.Text = schema.Label("ranges", "Page ranges");
            RangeField.TextChanged += OnRangesTyped;
            Tool("No cuts", "PagesNoCuts", () =>
            {
                _plan.ClearCuts();
                WriteRanges();
            });
        }
        else
        {
            Tool("Turn left", "PagesTurnLeft", () => _plan.Turn(_selected, -90));
            Tool("Turn right", "PagesTurnRight", () => _plan.Turn(_selected, 90));
            Tool("Move earlier", "PagesEarlier", () => MoveSelected(-1));
            Tool("Move later", "PagesLater", () => MoveSelected(1));
            Tool("Delete", "PagesDelete", DeleteSelected);
            Tool("Undo", "PagesUndo", _plan.Undo);
            Tool("Reset", "PagesReset", _plan.Reset);
            Sheet.PreviewMouseMove += OnDragMove;
            Sheet.PreviewMouseLeftButtonUp += OnDragUp;
            // LostMouseCapture bubbles: the card losing its capture to Sheet,
            // which is how every drag starts, arrives here too. Only Sheet's
            // own loss (Alt+Tab, another window) ends the drag.
            Sheet.LostMouseCapture += (_, e) =>
            {
                if (e.OriginalSource == Sheet)
                {
                    EndDrag();
                }
            };
            PreviewKeyDown += OnKeys;
        }
        Rebuild();
        Describe();
    }

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        if (_split)
        {
            var ranges = RangeField.Text.Trim();
            if (PagePlan.CheckRanges(ranges, _plan.Count, out var problem) < 0)
            {
                Hint.Text = problem;
                return null;
            }
            // Empty is the engine's default (one file per page): send nothing.
            var sent = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ranges.Length > 0)
            {
                sent["ranges"] = ranges;
            }
            return sent;
        }
        if (_plan.OrganizeProblem() is { } refused)
        {
            Hint.Text = refused;
            return null;
        }
        return _plan.ToOrganizeParams();
    }

    // --- the grid -------------------------------------------------------------

    private void Rebuild()
    {
        Cards.Children.Clear();
        _cards.Clear();
        for (var index = 0; index < _plan.Pages.Count; index++)
        {
            var page = _plan.Pages[index];
            var card = Card(page, index);
            _cards.Add(card);
            Cards.Children.Add(card);
            if (_split && page.Source < _plan.Count)
            {
                Cards.Children.Add(CutToggle(page.Source));
            }
        }
        if (_selected >= _cards.Count)
        {
            _selected = _cards.Count - 1;
        }
        Highlight();
    }

    private Button Card(PageItem page, int index)
    {
        var bitmap = _thumbs[page.Source - 1];
        var picture = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxHeight = CardHeight,
            MaxWidth = CardHeight,
            LayoutTransform = new RotateTransform(page.Turn),
            IsHitTestVisible = false,
        };
        var label = page.Turn == 0
            ? $"Page {page.Source}"
            : string.Format(CultureInfo.InvariantCulture, "Page {0}, turned {1}", page.Source, page.Turn);
        var caption = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("EditorText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var body = new StackPanel { Width = CardHeight, IsHitTestVisible = false };
        body.Children.Add(new Border { Height = CardHeight, Child = picture, Background = Brushes.Transparent });
        body.Children.Add(caption);
        var card = new Button
        {
            Content = body,
            Style = (Style)FindResource("EditorButton"),
            Padding = new Thickness(6),
            Margin = new Thickness(6),
            Tag = index,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, $"Page{page.Source}");
        System.Windows.Automation.AutomationProperties.SetName(card, label);
        card.Click += (sender, _) =>
        {
            _selected = (int)((Button)sender).Tag;
            Highlight();
        };
        if (!_split)
        {
            card.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                _pressed = (int)((Button)sender).Tag;
                _pressedAt = e.GetPosition(Sheet);
            };
        }
        return card;
    }

    private ToggleButton CutToggle(int afterPage)
    {
        var toggle = new ToggleButton
        {
            Content = "Cut",
            Style = (Style)FindResource("EditorToggle"),
            MinWidth = 40,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _plan.CutsAfter.Contains(afterPage),
            ToolTip = $"Start a new file after page {afterPage}",
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, $"PagesCutAfter{afterPage}");
        System.Windows.Automation.AutomationProperties.SetName(toggle, $"Cut after page {afterPage}");
        // Checked and Unchecked, not Click: UI Automation's Toggle changes
        // IsChecked without raising Click.
        toggle.Checked += (_, _) => SetCut(afterPage, true);
        toggle.Unchecked += (_, _) => SetCut(afterPage, false);
        return toggle;
    }

    private void SetCut(int afterPage, bool on)
    {
        if (_writing || _plan.CutsAfter.Contains(afterPage) == on)
        {
            return;
        }
        _plan.ToggleCut(afterPage);
        WriteRanges();
    }

    private void Highlight()
    {
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].BorderBrush = i == _selected
                ? (Brush)FindResource("EditorAccent")
                : (Brush)FindResource("EditorEdge");
            _cards[i].BorderThickness = new Thickness(i == _selected ? 2 : 1);
        }
    }

    private void Tool(string label, string automationId, Action act)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("EditorButton") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) =>
        {
            act();
            Rebuild();
            Describe();
        };
        Tools.Children.Add(button);
    }

    // --- organize -------------------------------------------------------------

    private void MoveSelected(int step)
    {
        if (_selected < 0)
        {
            Hint.Text = "Click a page first.";
            return;
        }
        var to = _selected + step;
        if (to < 0 || to >= _plan.Pages.Count)
        {
            return;
        }
        // MoveBefore counts the slot the page leaves, so a step later is two slots on.
        if (_plan.MoveBefore(_selected, step > 0 ? to + 1 : to))
        {
            _selected = to;
        }
    }

    private void DeleteSelected()
    {
        if (_selected < 0)
        {
            Hint.Text = "Click a page first.";
            return;
        }
        if (!_plan.Remove(_selected))
        {
            Hint.Text = "The last page cannot go: a PDF needs at least one.";
        }
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (_selected < 0 || System.Windows.Input.Keyboard.FocusedElement is TextBox)
        {
            return;
        }
        var control = (System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Delete:
                DeleteSelected();
                break;
            case Key.Left when control:
                MoveSelected(-1);
                break;
            case Key.Right when control:
                MoveSelected(1);
                break;
            default:
                return;
        }
        e.Handled = true;
        Rebuild();
        Describe();
        if (_selected >= 0 && _selected < _cards.Count)
        {
            _cards[_selected].Focus();
        }
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_pressed < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var here = e.GetPosition(Sheet);
        if (!_dragging)
        {
            if (Math.Abs(here.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(here.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            _dragging = true;
            _selected = _pressed;
            Highlight();
            Sheet.CaptureMouse();
        }
        ShowMarker(PageGrid.InsertionIndex(here, CardRects()));
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            _pressed = -1;
            return;
        }
        var before = PageGrid.InsertionIndex(e.GetPosition(Sheet), CardRects());
        var from = _pressed;
        // Sheet took the capture from the card when the drag began, so the
        // card raises no Click for this press.
        EndDrag();
        if (_plan.MoveBefore(from, before))
        {
            _selected = before > from ? before - 1 : before;
            Log.Write($"{_id}: page {_plan.Pages[_selected].Source} dragged to place {_selected + 1}");
        }
        Rebuild();
        Describe();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            if (Sheet.IsMouseCaptured)
            {
                Sheet.ReleaseMouseCapture();
            }
        }
        _pressed = -1;
        Marks.Children.Clear();
    }

    private List<Rect> CardRects() => _cards
        .Select(card => new Rect(card.TranslatePoint(new Point(0, 0), Sheet), new Size(card.ActualWidth, card.ActualHeight)))
        .ToList();

    private void ShowMarker(int before)
    {
        Marks.Children.Clear();
        var rects = CardRects();
        if (rects.Count == 0)
        {
            return;
        }
        var anchor = before < rects.Count ? rects[before] : rects[^1];
        var x = before < rects.Count ? anchor.Left - 4 : anchor.Right + 2;
        var marker = new Rectangle
        {
            Width = 3,
            Height = anchor.Height,
            Fill = (Brush)FindResource("EditorAccent"),
        };
        Canvas.SetLeft(marker, x);
        Canvas.SetTop(marker, anchor.Top);
        Marks.Children.Add(marker);
    }

    // --- split ----------------------------------------------------------------

    private void WriteRanges()
    {
        _writing = true;
        try
        {
            RangeField.Text = _plan.RangesFromCuts();
            _rangeProblem = null;
        }
        finally
        {
            _writing = false;
        }
        Describe();
    }

    private void OnRangesTyped(object sender, TextChangedEventArgs e)
    {
        if (_writing)
        {
            return;
        }
        // Typed ranges are what is sent, so the cut toggles no longer say
        // anything: clear them rather than show cuts the text overrides.
        _plan.ClearCuts();
        _writing = true;
        try
        {
            foreach (var toggle in Cards.Children.OfType<ToggleButton>())
            {
                toggle.IsChecked = false;
            }
        }
        finally
        {
            _writing = false;
        }
        _rangeProblem = PagePlan.CheckRanges(RangeField.Text, _plan.Count, out var problem) < 0 ? problem : null;
        Describe();
    }

    private void Describe()
    {
        if (_split)
        {
            if (_rangeProblem is not null)
            {
                Hint.Text = _rangeProblem;
                return;
            }
            var files = PagePlan.CheckRanges(RangeField.Text, _plan.Count, out _);
            Hint.Text = $"{PagePlan.DescribeSplit(RangeField.Text, files)}. Enter splits, Escape cancels.";
            return;
        }
        Hint.Text = $"{_plan.DescribeOrganize()}. Enter saves, Escape cancels.";
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
